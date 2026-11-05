using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.Grading;
using Microsoft.Extensions.Options;

namespace AIVES.Tests.Realtime;

// G4-RT-01 / RT-03: bounded channel queue and duplicate guard.
public sealed class GradingQueueTests
{
    private static ChannelGradingQueue Queue(int capacity = 100) =>
        new(Options.Create(new GradingWorkerOptions { QueueCapacity = capacity }));

    private static async Task<List<GradingJob>> Drain(ChannelGradingQueue queue, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var jobs = new List<GradingJob>();
        await foreach (var job in queue.ReadAllAsync(cts.Token))
        {
            jobs.Add(job);
            if (jobs.Count == count) break;
        }
        return jobs;
    }

    [Fact]
    public async Task Jobs_are_read_in_fifo_order()
    {
        var queue = Queue();
        var a = new GradingJob(Guid.NewGuid());
        var b = new GradingJob(Guid.NewGuid(), "Q1");
        var c = new GradingJob(Guid.NewGuid(), "Q2", GradingJobKind.Regrade);
        Assert.True(queue.TryEnqueue(a));
        Assert.True(queue.TryEnqueue(b));
        Assert.True(queue.TryEnqueue(c));
        Assert.Equal(3, queue.PendingCount);

        Assert.Equal(new[] { a, b, c }, await Drain(queue, 3));
    }

    [Fact]
    public void Same_question_cannot_be_queued_twice_until_completed()
    {
        var queue = Queue();
        var session = Guid.NewGuid();
        var job = new GradingJob(session, "Q1", GradingJobKind.Regrade);

        Assert.True(queue.TryEnqueue(job));
        Assert.False(queue.TryEnqueue(job with { RequestedBy = "another-lecturer" }));
        Assert.True(queue.IsInFlight(session, "Q1"));

        queue.MarkCompleted(job);
        Assert.False(queue.IsInFlight(session, "Q1"));
        Assert.True(queue.TryEnqueue(job));
    }

    [Fact]
    public void Whole_session_job_blocks_question_jobs_of_that_session_only()
    {
        var queue = Queue();
        var session = Guid.NewGuid();
        var whole = new GradingJob(session);

        Assert.True(queue.TryEnqueue(whole));
        Assert.False(queue.TryEnqueue(new GradingJob(session, "Q1", GradingJobKind.Regrade)));
        Assert.False(queue.TryEnqueue(new GradingJob(session)));
        Assert.True(queue.TryEnqueue(new GradingJob(Guid.NewGuid(), "Q1")));
        Assert.True(queue.IsInFlight(session, "Q9"));
    }

    [Fact]
    public void Question_job_in_flight_marks_session_in_flight_but_allows_initial_session_job()
    {
        var queue = Queue();
        var session = Guid.NewGuid();
        Assert.True(queue.TryEnqueue(new GradingJob(session, "Q1", GradingJobKind.Regrade)));

        Assert.True(queue.IsInFlight(session));
        Assert.False(queue.IsInFlight(session, "Q2"));
        // The worker skips already graded/finalized questions, so a whole-session job is safe to queue.
        Assert.True(queue.TryEnqueue(new GradingJob(session)));
    }

    [Fact]
    public void Full_queue_rejects_without_leaving_a_stale_in_flight_marker()
    {
        var queue = Queue(capacity: 2);
        Assert.True(queue.TryEnqueue(new GradingJob(Guid.NewGuid())));
        Assert.True(queue.TryEnqueue(new GradingJob(Guid.NewGuid())));
        var rejected = new GradingJob(Guid.NewGuid());

        Assert.False(queue.TryEnqueue(rejected));
        Assert.False(queue.IsInFlight(rejected.SessionId));
    }

    [Fact]
    public void Invalid_jobs_are_rejected()
    {
        var queue = Queue();
        Assert.Throws<ArgumentException>(() => queue.TryEnqueue(new GradingJob(Guid.Empty)));
        Assert.Throws<ArgumentException>(() => queue.TryEnqueue(new GradingJob(Guid.NewGuid(), " ")));
        Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null!));
    }
}
