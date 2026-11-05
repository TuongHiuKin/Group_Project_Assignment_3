namespace AIVES.BLL.Models.Grading;

public enum GradingJobKind : byte
{
    /// <summary>First grading after the session completed (or startup recovery). Grades only questions without a result yet.</summary>
    Initial = 0,
    /// <summary>Lecturer-requested AI regrade. Re-grades every question that is not Finalized.</summary>
    Regrade = 1
}

/// <summary>
/// One unit of work for the background grading queue.
/// <paramref name="QuestionId"/> null means "every question of the session".
/// <paramref name="ExamId"/> may be null (e.g. startup recovery); the worker resolves it from the session.
/// </summary>
public sealed record GradingJob(
    Guid SessionId,
    string? QuestionId = null,
    GradingJobKind Kind = GradingJobKind.Initial,
    string? ExamId = null,
    string? RequestedBy = null);

public enum RegradeRequestResult
{
    Queued,
    AlreadyInProgress,
    AlreadyFinalized,
    SessionNotFound,
    SessionNotCompleted,
    QueueUnavailable
}

/// <summary>
/// Thrown by grading persistence when an optimistic-concurrency (RowVersion) conflict occurs,
/// e.g. a lecturer finalized the score while AI regrading was running. The worker treats it as "skip, do not overwrite".
/// </summary>
public sealed class GradingConflictException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class GradingWorkerOptions
{
    public const string SectionName = "Grading:Worker";

    /// <summary>Bounded queue capacity.</summary>
    public int QueueCapacity { get; set; } = 100;
    /// <summary>Retries for transient failures after the first attempt (3 → up to 4 attempts).</summary>
    public int MaxRetries { get; set; } = 3;
    /// <summary>Exponential backoff base: 2s, 4s, 8s (+ up to 20% jitter).</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    /// <summary>Run the startup recovery scan for unfinished grading jobs.</summary>
    public bool RecoverOnStartup { get; set; } = true;
}
