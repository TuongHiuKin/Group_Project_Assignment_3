using System.Collections.Concurrent;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;

namespace AIVES.Tests.Realtime;

internal sealed class ManualClock : TimeProvider
{
    private long _ticks = DateTimeOffset.UtcNow.UtcTicks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
}

// In-memory repository boundary only. This fixture does not register fake persistence in production.
internal sealed class RealtimeData : IVivaSessionRepository, IVivaQuestionRepository, IVivaTurnRepository
{
    public ConcurrentDictionary<Guid, VivaSession> Sessions { get; } = new();
    public ConcurrentDictionary<Guid, VivaSessionTurn> Turns { get; } = new();
    public VivaQuestion Question { get; } = new()
    {
        QuestionId = "Q1", ExamId = "EXAM01", Title = "Lifetime", Content = "Explain scoped lifetime.",
        TimeLimitSeconds = 60, AudioUrl = "/audio/question.mp3"
    };
    public bool FailSave { get; set; }
    public int TimeoutSaves;
    public int StateSaves;

    public Guid AddSession(string studentId, VivaSessionState state = VivaSessionState.QuestionDelivered)
    {
        var id = Guid.NewGuid();
        Sessions[id] = new() { SessionId = id, StudentId = studentId, ExamId = Question.ExamId,
            State = (byte)state, StartedAtUtc = DateTime.UtcNow };
        return id;
    }

    Task<VivaSession?> IVivaSessionRepository.GetByIdAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Sessions.GetValueOrDefault(id));
    }
    Task<VivaSession?> IVivaSessionRepository.GetWithDetailsAsync(Guid id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = Sessions.GetValueOrDefault(id);
        if (session is not null) session.Turns = Turns.Values.Where(t => t.SessionId == id).ToList();
        return Task.FromResult(session);
    }
    public Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailSave) throw new InvalidOperationException("Fixture persistence failed.");
        Sessions[session.SessionId] = session;
        return Task.FromResult(session.SessionId);
    }
    public Task UpdateStateAsync(Guid id, byte state, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailSave) throw new InvalidOperationException("Fixture persistence failed.");
        Sessions[id].State = state;
        Interlocked.Increment(ref StateSaves);
        if (state == (byte)VivaSessionState.TimedOut) Interlocked.Increment(ref TimeoutSaves);
        return Task.CompletedTask;
    }
    public Task CompleteSessionAsync(Guid id, DateTime date, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailSave) throw new InvalidOperationException("Fixture persistence failed.");
        Sessions[id].State = (byte)VivaSessionState.Completed;
        Sessions[id].CompletedAtUtc = date;
        return Task.CompletedTask;
    }
    public Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(examId == Question.ExamId ? new List<VivaQuestion> { Question } : new());
    }
    Task<VivaQuestion?> IVivaQuestionRepository.GetByIdAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(id == Question.QuestionId ? Question : null);
    }
    public Task<Guid> CreateTurnAsync(VivaSessionTurn turn, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (FailSave) throw new InvalidOperationException("Fixture persistence failed.");
        Turns[turn.TurnId] = turn;
        return Task.FromResult(turn.TurnId);
    }
    Task<VivaSessionTurn?> IVivaTurnRepository.GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(Turns.GetValueOrDefault(id));
    public Task SaveTranscriptAsync(Guid id, string text, decimal confidence, CancellationToken ct = default)
    {
        if (FailSave) throw new InvalidOperationException("Fixture persistence failed.");
        Turns[id].TranscriptText = text;
        Turns[id].ConfidenceScore = confidence;
        return Task.CompletedTask;
    }
    public Task SaveFollowUpAsync(Guid id, string text, string? audio, CancellationToken ct = default)
    {
        if (FailSave) throw new InvalidOperationException("Fixture persistence failed.");
        Turns[id].FollowUpQuestionText = text;
        Turns[id].FollowUpAudioUrl = audio;
        return Task.CompletedTask;
    }
    public Task<int> GetTurnCountForSessionAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Turns.Values.Count(t => t.SessionId == id));
}

internal sealed class RealtimeAi : ISpeechToTextService, ITextToSpeechService, IFollowUpGeneratorService
{
    public int Transcriptions;
    public bool TranscriptionSucceeds { get; set; } = true;
    public Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Transcriptions);
        return Task.FromResult(TranscriptionSucceeds
            ? new SpeechToTextResult(true, "Scoped per request.", .95m)
            : new SpeechToTextResult(false, "", 0, "Fixture STT failure"));
    }
    public Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default) =>
        Task.FromResult(new TextToSpeechResult(true, "/audio/fixture.mp3", null));
    public Task<FollowUpResult> GenerateFollowUpQuestionAsync(string originalQuestion, string studentAnswerTranscript,
        string subjectContext, CancellationToken ct = default) => Task.FromResult(new FollowUpResult(true, "Why not singleton?", "Clarification"));
}

internal sealed class RecordingPublisher : IVivaRealtimePublisher
{
    public List<object> Events { get; } = new();
    private Task Add(object dto, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Events.Add(dto); return Task.CompletedTask; }
    public Task PublishStatusChangedAsync(VivaSessionStatusDto status, CancellationToken ct = default) => Add(status, ct);
    public Task PublishTranscriptReadyAsync(VivaTranscriptDto transcript, CancellationToken ct = default) => Add(transcript, ct);
    public Task PublishFollowUpGeneratedAsync(VivaFollowUpDto followUp, CancellationToken ct = default) => Add(followUp, ct);
    public Task PublishTimeRemainingAsync(VivaTimeRemainingDto timer, CancellationToken ct = default) => Add(timer, ct);
    public Task PublishSessionCompletedAsync(Guid id, string examId, string studentId, CancellationToken ct = default) => Add((id, examId, studentId), ct);
    public Task PublishErrorAsync(VivaErrorDto error, CancellationToken ct = default) => Add(error, ct);
}
