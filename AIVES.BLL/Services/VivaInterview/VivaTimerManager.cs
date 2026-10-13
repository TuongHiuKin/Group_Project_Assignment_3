using System.Collections.Concurrent;
using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Services.VivaInterview;

/// <summary>
/// Single-process countdown registry. No scoped services or connection cancellation tokens
/// are retained here. Durable deadlines must be supplied by the DAL before multi-node hosting.
/// </summary>
public sealed class VivaTimerManager(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, Deadline> _deadlines = new();
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public sealed record Deadline(Guid SessionId, string QuestionId, DateTimeOffset EndsAtUtc, int TotalSecondsAllowed);

    public async Task<IDisposable> LockSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var gate = _gates[(uint)sessionId.GetHashCode() % (uint)_gates.Length];
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    // Caller holds the session lock across persistence and timer changes.
    public Deadline Start(Guid sessionId, string questionId, int seconds)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var deadline = new Deadline(sessionId, questionId, clock.GetUtcNow().AddSeconds(seconds), seconds);
        if (!_deadlines.TryAdd(sessionId, deadline))
            throw new InvalidOperationException("An answering timer already exists for this session.");
        return deadline;
    }

    public Deadline? GetDeadline(Guid sessionId) => _deadlines.GetValueOrDefault(sessionId);
    public int SecondsRemaining(Deadline deadline) => Math.Clamp(
        (int)Math.Ceiling((deadline.EndsAtUtc - clock.GetUtcNow()).TotalSeconds), 0, deadline.TotalSecondsAllowed);
    public void Restore(Deadline deadline)
    {
        if (!_deadlines.TryAdd(deadline.SessionId, deadline))
            throw new InvalidOperationException("An answering timer already exists for this session.");
    }
    public Guid[] GetActiveSessionIds() => _deadlines.Keys.ToArray();
    public void Stop(Guid sessionId) => _deadlines.TryRemove(sessionId, out _);

    public VivaTimeRemainingDto? GetRemaining(Guid sessionId)
    {
        var deadline = GetDeadline(sessionId);
        return deadline is null ? null : new(sessionId,
            SecondsRemaining(deadline),
            deadline.TotalSecondsAllowed);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
