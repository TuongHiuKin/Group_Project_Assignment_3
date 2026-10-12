using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
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
    private const int DefaultMaxTurnsAllowed = 2;
    private const int MaxAudioSizeBytes = 25 * 1024 * 1024; // 25 MB

    private readonly IVivaSessionRepository _sessionRepo;
    private readonly IVivaQuestionRepository _questionRepo;
    private readonly IVivaTurnRepository _turnRepo;
    private readonly ISpeechToTextService _sttService;
    private readonly ITextToSpeechService _ttsService;
    private readonly IFollowUpGeneratorService _followUpService;
    private readonly IVivaRealtimePublisher _publisher;
    private readonly ILogger<VivaSessionOrchestrator> _logger;

    public VivaSessionOrchestrator(
        IVivaSessionRepository sessionRepo,
        IVivaQuestionRepository questionRepo,
        IVivaTurnRepository turnRepo,
        ISpeechToTextService sttService,
        ITextToSpeechService ttsService,
        IFollowUpGeneratorService followUpService,
        IVivaRealtimePublisher publisher,
        ILogger<VivaSessionOrchestrator> logger)
    {
        _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
        _questionRepo = questionRepo ?? throw new ArgumentNullException(nameof(questionRepo));
        _turnRepo = turnRepo ?? throw new ArgumentNullException(nameof(turnRepo));
        _sttService = sttService ?? throw new ArgumentNullException(nameof(sttService));
        _ttsService = ttsService ?? throw new ArgumentNullException(nameof(ttsService));
        _followUpService = followUpService ?? throw new ArgumentNullException(nameof(followUpService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        var statusDto = new VivaSessionStatusDto(
            SessionId: session.SessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.Created,
            CurrentTurnOrdinal: 0,
            MaxTurnsAllowed: DefaultMaxTurnsAllowed,
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
        _logger.LogInformation("Starting viva session {SessionId}", sessionId);

        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        if (session.State == (byte)VivaSessionState.Completed || session.State == (byte)VivaSessionState.Failed)
        {
            throw new InvalidOperationException($"Cannot start session {sessionId} in state {(VivaSessionState)session.State}.");
        }

        var questions = await _questionRepo.GetQuestionsByExamAsync(session.ExamId, ct);
        if (questions == null || questions.Count == 0)
        {
            _logger.LogError("No questions found for Exam {ExamId}", session.ExamId);
            throw new InvalidOperationException($"No questions available for Exam '{session.ExamId}'.");
        }

        var initialQuestion = questions[0];

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
            MaxTurnsAllowed: DefaultMaxTurnsAllowed,
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

        var session = await _sessionRepo.GetByIdAsync(submission.SessionId, ct)
            ?? throw new KeyNotFoundException($"Session {submission.SessionId} was not found.");

        // 1. Persistence First: Update state to Transcribing
        await _sessionRepo.UpdateStateAsync(submission.SessionId, (byte)VivaSessionState.Transcribing, ct);
        await _publisher.PublishStatusChangedAsync(new VivaSessionStatusDto(
            SessionId: submission.SessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.Transcribing,
            CurrentTurnOrdinal: 1,
            MaxTurnsAllowed: DefaultMaxTurnsAllowed,
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
            _logger.LogError("STT processing failed for Session: {SessionId}. Error: {Error}", submission.SessionId, sttResult.ErrorMessage);
            await _publisher.PublishErrorAsync(new VivaErrorDto(
                SessionId: submission.SessionId,
                ErrorCode: "STT_PROCESSING_FAILED",
                ErrorMessage: sttResult.ErrorMessage ?? "Speech-to-text transcription failed.",
                CanRetry: true
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
            QuestionId = submission.QuestionId,
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
            MaxTurnsAllowed: DefaultMaxTurnsAllowed,
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

        // Rule: Follow-Up Count Limit (Max 1-2 turns)
        var turnCount = await _turnRepo.GetTurnCountForSessionAsync(sessionId, ct);
        if (turnCount >= DefaultMaxTurnsAllowed)
        {
            _logger.LogInformation("Session {SessionId} reached maximum allowed turns ({MaxTurns}). Concluding session.",
                sessionId, DefaultMaxTurnsAllowed);
            await CompleteSessionAsync(sessionId, ct);
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
            MaxTurnsAllowed: DefaultMaxTurnsAllowed,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Generating follow-up question..."
        ), ct);

        // 2. LLM Follow-Up Generation
        var followUpResult = await _followUpService.GenerateFollowUpQuestionAsync(
            originalQuestion: originalQuestionContent,
            studentAnswerTranscript: turn.TranscriptText ?? string.Empty,
            subjectContext: session.ExamId,
            ct: ct
        );

        if (!followUpResult.IsSuccess || string.IsNullOrWhiteSpace(followUpResult.FollowUpQuestionText))
        {
            _logger.LogWarning("Follow-up generation skipped or returned empty. Completing session {SessionId}.", sessionId);
            await CompleteSessionAsync(sessionId, ct);
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
            MaxTurnsAllowed: DefaultMaxTurnsAllowed,
            UpdatedAtUtc: DateTime.UtcNow,
            Message: "Follow-up question delivered."
        ), ct);

        return followUpDto;
    }

    /// <inheritdoc />
    public async Task<VivaSessionStatusDto> CompleteSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        _logger.LogInformation("Completing viva session {SessionId}", sessionId);

        var session = await _sessionRepo.GetByIdAsync(sessionId, ct)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        var completedAt = DateTime.UtcNow;

        // 1. Persistence First
        await _sessionRepo.CompleteSessionAsync(sessionId, completedAt, ct);

        var turnCount = await _turnRepo.GetTurnCountForSessionAsync(sessionId, ct);

        // 2. Realtime Broadcast: SessionCompleted and StatusChanged
        await _publisher.PublishSessionCompletedAsync(sessionId, session.ExamId, session.StudentId, ct);

        var statusDto = new VivaSessionStatusDto(
            SessionId: sessionId,
            ExamId: session.ExamId,
            StudentId: session.StudentId,
            State: VivaSessionState.Completed,
            CurrentTurnOrdinal: turnCount,
            MaxTurnsAllowed: DefaultMaxTurnsAllowed,
            UpdatedAtUtc: completedAt,
            Message: "Session completed successfully."
        );
        await _publisher.PublishStatusChangedAsync(statusDto, ct);

        return statusDto;
    }
}
