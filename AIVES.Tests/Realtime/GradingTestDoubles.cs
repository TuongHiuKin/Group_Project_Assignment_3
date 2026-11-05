using System.Collections.Concurrent;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Entities;

namespace AIVES.Tests.Realtime;

/// <summary>In-memory stand-in for LEAD-03 GradingOrchestrator. "Persists" before returning, like the real one.</summary>
internal sealed class FakeGradingOrchestrator : IGradingOrchestrator
{
    public ConcurrentDictionary<(Guid SessionId, string QuestionId), QuestionGradingResultDto> Results { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentDictionary<string, int> Attempts { get; } = new();
    /// <summary>(sessionId, questionId, attempt) → throw to fail, return null for the default AiGraded result.</summary>
    public Func<Guid, string, int, QuestionGradingResultDto?>? Behavior { get; set; }
    /// <summary>When set, grading waits for this task (used to observe concurrency).</summary>
    public TaskCompletionSource? Gate { get; set; }
    public decimal Score { get; set; } = 7m;
    private int _running;
    public int MaxConcurrent;

    public QuestionGradingResultDto Seed(Guid sessionId, string questionId, GradingState state, decimal? score = 5m)
    {
        var dto = new QuestionGradingResultDto
        {
            Id = Guid.NewGuid(), SessionId = sessionId, QuestionId = questionId, State = state, MaxPoints = 10m,
            AiScore = state == GradingState.Pending ? null : score,
            TeacherFinalScore = state == GradingState.Finalized ? score : null
        };
        Results[(sessionId, questionId)] = dto;
        return dto;
    }

    public Task<QuestionGradingResultDto> GradeQuestionAsync(Guid sessionId, string questionId, CancellationToken ct = default) =>
        RunAsync("grade", sessionId, questionId, ct);

    public Task<QuestionGradingResultDto> RegradeQuestionAsync(Guid sessionId, string questionId, string requestedBy, CancellationToken ct = default) =>
        RunAsync("regrade", sessionId, questionId, ct);

    private async Task<QuestionGradingResultDto> RunAsync(string kind, Guid sessionId, string questionId, CancellationToken ct)
    {
        var running = Interlocked.Increment(ref _running);
        InterlockedMax(ref MaxConcurrent, running);
        try
        {
            Calls.Enqueue($"{kind}:{questionId}");
            var attempt = Attempts.AddOrUpdate($"{sessionId}/{questionId}", 1, (_, n) => n + 1);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            var result = Behavior?.Invoke(sessionId, questionId, attempt) ?? new QuestionGradingResultDto
            {
                Id = Guid.NewGuid(), SessionId = sessionId, QuestionId = questionId, MaxPoints = 10m,
                AiScore = Score, State = GradingState.AiGraded, AiProvider = "Mock"
            };
            Results[(sessionId, questionId)] = result; // persisted before returning (persistence-first)
            return result;
        }
        finally { Interlocked.Decrement(ref _running); }
    }

    public Task<SessionGradingSummaryDto?> GetSessionGradingAsync(Guid sessionId, CancellationToken ct = default)
    {
        var rows = Results.Where(r => r.Key.SessionId == sessionId).Select(r => r.Value).OrderBy(r => r.QuestionId).ToList();
        return Task.FromResult(rows.Count == 0 ? null : new SessionGradingSummaryDto { SessionId = sessionId, QuestionResults = rows });
    }

    public Task<SessionGradingSummaryDto> GradeSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<QuestionGradingResultDto> FinalizeScoreAsync(FinalizeScoreRequestDto request, string teacherId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<List<SessionGradingSummaryDto>> GetGradingListByExamAsync(string examId, string? classId = null, GradingState? filterState = null, CancellationToken ct = default) => throw new NotSupportedException();

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }
}

/// <summary>Records grading events and lets a test inspect state at the moment each event is published.</summary>
internal sealed class GradingEventRecorder : IVivaRealtimePublisher
{
    public ConcurrentQueue<(string ExamId, object Event)> Events { get; } = new();
    public Action<object>? OnEvent { get; set; }

    private Task Add(string examId, object dto)
    {
        OnEvent?.Invoke(dto);
        Events.Enqueue((examId, dto));
        return Task.CompletedTask;
    }

    public IEnumerable<T> Of<T>() => Events.Select(e => e.Event).OfType<T>();

    public Task PublishGradingStartedAsync(string examId, GradingStartedDto started, CancellationToken ct = default) => Add(examId, started);
    public Task PublishGradingCompletedAsync(string examId, GradingCompletedDto completed, CancellationToken ct = default) => Add(examId, completed);
    public Task PublishGradingFailedAsync(string examId, GradingFailedDto failed, CancellationToken ct = default) => Add(examId, failed);

    public Task PublishStatusChangedAsync(VivaSessionStatusDto status, CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishTranscriptReadyAsync(VivaTranscriptDto transcript, CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishFollowUpGeneratedAsync(VivaFollowUpDto followUp, CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishTimeRemainingAsync(VivaTimeRemainingDto timer, CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishSessionCompletedAsync(Guid sessionId, string examId, string studentId, CancellationToken ct = default) => Task.CompletedTask;
    public Task PublishErrorAsync(VivaErrorDto error, CancellationToken ct = default) => Task.CompletedTask;
}

internal static class GradingFixture
{
    /// <summary>Adds a completed EXAM01 session where the student answered the given questions (in order).</summary>
    public static Guid AddCompletedSession(this RealtimeData data, string studentId, params string[] answeredQuestionIds)
    {
        var id = data.AddSession(studentId, VivaSessionState.Completed);
        data.Sessions[id].CompletedAtUtc = DateTime.UtcNow;
        var ordinal = 0;
        foreach (var questionId in answeredQuestionIds)
        {
            var turn = new VivaSessionTurn
            {
                TurnId = Guid.NewGuid(), SessionId = id, QuestionId = questionId, TurnOrdinal = ++ordinal,
                TranscriptText = "answer", CreatedAtUtc = DateTime.UtcNow
            };
            data.Turns[turn.TurnId] = turn;
        }
        return id;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
