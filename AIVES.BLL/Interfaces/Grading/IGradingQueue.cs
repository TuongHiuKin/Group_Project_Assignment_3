using AIVES.BLL.Models.Grading;

namespace AIVES.BLL.Interfaces.Grading;

/// <summary>
/// In-process background grading queue (G4-RT-01). Enqueueing never blocks the caller.
/// A job stays "in flight" from <see cref="TryEnqueue"/> until <see cref="MarkCompleted"/>, which prevents duplicates (G4-RT-03).
/// </summary>
public interface IGradingQueue
{
    /// <summary>Returns false when an overlapping job is already queued/running or the queue is full.</summary>
    bool TryEnqueue(GradingJob job);

    /// <summary>True when a job covering this session (and question, if given) is queued or running.</summary>
    bool IsInFlight(Guid sessionId, string? questionId = null);

    IAsyncEnumerable<GradingJob> ReadAllAsync(CancellationToken ct);

    /// <summary>Releases the duplicate guard for a job. Must be called once the job has been processed (success or failure).</summary>
    void MarkCompleted(GradingJob job);

    int PendingCount { get; }
}

/// <summary>Lecturer entry point for "Chấm lại bằng AI" (G4-RT-03). The work itself runs in the background worker.</summary>
public interface IGradingRegradeService
{
    Task<RegradeRequestResult> RequestRegradeAsync(
        Guid sessionId, string? questionId, string requestedBy, CancellationToken ct = default);
}

/// <summary>Finds completed sessions whose grading never finished (G4-RT-04 startup recovery).</summary>
public interface IGradingRecoveryScanner
{
    Task<IReadOnlyList<GradingJob>> FindUnfinishedJobsAsync(CancellationToken ct = default);
}
