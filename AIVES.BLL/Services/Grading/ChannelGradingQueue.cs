using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.Grading;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Grading;

/// <summary>
/// Bounded <see cref="Channel{T}"/> queue with an in-flight guard (G4-RT-01, G4-RT-03).
/// Registered as a singleton; a single background reader processes jobs one at a time,
/// so the same question is never graded by two jobs concurrently.
/// </summary>
public sealed class ChannelGradingQueue : IGradingQueue
{
    private const string WholeSession = "*";
    private readonly Channel<GradingJob> _channel;
    private readonly Dictionary<Guid, HashSet<string>> _inFlight = new();
    private readonly object _gate = new();

    public ChannelGradingQueue(IOptions<GradingWorkerOptions>? options = null)
    {
        var capacity = Math.Max(1, options?.Value.QueueCapacity ?? 100);
        _channel = Channel.CreateBounded<GradingJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false instead of dropping old jobs.
            SingleReader = true,
            SingleWriter = false
        });
    }

    public int PendingCount => _channel.Reader.Count;

    public bool TryEnqueue(GradingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.SessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(job));
        if (job.QuestionId is not null && string.IsNullOrWhiteSpace(job.QuestionId))
            throw new ArgumentException("Question ID must be null (whole session) or non-empty.", nameof(job));

        var key = KeyOf(job.QuestionId);
        lock (_gate)
        {
            if (_inFlight.TryGetValue(job.SessionId, out var keys) &&
                (keys.Contains(WholeSession) || keys.Contains(key)))
                return false; // Already queued or running: do not grade twice.

            if (!_channel.Writer.TryWrite(job)) return false; // Queue full; startup recovery picks it up later.

            if (keys is null) _inFlight[job.SessionId] = keys = new HashSet<string>(StringComparer.Ordinal);
            keys.Add(key);
            return true;
        }
    }

    public bool IsInFlight(Guid sessionId, string? questionId = null)
    {
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(sessionId, out var keys) || keys.Count == 0) return false;
            return questionId is null || keys.Contains(WholeSession) || keys.Contains(questionId);
        }
    }

    public void MarkCompleted(GradingJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(job.SessionId, out var keys)) return;
            keys.Remove(KeyOf(job.QuestionId));
            if (keys.Count == 0) _inFlight.Remove(job.SessionId);
        }
    }

    public async IAsyncEnumerable<GradingJob> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var job in _channel.Reader.ReadAllAsync(ct)) yield return job;
    }

    private static string KeyOf(string? questionId) => questionId ?? WholeSession;
}
