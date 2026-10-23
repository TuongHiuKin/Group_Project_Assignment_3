using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;

namespace AIVES.Tests.VivaInterview;

public class VivaSessionOrchestratorTests
{
    #region Test Doubles

    private sealed class FakeVivaSessionRepository : IVivaSessionRepository
    {
        public readonly Dictionary<Guid, VivaSession> Sessions = new();

        public Task<VivaSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default)
        {
            Sessions.TryGetValue(sessionId, out var session);
            return Task.FromResult(session);
        }

        public Task<VivaSession?> GetWithDetailsAsync(Guid sessionId, CancellationToken ct = default)
        {
            return GetByIdAsync(sessionId, ct);
        }

        public Task<List<VivaSession>> GetSessionsWithDetailsAsync(string? examId = null, string? studentId = null, CancellationToken ct = default)
        {
            var query = Sessions.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(examId)) query = query.Where(s => s.ExamId == examId);
            if (!string.IsNullOrWhiteSpace(studentId)) query = query.Where(s => s.StudentId == studentId);
            return Task.FromResult(query.OrderBy(s => s.StartedAtUtc).ToList());
        }

        public Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(Sessions.Values.Select(s => s.ExamId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList());
        }

        public Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default)
        {
            Sessions[session.SessionId] = session;
            return Task.FromResult(session.SessionId);
        }

        public Task UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default)
        {
            if (Sessions.TryGetValue(sessionId, out var session))
            {
                session.State = state;
            }
            return Task.CompletedTask;
        }

        public Task CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default)
        {
            if (Sessions.TryGetValue(sessionId, out var session))
            {
                session.State = (byte)VivaSessionState.Completed;
                session.CompletedAtUtc = completedAtUtc;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVivaQuestionRepository : IVivaQuestionRepository
    {
        public readonly List<VivaQuestion> Questions = new();

        public Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default)
        {
            var result = Questions.Where(q => q.ExamId == examId).ToList();
            return Task.FromResult(result);
        }

        public Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default)
        {
            var result = Questions.Select(q => q.ExamId).Distinct().OrderBy(x => x).ToList();
            return Task.FromResult(result);
        }

        public Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default)
        {
            var result = Questions.FirstOrDefault(q => q.QuestionId == questionId);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeVivaTurnRepository : IVivaTurnRepository
    {
        public readonly Dictionary<Guid, VivaSessionTurn> Turns = new();

        public Task<Guid> CreateTurnAsync(VivaSessionTurn turn, CancellationToken ct = default)
        {
            Turns[turn.TurnId] = turn;
            return Task.FromResult(turn.TurnId);
        }

        public Task<VivaSessionTurn?> GetByIdAsync(Guid turnId, CancellationToken ct = default)
        {
            Turns.TryGetValue(turnId, out var turn);
            return Task.FromResult(turn);
        }

        public Task SaveTranscriptAsync(Guid turnId, string transcript, decimal confidence, CancellationToken ct = default)
        {
            if (Turns.TryGetValue(turnId, out var turn))
            {
                turn.TranscriptText = transcript;
                turn.ConfidenceScore = confidence;
            }
            return Task.CompletedTask;
        }

        public Task SaveFollowUpAsync(Guid turnId, string followUpQuestion, string? audioPath, CancellationToken ct = default)
        {
            if (Turns.TryGetValue(turnId, out var turn))
            {
                turn.FollowUpQuestionText = followUpQuestion;
                turn.FollowUpAudioUrl = audioPath;
            }
            return Task.CompletedTask;
        }

        public Task<int> GetTurnCountForSessionAsync(Guid sessionId, CancellationToken ct = default)
        {
            var count = Turns.Values.Count(t => t.SessionId == sessionId);
            return Task.FromResult(count);
        }
    }

    private sealed class FakeSpeechToTextService : ISpeechToTextService
    {
        public bool ShouldSucceed { get; set; } = true;
        public string ResultTranscript { get; set; } = "Scoped DbContext is instantiated once per HTTP request.";
        public decimal Confidence { get; set; } = 0.95m;
        public string? ErrorMessage { get; set; }

        public Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default)
        {
            if (!ShouldSucceed)
            {
                return Task.FromResult(new SpeechToTextResult(false, string.Empty, 0m, ErrorMessage ?? "STT transcription failed"));
            }

            return Task.FromResult(new SpeechToTextResult(true, ResultTranscript, Confidence));
        }
    }

    private sealed class FakeTextToSpeechService : ITextToSpeechService
    {
        public bool ShouldSucceed { get; set; } = true;
        public string AudioUrl { get; set; } = "/audio/sample.mp3";

        public Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default)
        {
            if (!ShouldSucceed)
            {
                return Task.FromResult(new TextToSpeechResult(false, null, null, "TTS error"));
            }
            return Task.FromResult(new TextToSpeechResult(true, AudioUrl, new byte[] { 1, 2, 3 }));
        }
    }

    private sealed class FakeFollowUpGeneratorService : IFollowUpGeneratorService
    {
        public bool ShouldSucceed { get; set; } = true;
        public bool ShouldThrow { get; set; }
        public string ErrorMessage { get; set; } = "LLM failed";
        public string FollowUpText { get; set; } = "Can you elaborate on how DbContext behaves inside a singleton?";

        public Task<FollowUpResult> GenerateFollowUpQuestionAsync(string originalQuestion, string studentAnswerTranscript, string subjectContext, CancellationToken ct = default)
        {
            if (ShouldThrow)
            {
                throw new HttpRequestException("socket closed");
            }
            if (!ShouldSucceed)
            {
                return Task.FromResult(new FollowUpResult(false, string.Empty, string.Empty, ErrorMessage));
            }
            return Task.FromResult(new FollowUpResult(true, FollowUpText, "Deep-Dive"));
        }
    }

    private sealed class FakeVivaRealtimePublisher : IVivaRealtimePublisher
    {
        public readonly List<VivaSessionStatusDto> PublishedStatuses = new();
        public readonly List<VivaTranscriptDto> PublishedTranscripts = new();
        public readonly List<VivaFollowUpDto> PublishedFollowUps = new();
        public readonly List<VivaTimeRemainingDto> PublishedTimers = new();
        public readonly List<(Guid SessionId, string ExamId, string StudentId)> PublishedCompleted = new();
        public readonly List<VivaErrorDto> PublishedErrors = new();

        public Task PublishStatusChangedAsync(VivaSessionStatusDto status, CancellationToken ct = default)
        {
            PublishedStatuses.Add(status);
            return Task.CompletedTask;
        }

        public Task PublishTranscriptReadyAsync(VivaTranscriptDto transcript, CancellationToken ct = default)
        {
            PublishedTranscripts.Add(transcript);
            return Task.CompletedTask;
        }

        public Task PublishFollowUpGeneratedAsync(VivaFollowUpDto followUp, CancellationToken ct = default)
        {
            PublishedFollowUps.Add(followUp);
            return Task.CompletedTask;
        }

        public Task PublishTimeRemainingAsync(VivaTimeRemainingDto timer, CancellationToken ct = default)
        {
            PublishedTimers.Add(timer);
            return Task.CompletedTask;
        }

        public Task PublishSessionCompletedAsync(Guid sessionId, string examId, string studentId, CancellationToken ct = default)
        {
            PublishedCompleted.Add((sessionId, examId, studentId));
            return Task.CompletedTask;
        }

        public Task PublishErrorAsync(VivaErrorDto error, CancellationToken ct = default)
        {
            PublishedErrors.Add(error);
            return Task.CompletedTask;
        }
    }

    #endregion

    private readonly FakeVivaSessionRepository _sessionRepo = new();
    private readonly FakeVivaQuestionRepository _questionRepo = new();
    private readonly FakeVivaTurnRepository _turnRepo = new();
    private readonly FakeSpeechToTextService _sttService = new();
    private readonly FakeTextToSpeechService _ttsService = new();
    private readonly FakeFollowUpGeneratorService _followUpService = new();
    private readonly FakeVivaRealtimePublisher _publisher = new();

    private VivaSessionOrchestrator CreateOrchestrator()
    {
        return new VivaSessionOrchestrator(
            _sessionRepo,
            _questionRepo,
            _turnRepo,
            _sttService,
            _ttsService,
            _followUpService,
            _publisher,
            NullLogger<VivaSessionOrchestrator>.Instance
        );
    }

    [Fact]
    public async Task InitializeSessionAsync_ShouldPersistSessionAndBroadcastStatus()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();

        // Act
        var status = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        // Assert
        Assert.NotEqual(Guid.Empty, status.SessionId);
        Assert.Equal("EXAM01", status.ExamId);
        Assert.Equal("STUDENT_42", status.StudentId);
        Assert.Equal(VivaSessionState.Created, status.State);

        // Verify Persistence First
        Assert.True(_sessionRepo.Sessions.ContainsKey(status.SessionId));
        Assert.Equal((byte)VivaSessionState.Created, _sessionRepo.Sessions[status.SessionId].State);

        // Verify Realtime Broadcast
        Assert.Single(_publisher.PublishedStatuses);
        Assert.Equal(status.SessionId, _publisher.PublishedStatuses[0].SessionId);
        Assert.Equal(VivaSessionState.Created, _publisher.PublishedStatuses[0].State);
    }

    [Fact]
    public async Task AuthorizeUserForSessionAsync_ShouldVerifyMatchingStudentId()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var status = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        // Act & Assert
        Assert.True(await orchestrator.AuthorizeUserForSessionAsync(status.SessionId, "STUDENT_42"));
        Assert.True(await orchestrator.AuthorizeUserForSessionAsync(status.SessionId, "student_42")); // case insensitive
        Assert.False(await orchestrator.AuthorizeUserForSessionAsync(status.SessionId, "OTHER_STUDENT"));
        Assert.False(await orchestrator.AuthorizeUserForSessionAsync(Guid.NewGuid(), "STUDENT_42")); // nonexistent session
    }

    [Fact]
    public async Task StartSessionAsync_ShouldDeliverFirstQuestionAndTransitionState()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        _questionRepo.Questions.Add(new VivaQuestion
        {
            QuestionId = "Q1",
            ExamId = "EXAM01",
            Title = "ASP.NET Core DI",
            Content = "Explain the difference between Transient, Scoped, and Singleton.",
            TimeLimitSeconds = 90
        });

        // Act
        var question = await orchestrator.StartSessionAsync(session.SessionId);

        // Assert
        Assert.Equal("Q1", question.QuestionId);
        Assert.Equal("ASP.NET Core DI", question.Title);
        Assert.Equal("/audio/sample.mp3", question.AudioUrl);

        // Verify State transition in DB
        Assert.Equal((byte)VivaSessionState.QuestionDelivered, _sessionRepo.Sessions[session.SessionId].State);

        // Verify Realtime Broadcast
        var deliveredStatus = _publisher.PublishedStatuses.Last();
        Assert.Equal(VivaSessionState.QuestionDelivered, deliveredStatus.State);
    }

    [Fact]
    public async Task SubmitAnswerAudioAsync_ValidAudio_TranscribesAndPersistsTurn()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        var submission = new VivaAnswerSubmissionDto(
            SessionId: session.SessionId,
            QuestionId: "Q1",
            AudioData: new byte[] { 10, 20, 30, 40, 50 },
            ContentType: "audio/webm",
            DurationSeconds: 45
        );

        // Act
        var transcriptDto = await orchestrator.SubmitAnswerAudioAsync(submission);

        // Assert
        Assert.Equal(session.SessionId, transcriptDto.SessionId);
        Assert.Equal("Q1", transcriptDto.QuestionId);
        Assert.Equal("Scoped DbContext is instantiated once per HTTP request.", transcriptDto.TranscriptText);
        Assert.Equal(0.95m, transcriptDto.ConfidenceScore);

        // Verify Turn is persisted in repository
        Assert.Single(_turnRepo.Turns);
        var turn = _turnRepo.Turns[transcriptDto.TurnId];
        Assert.Equal("Scoped DbContext is instantiated once per HTTP request.", turn.TranscriptText);
        Assert.Equal(1, turn.TurnOrdinal);

        // Verify Realtime Broadcast
        Assert.Single(_publisher.PublishedTranscripts);
        Assert.Equal(transcriptDto.TurnId, _publisher.PublishedTranscripts[0].TurnId);
        Assert.Equal((byte)VivaSessionState.TranscriptReady, _sessionRepo.Sessions[session.SessionId].State);
    }

    [Fact]
    public async Task SubmitAnswerAudioAsync_EmptyAudio_ThrowsArgumentException()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        var submission = new VivaAnswerSubmissionDto(
            SessionId: session.SessionId,
            QuestionId: "Q1",
            AudioData: Array.Empty<byte>(),
            ContentType: "audio/webm",
            DurationSeconds: 0
        );

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => orchestrator.SubmitAnswerAudioAsync(submission));
    }

    [Fact]
    public async Task SubmitAnswerAudioAsync_SttFailure_PublishesVivaErrorAndThrows()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        _sttService.ShouldSucceed = false;
        _sttService.ErrorMessage = "Audio corrupted.";

        var submission = new VivaAnswerSubmissionDto(
            SessionId: session.SessionId,
            QuestionId: "Q1",
            AudioData: new byte[] { 1, 2, 3 },
            ContentType: "audio/webm",
            DurationSeconds: 10
        );

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.SubmitAnswerAudioAsync(submission));
        Assert.Single(_publisher.PublishedErrors);
        Assert.Equal("STT_PROCESSING_FAILED", _publisher.PublishedErrors[0].ErrorCode);
        Assert.True(_publisher.PublishedErrors[0].CanRetry);
    }

    [Fact]
    public async Task GenerateFollowUpAsync_FirstTurn_GeneratesAndBroadcastsFollowUp()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        _questionRepo.Questions.Add(new VivaQuestion
        {
            QuestionId = "Q1",
            ExamId = "EXAM01",
            Title = "ASP.NET Core DI",
            Content = "Explain the difference between Transient, Scoped, and Singleton."
        });

        var submission = new VivaAnswerSubmissionDto(
            SessionId: session.SessionId,
            QuestionId: "Q1",
            AudioData: new byte[] { 1, 2, 3 },
            ContentType: "audio/webm",
            DurationSeconds: 30
        );
        var transcript = await orchestrator.SubmitAnswerAudioAsync(submission);

        // Act
        var followUp = await orchestrator.GenerateFollowUpAsync(session.SessionId, transcript.TurnId);

        // Assert
        Assert.NotNull(followUp);
        Assert.Equal(transcript.TurnId, followUp.TurnId);
        Assert.Equal("Can you elaborate on how DbContext behaves inside a singleton?", followUp.FollowUpContent);
        Assert.Equal("Q1-FU1", followUp.FollowUpQuestionId);
        Assert.Equal("/audio/sample.mp3", followUp.AudioUrl);

        // Verify Persistence First
        var turn = _turnRepo.Turns[transcript.TurnId];
        Assert.Equal("Can you elaborate on how DbContext behaves inside a singleton?", turn.FollowUpQuestionText);
        Assert.Equal((byte)VivaSessionState.FollowUpDelivered, _sessionRepo.Sessions[session.SessionId].State);

        // Verify Realtime Broadcast
        Assert.Single(_publisher.PublishedFollowUps);
        Assert.Equal(followUp.FollowUpQuestionId, _publisher.PublishedFollowUps[0].FollowUpQuestionId);
    }

    [Fact]
    public async Task GenerateFollowUpAsync_MaxTurnsReached_CompletesSessionAndReturnsNull()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        // Simulate 2 turns already completed (reaching max 2 allowed)
        var turn1 = new VivaSessionTurn { TurnId = Guid.NewGuid(), SessionId = session.SessionId, QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "Answer 1" };
        var turn2 = new VivaSessionTurn { TurnId = Guid.NewGuid(), SessionId = session.SessionId, QuestionId = "Q1", TurnOrdinal = 2, TranscriptText = "Answer 2" };
        await _turnRepo.CreateTurnAsync(turn1);
        await _turnRepo.CreateTurnAsync(turn2);

        // Act
        var followUp = await orchestrator.GenerateFollowUpAsync(session.SessionId, turn2.TurnId);

        // Assert
        Assert.Null(followUp);
        Assert.Equal((byte)VivaSessionState.Completed, _sessionRepo.Sessions[session.SessionId].State);
        Assert.Single(_publisher.PublishedCompleted);
        Assert.Equal(session.SessionId, _publisher.PublishedCompleted[0].SessionId);
    }

    private async Task<(VivaSessionOrchestrator Orchestrator, Guid SessionId, Guid TurnId)> ArrangeFirstTurnAsync()
    {
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");
        _questionRepo.Questions.Add(new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "DI", Content = "Explain Scoped lifetime." });
        var transcript = await orchestrator.SubmitAnswerAudioAsync(new VivaAnswerSubmissionDto(
            SessionId: session.SessionId, QuestionId: "Q1", AudioData: new byte[] { 1, 2, 3 }, ContentType: "audio/webm", DurationSeconds: 30));
        return (orchestrator, session.SessionId, transcript.TurnId);
    }

    [Theory]
    [InlineData(false, "Gemini LLM error: This model is currently experiencing high demand (503)")]
    [InlineData(true, "")]
    public async Task GenerateFollowUpAsync_LlmFailure_KeepsSessionOpenAndPublishesRetryableVivaError(bool throws, string providerError)
    {
        var (orchestrator, sessionId, turnId) = await ArrangeFirstTurnAsync();
        _followUpService.ShouldSucceed = false;
        _followUpService.ShouldThrow = throws;
        _followUpService.ErrorMessage = providerError;

        var followUp = await orchestrator.GenerateFollowUpAsync(sessionId, turnId);

        Assert.Null(followUp);
        Assert.Equal((byte)VivaSessionState.TranscriptReady, _sessionRepo.Sessions[sessionId].State);
        Assert.Null(_sessionRepo.Sessions[sessionId].CompletedAtUtc);
        Assert.Empty(_publisher.PublishedCompleted);
        Assert.Equal(VivaSessionState.TranscriptReady, _publisher.PublishedStatuses.Last().State);

        var error = Assert.Single(_publisher.PublishedErrors);
        Assert.Equal(VivaSessionOrchestrator.FollowUpFailedErrorCode, error.ErrorCode);
        Assert.True(error.CanRetry);
        Assert.StartsWith("Giám khảo AI chưa tạo được câu hỏi phụ", error.ErrorMessage);
    }

    [Fact]
    public async Task GenerateFollowUpAsync_RetryAfterFailure_DeliversFollowUp()
    {
        var (orchestrator, sessionId, turnId) = await ArrangeFirstTurnAsync();
        _followUpService.ShouldSucceed = false;
        Assert.Null(await orchestrator.GenerateFollowUpAsync(sessionId, turnId));

        _followUpService.ShouldSucceed = true;
        var followUp = await orchestrator.GenerateFollowUpAsync(sessionId, turnId);

        Assert.NotNull(followUp);
        Assert.Equal((byte)VivaSessionState.FollowUpDelivered, _sessionRepo.Sessions[sessionId].State);
        Assert.Single(_publisher.PublishedFollowUps);
        Assert.Empty(_publisher.PublishedCompleted);
    }

    [Fact]
    public async Task GenerateFollowUpAsync_SuccessWithEmptyQuestion_StillCompletesSession()
    {
        var (orchestrator, sessionId, turnId) = await ArrangeFirstTurnAsync();
        _followUpService.FollowUpText = "  ";

        Assert.Null(await orchestrator.GenerateFollowUpAsync(sessionId, turnId));
        Assert.Equal((byte)VivaSessionState.Completed, _sessionRepo.Sessions[sessionId].State);
        Assert.Empty(_publisher.PublishedErrors);
    }

    [Theory]
    [InlineData("Gemini LLM error: This model is currently experiencing high demand.", "máy chủ AI đang quá tải")]
    [InlineData("HTTP 429 Too Many Requests", "máy chủ AI đang quá tải")]
    [InlineData("Gemini Follow-up timed out after 35 seconds.", "quá thời gian chờ")]
    [InlineData("Câu hỏi phụ (LLM) chưa sẵn sàng: chưa bật AI thật", "Câu hỏi phụ (LLM) chưa sẵn sàng")]
    [InlineData(null, "không phản hồi")]
    public void BuildFollowUpFailureMessage_IsVietnameseAndExplainsReason(string? providerError, string expectedFragment)
    {
        var message = VivaSessionOrchestrator.BuildFollowUpFailureMessage(providerError);

        Assert.Contains(expectedFragment, message);
        Assert.Contains("Thử lại", message);
    }

    [Fact]
    public async Task CompleteSessionAsync_MarksCompletedAndBroadcastsEvents()
    {
        // Arrange
        var orchestrator = CreateOrchestrator();
        var session = await orchestrator.InitializeSessionAsync("EXAM01", "STUDENT_42");

        // Act
        var status = await orchestrator.CompleteSessionAsync(session.SessionId);

        // Assert
        Assert.Equal(VivaSessionState.Completed, status.State);
        Assert.NotNull(_sessionRepo.Sessions[session.SessionId].CompletedAtUtc);
        Assert.Single(_publisher.PublishedCompleted);
        Assert.Equal(session.SessionId, _publisher.PublishedCompleted[0].SessionId);
    }
}
