using System.Net;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.Grading;
using AIVES.BLL.Services.VivaInterview;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIVES.Tests.Realtime;

// G4-RT-01..RT-04: worker pipeline, per-question isolation, retry, regrade guards and startup recovery.
public sealed class GradingWorkerTests
{
    private sealed class Harness
    {
        public RealtimeData Data { get; } = new();
        public FakeGradingOrchestrator Grader { get; } = new();
        public GradingEventRecorder Events { get; } = new();
        public GradingWorkerOptions Options { get; } = new() { RetryBaseDelay = TimeSpan.Zero, RecoverOnStartup = false };
        public ChannelGradingQueue Queue { get; }
        public ServiceProvider Services { get; }

        public Harness(bool registerOrchestrator = true)
        {
            Data.ExtraQuestions.Add(new() { QuestionId = "Q2", ExamId = "EXAM01", Title = "DI", Content = "Explain DI." });
            Queue = new ChannelGradingQueue(Microsoft.Extensions.Options.Options.Create(Options));
            var services = new ServiceCollection();
            services.AddSingleton<IVivaSessionRepository>(Data);
            services.AddSingleton<IVivaQuestionRepository>(Data);
            services.AddSingleton<IVivaRealtimePublisher>(Events);
            services.AddSingleton<IGradingQueue>(Queue);
            if (registerOrchestrator) services.AddSingleton<IGradingOrchestrator>(Grader);
            services.AddScoped<IGradingRecoveryScanner, GradingRecoveryScanner>();
            services.AddScoped<IGradingRegradeService, GradingRegradeService>();
            Services = services.BuildServiceProvider();
        }

        public GradingJobProcessor Processor() => new(Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(Options), TimeProvider.System, NullLogger<GradingJobProcessor>.Instance);

        public VivaGradingBackgroundService Worker() => new(Queue, Processor(), Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<VivaGradingBackgroundService>.Instance);

        public IGradingRegradeService Regrade() => Services.CreateScope().ServiceProvider.GetRequiredService<IGradingRegradeService>();
        public IGradingRecoveryScanner Scanner() => Services.CreateScope().ServiceProvider.GetRequiredService<IGradingRecoveryScanner>();
    }

    private static string Describe(object e) => e switch
    {
        GradingStartedDto s => $"started:{s.QuestionId}",
        GradingCompletedDto c => $"completed:{c.QuestionId}",
        GradingFailedDto f => $"failed:{f.QuestionId}",
        _ => e.ToString()!
    };

    // ---- RT-01 / RT-02: pipeline and payloads ----

    [Fact]
    public async Task Initial_job_grades_answered_questions_in_order_and_publishes_started_then_completed()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1", "Q1", "Q2");

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.Equal(new[] { "grade:Q1", "grade:Q2" }, h.Grader.Calls);
        Assert.Equal(new[] { "started:Q1", "completed:Q1", "started:Q2", "completed:Q2" },
            h.Events.Events.Select(e => Describe(e.Event)));
        Assert.All(h.Events.Events, e => Assert.Equal("EXAM01", e.ExamId)); // examId resolved from the session
        var completed = h.Events.Of<GradingCompletedDto>().First();
        Assert.Equal(new GradingCompletedDto(session, "Q1", (byte)GradingState.AiGraded, 7m, 10m, true), completed);
        Assert.Equal(session, h.Events.Of<GradingStartedDto>().First().SessionId);
    }

    [Fact]
    public async Task Completed_event_is_published_only_after_the_result_was_persisted()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1");
        var persistedAtPublish = false;
        h.Events.OnEvent = e =>
        {
            if (e is GradingCompletedDto c) persistedAtPublish = h.Grader.Results.ContainsKey((c.SessionId, c.QuestionId));
        };

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.True(persistedAtPublish);
    }

    [Fact]
    public async Task Falls_back_to_exam_questions_when_session_has_no_turns()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a");

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.Equal(new[] { "grade:Q1", "grade:Q2" }, h.Grader.Calls);
    }

    [Fact]
    public async Task One_failing_question_does_not_block_the_others()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1", "Q2");
        h.Grader.Behavior = (_, q, _) => q == "Q1" ? throw new InvalidOperationException("secret stack detail") : null;

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.Equal(new[] { "started:Q1", "failed:Q1", "started:Q2", "completed:Q2" },
            h.Events.Events.Select(e => Describe(e.Event)));
        var failed = h.Events.Of<GradingFailedDto>().Single();
        Assert.Equal(GradingJobProcessor.FailedMessage, failed.ErrorMessage);
        Assert.DoesNotContain("secret", failed.ErrorMessage);
        Assert.Equal(1, h.Grader.Attempts[$"{session}/Q1"]); // non-transient errors are not retried
    }

    [Fact]
    public async Task Failed_result_with_keyword_fallback_is_reported_as_failed_with_fallback_flag()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1");
        h.Grader.Behavior = (s, q, _) => new QuestionGradingResultDto
        {
            SessionId = s, QuestionId = q, State = GradingState.Failed, AiProvider = GradingJobProcessor.KeywordFallbackProvider
        };

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.True(h.Events.Of<GradingFailedDto>().Single().FallbackTriggered);
        Assert.Empty(h.Events.Of<GradingCompletedDto>());
    }

    [Fact]
    public async Task Skips_session_that_is_not_completed_or_missing()
    {
        var h = new Harness();
        var running = h.Data.AddSession("student-a", VivaSessionState.Answering);

        await h.Processor().ProcessAsync(new GradingJob(running), CancellationToken.None);
        await h.Processor().ProcessAsync(new GradingJob(Guid.NewGuid()), CancellationToken.None);

        Assert.Empty(h.Grader.Calls);
        Assert.Empty(h.Events.Events);
    }

    [Fact]
    public async Task Without_registered_orchestrator_job_is_skipped_safely()
    {
        var h = new Harness(registerOrchestrator: false);
        var session = h.Data.AddCompletedSession("student-a", "Q1");

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.Empty(h.Events.Events);
    }

    // ---- RT-04: bounded retry ----

    [Fact]
    public async Task Transient_errors_are_retried_then_succeed()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1");
        h.Grader.Behavior = (_, _, attempt) => attempt <= 2
            ? throw new HttpRequestException("busy", null, HttpStatusCode.ServiceUnavailable)
            : null;

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.Equal(3, h.Grader.Attempts[$"{session}/Q1"]);
        Assert.Single(h.Events.Of<GradingStartedDto>());
        Assert.Single(h.Events.Of<GradingCompletedDto>());
    }

    [Fact]
    public async Task Transient_errors_stop_after_three_retries_and_publish_failed()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1");
        h.Grader.Behavior = (_, _, _) => throw new TimeoutException("provider timeout");

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.Equal(4, h.Grader.Attempts[$"{session}/Q1"]); // 1 attempt + 3 retries
        Assert.Single(h.Events.Of<GradingFailedDto>());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    public void Http_status_classification(HttpStatusCode status, bool transient) =>
        Assert.Equal(transient, GradingJobProcessor.IsTransient(new HttpRequestException("x", null, status), CancellationToken.None));

    [Fact]
    public void Backoff_is_exponential_with_bounded_jitter()
    {
        var h = new Harness();
        h.Options.RetryBaseDelay = TimeSpan.FromSeconds(2);
        var processor = h.Processor();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var expected = TimeSpan.FromSeconds(2 * Math.Pow(2, attempt));
            var delay = processor.BackoffDelay(attempt);
            Assert.InRange(delay, expected, expected * 1.2);
        }
    }

    [Fact]
    public async Task Cancellation_stops_processing_without_publishing_failed()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1");
        h.Grader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var run = h.Processor().ProcessAsync(new GradingJob(session), cts.Token);
        await GradingFixture.WaitUntilAsync(() => !h.Grader.Calls.IsEmpty);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Empty(h.Events.Of<GradingFailedDto>());
    }

    // ---- RT-03: regrade guards ----

    [Fact]
    public async Task Regrade_never_touches_finalized_questions()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1", "Q2");
        var finalized = h.Grader.Seed(session, "Q1", GradingState.Finalized, 9m);
        h.Grader.Seed(session, "Q2", GradingState.AiGraded);

        await h.Processor().ProcessAsync(new GradingJob(session, Kind: GradingJobKind.Regrade, RequestedBy: "lecturer-1"),
            CancellationToken.None);

        Assert.Equal(new[] { "regrade:Q2" }, h.Grader.Calls);
        Assert.Same(finalized, h.Grader.Results[(session, "Q1")]);
        Assert.DoesNotContain(h.Events.Events, e => Describe(e.Event).EndsWith(":Q1"));
    }

    [Fact]
    public async Task Initial_grading_is_idempotent_and_skips_already_graded_questions()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1", "Q2");
        h.Grader.Seed(session, "Q1", GradingState.AiGraded);
        h.Grader.Seed(session, "Q2", GradingState.Pending);

        await h.Processor().ProcessAsync(new GradingJob(session), CancellationToken.None);

        Assert.Equal(new[] { "grade:Q2" }, h.Grader.Calls);
    }

    [Fact]
    public async Task Concurrency_conflict_keeps_lecturer_score_and_publishes_no_result()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1");
        h.Grader.Behavior = (_, _, _) => throw new GradingConflictException("RowVersion changed");

        await h.Processor().ProcessAsync(new GradingJob(session, "Q1", GradingJobKind.Regrade), CancellationToken.None);

        Assert.Equal(1, h.Grader.Attempts[$"{session}/Q1"]);
        Assert.Empty(h.Events.Of<GradingCompletedDto>());
        Assert.Empty(h.Events.Of<GradingFailedDto>());
    }

    [Fact]
    public async Task Regrade_request_is_queued_once_and_duplicates_are_rejected()
    {
        var h = new Harness();
        var session = h.Data.AddCompletedSession("student-a", "Q1");
        h.Grader.Seed(session, "Q1", GradingState.AiGraded);

        Assert.Equal(RegradeRequestResult.Queued, await h.Regrade().RequestRegradeAsync(session, "Q1", "lecturer-1"));
        Assert.Equal(RegradeRequestResult.AlreadyInProgress, await h.Regrade().RequestRegradeAsync(session, "Q1", "lecturer-2"));
        Assert.Equal(RegradeRequestResult.AlreadyInProgress, await h.Regrade().RequestRegradeAsync(session, null, "lecturer-2"));
        Assert.Equal(1, h.Queue.PendingCount);
    }

    [Fact]
    public async Task Regrade_request_validation()
    {
        var h = new Harness();
        var finalizedSession = h.Data.AddCompletedSession("student-a", "Q1");
        h.Grader.Seed(finalizedSession, "Q1", GradingState.Finalized);
        var running = h.Data.AddSession("student-b", VivaSessionState.Answering);

        Assert.Equal(RegradeRequestResult.AlreadyFinalized, await h.Regrade().RequestRegradeAsync(finalizedSession, "Q1", "lecturer"));
        Assert.Equal(RegradeRequestResult.AlreadyFinalized, await h.Regrade().RequestRegradeAsync(finalizedSession, null, "lecturer"));
        Assert.Equal(RegradeRequestResult.SessionNotCompleted, await h.Regrade().RequestRegradeAsync(running, "Q1", "lecturer"));
        Assert.Equal(RegradeRequestResult.SessionNotFound, await h.Regrade().RequestRegradeAsync(Guid.NewGuid(), "Q1", "lecturer"));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Regrade().RequestRegradeAsync(Guid.Empty, "Q1", "lecturer"));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Regrade().RequestRegradeAsync(finalizedSession, "Q1", " "));
        Assert.Equal(0, h.Queue.PendingCount);
    }

    // ---- RT-04: startup recovery ----

    [Fact]
    public async Task Recovery_scanner_finds_only_unfinished_completed_sessions()
    {
        var h = new Harness();
        var noRows = h.Data.AddCompletedSession("s1", "Q1");
        var pending = h.Data.AddCompletedSession("s2", "Q1");
        h.Grader.Seed(pending, "Q1", GradingState.Pending);
        var partial = h.Data.AddCompletedSession("s3", "Q1", "Q2");
        h.Grader.Seed(partial, "Q1", GradingState.AiGraded);
        var done = h.Data.AddCompletedSession("s4", "Q1");
        h.Grader.Seed(done, "Q1", GradingState.AiGraded);
        var failed = h.Data.AddCompletedSession("s5", "Q1");
        h.Grader.Seed(failed, "Q1", GradingState.Failed);
        h.Data.AddSession("s6", VivaSessionState.Answering);

        var jobs = await h.Scanner().FindUnfinishedJobsAsync();

        Assert.Equal(new[] { noRows, pending, partial }.OrderBy(x => x), jobs.Select(j => j.SessionId).OrderBy(x => x));
        Assert.All(jobs, j => Assert.Equal(GradingJobKind.Initial, j.Kind));
        Assert.All(jobs, j => Assert.Equal("EXAM01", j.ExamId));
    }

    [Fact]
    public async Task Worker_recovers_pending_sessions_on_startup_without_duplicates()
    {
        var h = new Harness();
        h.Options.RecoverOnStartup = true;
        var pending = h.Data.AddCompletedSession("student-a", "Q1", "Q2");
        h.Grader.Seed(pending, "Q1", GradingState.Pending);
        // The same session was also queued live before recovery ran: it must be graded once.
        Assert.True(h.Queue.TryEnqueue(new GradingJob(pending, ExamId: "EXAM01")));
        var worker = h.Worker();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await GradingFixture.WaitUntilAsync(() => h.Events.Of<GradingCompletedDto>().Count() == 2 && !h.Queue.IsInFlight(pending));
        }
        finally { await worker.StopAsync(CancellationToken.None); }

        Assert.Equal(new[] { "grade:Q1", "grade:Q2" }, h.Grader.Calls);
    }

    [Fact]
    public async Task Worker_processes_one_job_at_a_time_and_survives_crashing_jobs()
    {
        var h = new Harness();
        var first = h.Data.AddCompletedSession("student-a", "Q1");
        var second = h.Data.AddCompletedSession("student-b", "Q1");
        h.Grader.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = h.Worker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(h.Queue.TryEnqueue(new GradingJob(first)));
            Assert.True(h.Queue.TryEnqueue(new GradingJob(Guid.NewGuid()))); // unknown session → skipped
            Assert.True(h.Queue.TryEnqueue(new GradingJob(second)));
            await GradingFixture.WaitUntilAsync(() => h.Grader.Calls.Count == 1);
            Assert.False(h.Queue.TryEnqueue(new GradingJob(first))); // running → duplicate rejected
            h.Grader.Gate.SetResult();
            await GradingFixture.WaitUntilAsync(() => h.Events.Of<GradingCompletedDto>().Count() == 2);
        }
        finally { await worker.StopAsync(CancellationToken.None); }

        Assert.Equal(1, h.Grader.MaxConcurrent);
        await GradingFixture.WaitUntilAsync(() => !h.Queue.IsInFlight(first) && !h.Queue.IsInFlight(second));
    }

    // ---- RT-01: hook in VivaSessionOrchestrator.CompleteSessionAsync ----

    [Fact]
    public async Task Completing_a_viva_session_queues_grading_after_persisting()
    {
        var h = new Harness();
        var session = h.Data.AddSession("student-a", VivaSessionState.Answering);
        var ai = new RealtimeAi();
        var orchestrator = new VivaSessionOrchestrator(h.Data, h.Data, h.Data, ai, ai, ai, new RecordingPublisher(),
            NullLogger<VivaSessionOrchestrator>.Instance, timers: null, gradingQueue: h.Queue);

        await orchestrator.CompleteSessionAsync(session);

        Assert.Equal((byte)VivaSessionState.Completed, h.Data.Sessions[session].State);
        Assert.True(h.Queue.IsInFlight(session));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var job in h.Queue.ReadAllAsync(cts.Token))
        {
            Assert.Equal(new GradingJob(session, ExamId: "EXAM01"), job);
            break;
        }
    }

    [Fact]
    public async Task Failed_completion_does_not_queue_grading()
    {
        var h = new Harness();
        var session = h.Data.AddSession("student-a", VivaSessionState.Answering);
        h.Data.FailSave = true;
        var ai = new RealtimeAi();
        var orchestrator = new VivaSessionOrchestrator(h.Data, h.Data, h.Data, ai, ai, ai, new RecordingPublisher(),
            NullLogger<VivaSessionOrchestrator>.Instance, timers: null, gradingQueue: h.Queue);

        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.CompleteSessionAsync(session));

        Assert.False(h.Queue.IsInFlight(session));
    }
}
