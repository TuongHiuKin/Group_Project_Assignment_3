using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIVES.Tests.VivaInterview;

/// <summary>
/// Câu hỏi xoáy phải được đọc bằng CÙNG giọng với câu hỏi chính. Câu hỏi chính luôn có file
/// audio do TTS sinh sẵn; câu hỏi xoáy sinh lúc chạy nên có thể thiếu file — khi đó client
/// rơi về speechSynthesis của trình duyệt, giọng khác hẳn. Các test này khoá hành vi
/// "sinh bù audio bằng đúng TTS service" để giọng luôn thống nhất.
/// </summary>
public class FollowUpAudioConsistencyTests
{
    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid TurnId = Guid.NewGuid();

    [Fact]
    public async Task EnsureFollowUpAudio_returns_existing_url_without_calling_tts()
    {
        const string existing = "/audio/cache/tts-already-there.wav";
        var turn = MakeTurn(FollowUpAudioUrl: existing);
        var fixture = new Fixture(turn);

        var url = await fixture.Orchestrator.EnsureFollowUpAudioAsync(SessionId, TurnId);

        Assert.Equal(existing, url);
        Assert.Equal(0, fixture.Tts.Calls);
    }

    [Fact]
    public async Task EnsureFollowUpAudio_generates_and_persists_missing_audio()
    {
        var turn = MakeTurn(FollowUpAudioUrl: null);
        var fixture = new Fixture(turn);

        var url = await fixture.Orchestrator.EnsureFollowUpAudioAsync(SessionId, TurnId);

        Assert.NotNull(url);
        Assert.Equal(1, fixture.Tts.Calls);
        // Cùng một ITextToSpeechService mà câu hỏi chính dùng ⇒ cùng voice/model ⇒ cùng giọng.
        Assert.Equal(turn.FollowUpQuestionText, fixture.Tts.LastText);
        Assert.Equal("vi-VN", fixture.Tts.LastLocale);
        Assert.Equal(url, fixture.TurnRepo.SavedAudioUrl);
    }

    [Fact]
    public async Task EnsureFollowUpAudio_returns_null_when_turn_has_no_follow_up()
    {
        var turn = MakeTurn(FollowUpAudioUrl: null, followUpText: null);
        var fixture = new Fixture(turn);

        var url = await fixture.Orchestrator.EnsureFollowUpAudioAsync(SessionId, TurnId);

        Assert.Null(url);
        Assert.Equal(0, fixture.Tts.Calls);
    }

    [Fact]
    public async Task EnsureFollowUpAudio_returns_null_when_tts_fails()
    {
        var turn = MakeTurn(FollowUpAudioUrl: null);
        var fixture = new Fixture(turn, ttsSucceeds: false);

        var url = await fixture.Orchestrator.EnsureFollowUpAudioAsync(SessionId, TurnId);

        Assert.Null(url);
        Assert.Null(fixture.TurnRepo.SavedAudioUrl);
    }

    [Fact]
    public async Task EnsureFollowUpAudio_rejects_turn_from_another_session()
    {
        var turn = MakeTurn(FollowUpAudioUrl: null);
        turn.SessionId = Guid.NewGuid();   // thuộc phiên khác
        var fixture = new Fixture(turn);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.EnsureFollowUpAudioAsync(SessionId, TurnId));
    }

    [Fact]
    public async Task EnsureFollowUpAudio_rejects_finished_session()
    {
        var turn = MakeTurn(FollowUpAudioUrl: null);
        var fixture = new Fixture(turn, sessionState: VivaSessionState.Completed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator.EnsureFollowUpAudioAsync(SessionId, TurnId));
    }

    private static VivaSessionTurn MakeTurn(string? FollowUpAudioUrl, string? followUpText = "Vì sao không dùng Singleton?")
        => new()
        {
            TurnId = TurnId,
            SessionId = SessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            TranscriptText = "DbContext nên là Scoped.",
            FollowUpQuestionText = followUpText,
            FollowUpAudioUrl = FollowUpAudioUrl,
            CreatedAtUtc = DateTime.UtcNow
        };

    private sealed class Fixture
    {
        public VivaSessionOrchestrator Orchestrator { get; }
        public RecordingTts Tts { get; }
        public StubTurnRepo TurnRepo { get; }

        public Fixture(VivaSessionTurn turn, bool ttsSucceeds = true, VivaSessionState sessionState = VivaSessionState.FollowUpDelivered)
        {
            Tts = new RecordingTts(ttsSucceeds);
            TurnRepo = new StubTurnRepo(turn);

            Orchestrator = new VivaSessionOrchestrator(
                new StubSessionRepo(new VivaSession
                {
                    SessionId = SessionId,
                    ExamId = "EXAM01",
                    StudentId = "1",
                    State = (byte)sessionState,
                    StartedAtUtc = DateTime.UtcNow
                }),
                new StubQuestionRepo(),
                TurnRepo,
                Tts,                          // STT (không dùng ở đây)
                Tts,                          // TTS — chính là service sinh giọng
                new StubFollowUp(),
                new NoopPublisher(),
                NullLogger<VivaSessionOrchestrator>.Instance,
                timers: null);
        }
    }

    private sealed class RecordingTts(bool succeeds) : ISpeechToTextService, ITextToSpeechService
    {
        public int Calls { get; private set; }
        public string? LastText { get; private set; }
        public string? LastLocale { get; private set; }

        public Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default)
            => Task.FromResult(new SpeechToTextResult(true, "transcript", 0.9m));

        public Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default)
        {
            Calls++;
            LastText = text;
            LastLocale = voiceLocale;
            return Task.FromResult(succeeds
                ? new TextToSpeechResult(true, "/audio/cache/tts-followup-generated.wav", null)
                : new TextToSpeechResult(false, null, null, "TTS failed"));
        }
    }

    private sealed class StubSessionRepo(VivaSession session) : IVivaSessionRepository
    {
        public Task<VivaSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(sessionId == session.SessionId ? session : null);

        public Task<VivaSession?> GetWithDetailsAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(sessionId == session.SessionId ? session : null);

        public Task<List<VivaSession>> GetSessionsWithDetailsAsync(string? examId = null, string? studentId = null, CancellationToken ct = default) =>
            Task.FromResult(new List<VivaSession> { session });

        public Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());

        public Task<Guid> CreateSessionAsync(VivaSession s, CancellationToken ct = default) => Task.FromResult(s.SessionId);

        public Task UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default)
        {
            session.State = state;
            return Task.CompletedTask;
        }

        public Task CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubQuestionRepo : IVivaQuestionRepository
    {
        public Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default) =>
            Task.FromResult(new List<VivaQuestion>
            {
                new() { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "DbContext", Content = "Vì sao Scoped?" }
            });

        public Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());

        public Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default) =>
            Task.FromResult<VivaQuestion?>(new VivaQuestion
            {
                QuestionId = "EXAM01-Q01",
                ExamId = "EXAM01",
                Title = "DbContext",
                Content = "Vì sao Scoped?"
            });

        public Task<bool> SaveAudioUrlAsync(string questionId, string audioUrl, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class StubTurnRepo(VivaSessionTurn turn) : IVivaTurnRepository
    {
        public string? SavedAudioUrl { get; private set; }

        public Task<Guid> CreateTurnAsync(VivaSessionTurn t, CancellationToken ct = default) =>
            Task.FromResult(t.TurnId);

        public Task<VivaSessionTurn?> GetByIdAsync(Guid turnId, CancellationToken ct = default) =>
            Task.FromResult(turnId == turn.TurnId ? turn : null);

        public Task SaveTranscriptAsync(Guid turnId, string transcript, decimal confidence, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SaveFollowUpAsync(Guid turnId, string followUpQuestion, string? audioPath, CancellationToken ct = default)
        {
            SavedAudioUrl = audioPath;
            turn.FollowUpQuestionText = followUpQuestion;
            turn.FollowUpAudioUrl = audioPath;
            return Task.CompletedTask;
        }

        public Task<int> GetTurnCountForSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult(1);
    }

    private sealed class StubFollowUp : IFollowUpGeneratorService
    {
        public Task<FollowUpResult> GenerateFollowUpQuestionAsync(string originalQuestion, string studentAnswerTranscript,
            string subjectContext, CancellationToken ct = default) =>
            Task.FromResult(new FollowUpResult(true, "Vì sao không dùng Singleton?", "Clarification"));
    }

    private sealed class NoopPublisher : IVivaRealtimePublisher
    {
        public Task PublishStatusChangedAsync(VivaSessionStatusDto status, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishTranscriptReadyAsync(VivaTranscriptDto transcript, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishFollowUpGeneratedAsync(VivaFollowUpDto followUp, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishTimeRemainingAsync(VivaTimeRemainingDto timer, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishSessionCompletedAsync(Guid sessionId, string examId, string studentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishErrorAsync(VivaErrorDto error, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishGradingStartedAsync(string examId, AIVES.BLL.Models.Grading.GradingStartedDto started, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishGradingCompletedAsync(string examId, AIVES.BLL.Models.Grading.GradingCompletedDto completed, CancellationToken ct = default) => Task.CompletedTask;
        public Task PublishGradingFailedAsync(string examId, AIVES.BLL.Models.Grading.GradingFailedDto failed, CancellationToken ct = default) => Task.CompletedTask;
    }
}
