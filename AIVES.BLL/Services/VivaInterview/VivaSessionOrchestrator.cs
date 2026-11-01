using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;

namespace AIVES.BLL.Services.VivaInterview;

/// <summary>
/// Core orchestrator for the viva examination session lifecycle.
/// Enforces business limits (follow-up caps), persistence-first rule, and coordinates STT, TTS, LLM, and realtime events.
/// </summary>
public class VivaSessionOrchestrator : IVivaSessionOrchestrator
{
    /// <summary>Each question gets the primary answer plus one follow-up; then the exam moves to the next question.</summary>
    public const int MaxTurnsPerQuestion = 2;
    private const int MaxAudioSizeBytes = 25 * 1024 * 1024; // 25 MB

    /// <summary>Total turns a session may take: every question of the exam times <see cref="MaxTurnsPerQuestion"/>.</summary>
    public static int SessionMaxTurns(int questionCount) => Math.Max(1, questionCount) * MaxTurnsPerQuestion;

    private async Task<int> GetSessionMaxTurnsAsync(string examId, CancellationToken ct) =>
        SessionMaxTurns((await _questionRepo.GetQuestionsByExamAsync(examId, ct))?.Count ?? 0);

    private readonly IVivaSessionRepository _sessionRepo;
    private readonly IVivaQuestionRepository _questionRepo;
    private readonly IVivaTurnRepository _turnRepo;
    private readonly ISpeechToTextService _sttService;
    private readonly ITextToSpeechService _ttsService;
    private readonly IFollowUpGeneratorService _followUpService;
    private readonly IVivaRealtimePublisher _publisher;
    private readonly ILogger<VivaSessionOrchestrator> _logger;
    private readonly VivaTimerManager? _timers;
    private readonly IGradingQueue? _gradingQueue;

    public VivaSessionOrchestrator(
        IVivaSessionRepository sessionRepo,
        IVivaQuestionRepository questionRepo,
        IVivaTurnRepository turnRepo,
        ISpeechToTextService sttService,
        ITextToSpeechService ttsService,
        IFollowUpGeneratorService followUpService,
        IVivaRealtimePublisher publisher,
        ILogger<VivaSessionOrchestrator> logger,
        VivaTimerManager? timers = null,
        IGradingQueue? gradingQueue = null)
    {
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _questionRepo = questionRepo ?? throw new ArgumentNullException(nameof(questionRepo));
        _turnRepo = turnRepo ?? throw new ArgumentNullException(nameof(turnRepo));
        _sttService = sttService ?? throw new ArgumentNullException(nameof(sttService));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        _followUpService = followUpService ?? throw new ArgumentNullException(nameof(followUpService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timers = timers;
        _gradingQueue = gradingQueue;
    }

    /// <inheritdoc />
    public async Task<VivaSessionStatusDto> InitializeSessionAsync(string examId, string studentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(examId))
            throw new ArgumentException("Exam ID must be provided.", nameof(examId));
        if (string.IsNullOrWhiteSpace(studentId))
            throw new ArgumentException("Student ID must be provided.", nameof(studentId));

        _logger.LogInformation("Initializing new viva session for Exam: {ExamId}, Student: {StudentId}", examId, studentId);

        var session = new VivaSession
        {
            SessionId = Guid.NewGuid(),
            ExamId = examId,
            StudentId = studentId,
            State = (byte)VivaSessionState.Created,
            StartedAtUtc = DateTime.UtcNow
        };

        // 1. Persistence First
        await _sessionRepo.CreateSessionAsync(session, ct);
        var maxTurns = await GetSessionMaxTurnsAsync(examId, ct);

        var statusDto = new VivaSessionStatusDto(
            SessionId: session.SessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.Created,
            CurrentTurnOrdinal: 0,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Session created successfully."
        );

        // 2. Realtime Broadcast
        await _publisher.PublishStatusChangedAsync(statusDto, ct);

        return statusDto;
    }

    /// <inheritdoc />
    public async Task<bool> AuthorizeUserForSessionAsync(Guid sessionId, string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        var session = await _sessionRepo.GetByIdAsync(sessionId, ct);
        if (session == null)
        {
            _logger.LogWarning("Authorization check failed: Session {SessionId} not found.", sessionId);
            return false;
        }

        return string.Equals(session.StudentId, userId, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async Task<VivaQuestionDto> StartSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var lease = _timers is null ? null : await _timers.LockSessionAsync(sessionId, ct);
        _logger.LogInformation("Starting viva session {SessionId}", sessionId);

        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        if (session.State is not ((byte)VivaSessionState.Created) and not ((byte)VivaSessionState.Ready))
        {
            throw new InvalidOperationException($"Cannot start session {sessionId} in state {(VivaSessionState)session.State}.");
        }

        var questions = await _questionRepo.GetQuestionsByExamAsync(session.ExamId, ct);
        if (questions == null || questions.Count == 0)
        {
            _logger.LogError("No questions found for Exam {ExamId}", session.ExamId);
            throw new InvalidOperationException($"No questions available for Exam '{session.ExamId}'.");
        }

        var initialQuestion = questions.OrderBy(q => q.QuestionId, StringComparer.Ordinal).First();
        var maxTurns = SessionMaxTurns(questions.Count);

        // Synthesize TTS audio if not already available
        string? audioUrl = initialQuestion.AudioUrl;
        if (string.IsNullOrEmpty(audioUrl))
        {
            try
            {
                var ttsResult = await _ttsService.SynthesizeAsync(initialQuestion.Content, "vi-VN", ct);
                if (ttsResult.IsSuccess && !string.IsNullOrEmpty(ttsResult.AudioUrl))
                {
                    audioUrl = ttsResult.AudioUrl;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TTS generation failed for initial question {QuestionId}, continuing without audio URL.", initialQuestion.QuestionId);
            }
        }

        // 1. Persistence First: Transition to QuestionDelivered
        await _sessionRepo.UpdateStateAsync(sessionId, (byte)VivaSessionState.QuestionDelivered, ct);

        var questionDto = new VivaQuestionDto(
            QuestionId: initialQuestion.QuestionId,
            ExamId: initialQuestion.ExamId,
            Title: initialQuestion.Title,
            Content: initialQuestion.Content,
            TimeLimitSeconds: initialQuestion.TimeLimitSeconds,
            AudioUrl: audioUrl
        );

        // 2. Realtime Broadcast
        var statusDto = new VivaSessionStatusDto(
            SessionId: sessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.QuestionDelivered,
            CurrentTurnOrdinal: 1,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: $"Question {initialQuestion.QuestionId} delivered."
        );
        await _publisher.PublishStatusChangedAsync(statusDto, ct);

        return questionDto;
    }

    /// <inheritdoc />
    public async Task<VivaTranscriptDto> SubmitAnswerAudioAsync(VivaAnswerSubmissionDto submission, CancellationToken ct = default)
    {
        if (submission == null)
            throw new ArgumentNullException(nameof(submission));
        if (submission.AudioData == null || submission.AudioData.Length == 0)
            throw new ArgumentException("Audio data must not be empty.", nameof(submission));
        if (submission.AudioData.Length > MaxAudioSizeBytes)
            throw new ArgumentException($"Audio payload exceeds maximum allowed limit of {MaxAudioSizeBytes} bytes.", nameof(submission));

        _logger.LogInformation("Processing answer audio submission for Session: {SessionId}, Question: {QuestionId}, Bytes: {ByteCount}",
            submission.SessionId, submission.QuestionId, submission.AudioData.Length);

        VivaSession session;
        VivaTimerManager.Deadline? acceptedDeadline = null;
        var secondsLeftAtSubmit = 0;
        var persistedQuestionId = submission.QuestionId;
        // Serialize the accepting transition with timeout. Release before the slow STT call.
        using (var lease = _timers is null ? null : await _timers.LockSessionAsync(submission.SessionId, ct))
        {
            session = await _sessionRepo.GetByIdAsync(submission.SessionId, ct)
                ?? throw new KeyNotFoundException($"Session {submission.SessionId} was not found.");
            if (_timers is not null)
            {
                var deadline = _timers.GetDeadline(submission.SessionId);
                acceptedDeadline = deadline;
                if (session.State != (byte)VivaSessionState.Answering || deadline is null ||
                    _timers.GetRemaining(submission.SessionId)!.SecondsRemaining == 0)
                    throw new InvalidOperationException("The answering period is not active or has expired.");
                if (!string.Equals(deadline.QuestionId, submission.QuestionId, StringComparison.Ordinal))
                    throw new ArgumentException("The answer does not match the active question.", nameof(submission));
                // The clock is paused while STT runs; a retry gets back exactly the time left at submission.
                secondsLeftAtSubmit = _timers.SecondsRemaining(deadline);
                // Follow-ups are stored on a turn; preserve the FK to the primary question.
                var details = await _sessionRepo.GetWithDetailsAsync(submission.SessionId, ct)
                    ?? throw new KeyNotFoundException("Examination session was not found.");
                var latest = details.Turns.OrderByDescending(t => t.TurnOrdinal).FirstOrDefault();
                if (latest?.FollowUpQuestionText is { Length: > 0 }) persistedQuestionId = latest.QuestionId;
            }
            // Only stop the timer after the answering transition is persisted.
            await _sessionRepo.UpdateStateAsync(submission.SessionId, (byte)VivaSessionState.Transcribing, ct);
            _timers?.Stop(submission.SessionId);
        }
        var maxTurns = await GetSessionMaxTurnsAsync(session.ExamId, ct);
        await _publisher.PublishStatusChangedAsync(new VivaSessionStatusDto(
            SessionId: submission.SessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.Transcribing,
            CurrentTurnOrdinal: 1,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Transcribing student audio..."
        ), ct);

        // 2. Execute STT
        SpeechToTextResult sttResult;
        using (var audioStream = new MemoryStream(submission.AudioData))
        {
            sttResult = await _sttService.TranscribeAsync(audioStream, submission.ContentType, ct);
        }

        if (!sttResult.IsSuccess)
        {
            var canRetry = true;
            if (_timers is not null && acceptedDeadline is not null)
            {
                using var lease = await _timers.LockSessionAsync(submission.SessionId, ct);
                var current = await _sessionRepo.GetByIdAsync(submission.SessionId, ct)
                    ?? throw new KeyNotFoundException("Examination session was not found.");
                canRetry = current.State == (byte)VivaSessionState.Transcribing && secondsLeftAtSubmit > 0;
                var retryState = canRetry ? VivaSessionState.Answering : VivaSessionState.TimedOut;
                if (current.State == (byte)VivaSessionState.Transcribing)
                {
                    await _sessionRepo.UpdateStateAsync(submission.SessionId, (byte)retryState, ct);
                    if (canRetry) _timers.Resume(acceptedDeadline, secondsLeftAtSubmit);
                    await _publisher.PublishStatusChangedAsync(new(submission.SessionId, session.ExamId, session.StudentId,
                        retryState, 1, maxTurns, DateTime.UtcNow), ct);
                }
            }
            _logger.LogError("STT processing failed for Session: {SessionId}. Error: {Error}", submission.SessionId, sttResult.ErrorMessage);
            var friendlyError = string.IsNullOrWhiteSpace(sttResult.ErrorMessage)
                ? "Không thể nhận diện giọng nói. Vui lòng kiểm tra cấu hình AI hoặc thử ghi âm lại."
                : $"Lỗi nhận diện giọng nói AI: {sttResult.ErrorMessage}";
            await _publisher.PublishErrorAsync(new VivaErrorDto(
                SessionId: submission.SessionId,
                ErrorCode: "STT_PROCESSING_FAILED",
                ErrorMessage: friendlyError,
                CanRetry: canRetry
            ), ct);

            throw new InvalidOperationException($"Speech-to-text processing failed: {sttResult.ErrorMessage}");
        }

        // 3. Persistence First: Create turn record with transcript
        var currentTurns = await _turnRepo.GetTurnCountForSessionAsync(submission.SessionId, ct);
        var turnOrdinal = currentTurns + 1;

        var turn = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = submission.SessionId,
            QuestionId = persistedQuestionId,
            TurnOrdinal = turnOrdinal,
            TranscriptText = sttResult.TranscriptText,
            ConfidenceScore = sttResult.ConfidenceScore,
            CreatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow
        };

        await _turnRepo.CreateTurnAsync(turn, ct);
        await _sessionRepo.UpdateStateAsync(submission.SessionId, (byte)VivaSessionState.TranscriptReady, ct);

        // 4. Realtime Broadcast
        var transcriptDto = new VivaTranscriptDto(
            SessionId: submission.SessionId,
            TurnId: turn.TurnId,
            QuestionId: submission.QuestionId,
            TranscriptText: sttResult.TranscriptText,
            ConfidenceScore: sttResult.ConfidenceScore,
            CompletedAtUtc: DateTime.UtcNow
        );

        await _publisher.PublishTranscriptReadyAsync(transcriptDto, ct);
        await _publisher.PublishStatusChangedAsync(new VivaSessionStatusDto(
            SessionId: submission.SessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.TranscriptReady,
            CurrentTurnOrdinal: turnOrdinal,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Transcript ready."
        ), ct);

        return transcriptDto;
    }

    /// <inheritdoc />
    public async Task<VivaFollowUpDto?> GenerateFollowUpAsync(Guid sessionId, Guid turnId, CancellationToken ct = default)
    {
        _logger.LogInformation("Evaluating follow-up generation for Session: {SessionId}, Turn: {TurnId}", sessionId, turnId);

        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        var turn = await _turnRepo.GetByIdAsync(turnId, ct)
            ?? throw new KeyNotFoundException($"Turn {turnId} was not found.");
        if (turn.SessionId != sessionId)
            throw new InvalidOperationException("Turn does not belong to this examination session.");

        var turnCount = await _turnRepo.GetTurnCountForSessionAsync(sessionId, ct);
        var questions = (await _questionRepo.GetQuestionsByExamAsync(session.ExamId, ct) ?? new())
            .OrderBy(q => q.QuestionId, StringComparer.Ordinal).ToList();
        var maxTurns = SessionMaxTurns(questions.Count);

        // Rule: Follow-Up Count Limit per question (primary answer + 1 follow-up), then the next question.
        var details = await _sessionRepo.GetWithDetailsAsync(sessionId, ct);
        var questionTurns = details?.Turns.Count(t => t.QuestionId == turn.QuestionId) ?? 0;
        if (questionTurns >= MaxTurnsPerQuestion)
        {
            _logger.LogInformation("Session {SessionId} reached {MaxTurns} turns for question {QuestionId}.",
                sessionId, MaxTurnsPerQuestion, turn.QuestionId);
            await AdvanceToNextQuestionAsync(session, turn.QuestionId, questions, turnCount, maxTurns, ct);
            return null;
        }

        var question = await _questionRepo.GetByIdAsync(turn.QuestionId, ct);
        var originalQuestionContent = question?.Content ?? "Viva primary question";

        // 1. Persistence First: State transition to FollowUpGenerating
        await _sessionRepo.UpdateStateAsync(sessionId, (byte)VivaSessionState.FollowUpGenerating, ct);
        await _publisher.PublishStatusChangedAsync(new VivaSessionStatusDto(
            SessionId: sessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.FollowUpGenerating,
            CurrentTurnOrdinal: turnCount,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Generating follow-up question..."
        ), ct);

        // 2. LLM Follow-Up Generation
        FollowUpResult followUpResult;
        try
        {
            followUpResult = await _followUpService.GenerateFollowUpQuestionAsync(
                originalQuestion: originalQuestionContent,
                studentAnswerTranscript: turn.TranscriptText ?? string.Empty,
                subjectContext: session.ExamId,
                ct: ct
            );
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Follow-up generation threw for Session {SessionId}.", sessionId);
            followUpResult = new FollowUpResult(false, string.Empty, string.Empty, "Lỗi không xác định khi gọi dịch vụ câu hỏi phụ.");
        }

        if (!followUpResult.IsSuccess)
        {
            // Infrastructure failure (429/503, timeout, unavailable provider): keep the session open and let the
            // student retry or finish instead of silently completing at turn 1/2.
            await ReportFollowUpFailureAsync(session, turnCount, maxTurns, followUpResult.ErrorMessage, ct);
            return null;
        }

        if (string.IsNullOrWhiteSpace(followUpResult.FollowUpQuestionText))
        {
            _logger.LogInformation("Follow-up generator returned no question for session {SessionId}.", sessionId);
            await AdvanceToNextQuestionAsync(session, turn.QuestionId, questions, turnCount, maxTurns, ct);
            return null;
        }

        // 3. Synthesize TTS for the Follow-Up question
        string? audioUrl = null;
        try
        {
            var ttsResult = await _ttsService.SynthesizeAsync(followUpResult.FollowUpQuestionText, "vi-VN", ct);
            if (ttsResult.IsSuccess)
            {
                audioUrl = ttsResult.AudioUrl;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS generation failed for follow-up question. Proceeding without audio URL.");
        }

        // 4. Persistence First: Save follow-up question to turn & update state
        await _turnRepo.SaveFollowUpAsync(turnId, followUpResult.FollowUpQuestionText, audioUrl, ct);
        await _sessionRepo.UpdateStateAsync(sessionId, (byte)VivaSessionState.FollowUpDelivered, ct);

        // 5. Realtime Broadcast
        var followUpOrdinal = turnCount;
        var followUpDto = new VivaFollowUpDto(
            SessionId: sessionId,
            TurnId: turnId,
            FollowUpQuestionId: $"{turn.QuestionId}-FU{followUpOrdinal}",
            FollowUpContent: followUpResult.FollowUpQuestionText,
            FollowUpOrdinal: followUpOrdinal,
            AudioUrl: audioUrl
        );

        await _publisher.PublishFollowUpGeneratedAsync(followUpDto, ct);
        await _publisher.PublishStatusChangedAsync(new VivaSessionStatusDto(
            SessionId: sessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.FollowUpDelivered,
            CurrentTurnOrdinal: followUpOrdinal,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Follow-up question delivered."
        ), ct);

        return followUpDto;
    }

    /// <summary>
    /// Delivers the exam's next question (ordered by QuestionId), or completes the session after the last one.
    /// The client reloads the snapshot on QuestionDelivered to read the new question.
    /// </summary>
    private async Task AdvanceToNextQuestionAsync(VivaSession session, string currentQuestionId,
        IReadOnlyList<VivaQuestion> orderedQuestions, int turnCount, int maxTurns, CancellationToken ct)
    {
        var next = orderedQuestions.FirstOrDefault(q => string.CompareOrdinal(q.QuestionId, currentQuestionId) > 0);
        if (next is null)
        {
            _logger.LogInformation("Session {SessionId} answered the last question. Concluding session.", session.SessionId);
            await CompleteSessionAsync(session.SessionId, ct);
            return;
        }

        // 1. Persistence First
        await _sessionRepo.UpdateStateAsync(session.SessionId, (byte)VivaSessionState.QuestionDelivered, ct);

        // 2. Realtime Broadcast
        await _publisher.PublishStatusChangedAsync(new VivaSessionStatusDto(
            SessionId: session.SessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.QuestionDelivered,
            CurrentTurnOrdinal: turnCount,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: $"Question {next.QuestionId} delivered."
        ), ct);
    }

    public const string FollowUpFailedErrorCode = "FOLLOWUP_GENERATION_FAILED";

    private async Task ReportFollowUpFailureAsync(VivaSession session, int turnCount, int maxTurns, string? providerError, CancellationToken ct)
    {
        _logger.LogWarning("Follow-up generation failed for Session {SessionId}: {Error}. Session kept open for retry.",
            session.SessionId, providerError);

        // Persistence First: return to TranscriptReady so the student can retry or finish.
        await _sessionRepo.UpdateStateAsync(session.SessionId, (byte)VivaSessionState.TranscriptReady, ct);
        await _publisher.PublishStatusChangedAsync(new VivaSessionStatusDto(
            SessionId: session.SessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.TranscriptReady,
            CurrentTurnOrdinal: turnCount,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Follow-up generation failed."
        ), ct);

        await _publisher.PublishErrorAsync(new VivaErrorDto(
            SessionId: session.SessionId,
            ErrorCode: FollowUpFailedErrorCode,
            ErrorMessage: BuildFollowUpFailureMessage(providerError),
            CanRetry: true
        ), ct);
    }

    public static string BuildFollowUpFailureMessage(string? providerError)
    {
        var error = providerError ?? string.Empty;
        string reason;
        if (error.Contains("503", StringComparison.Ordinal) || error.Contains("429", StringComparison.Ordinal) ||
            error.Contains("high demand", StringComparison.OrdinalIgnoreCase) || error.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
        {
            reason = "máy chủ AI đang quá tải";
        }
        else if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase) || error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            reason = "máy chủ AI phản hồi quá thời gian chờ";
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            reason = error.Trim().TrimEnd('.');
        }
        else
        {
            reason = "dịch vụ câu hỏi phụ không phản hồi";
        }

        return $"Giám khảo AI chưa tạo được câu hỏi phụ ({reason}). Bạn có thể bấm Thử lại hoặc Kết thúc bài thi.";
    }

    /// <inheritdoc />
    public async Task<VivaSessionStatusDto> CompleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        using var lease = _timers is null ? null : await _timers.LockSessionAsync(sessionId, ct);
        _logger.LogInformation("Completing viva session {SessionId}", sessionId);

        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        if (session.State is (byte)VivaSessionState.TimedOut or (byte)VivaSessionState.Failed)
            throw new InvalidOperationException("Cannot complete an expired or failed examination session.");

        var completedAt = DateTime.UtcNow;

        // 1. Persistence First
        await _sessionRepo.CompleteSessionAsync(sessionId, completedAt, ct);
        _timers?.Stop(sessionId);

        // G4-RT-01: queue AI grading only after the completion is committed. Non-blocking; if it cannot be
        // queued, startup recovery (G4-RT-04) picks the session up because it has no grading results yet.
        EnqueueGrading(sessionId, session.ExamId);

        var turnCount = await _turnRepo.GetTurnCountForSessionAsync(sessionId, ct);
        var maxTurns = await GetSessionMaxTurnsAsync(session.ExamId, ct);

        // 2. Realtime Broadcast: SessionCompleted and StatusChanged
        await _publisher.PublishSessionCompletedAsync(sessionId, session.ExamId, session.StudentId, ct);

        var statusDto = new VivaSessionStatusDto(
            SessionId: sessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.Completed,
            CurrentTurnOrdinal: turnCount,
            MaxTurnsAllowed: maxTurns,
            UpdatedAtUtc: completedAt,
            Message: "Session completed successfully."
        );
        await _publisher.PublishStatusChangedAsync(statusDto, ct);

        return statusDto;
    }

    private void EnqueueGrading(Guid sessionId, string examId)
    {
        if (_gradingQueue is null) return;
        try
        {
            if (!_gradingQueue.TryEnqueue(new GradingJob(sessionId, ExamId: examId)))
                _logger.LogInformation("Grading for session {SessionId} is already queued or the queue is full.", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue grading for session {SessionId}; recovery will retry.", sessionId);
        }
    }
}
