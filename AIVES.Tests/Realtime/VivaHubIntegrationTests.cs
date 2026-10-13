using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.Tests.Realtime;

public sealed class VivaHubIntegrationTests
{
    private sealed class Factory : WebApplicationFactory<global::Program>
    {
        public RealtimeData Data { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("VivaInterview:Enabled", "true");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "true",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=SignalRFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "fixture@example.test",
                ["DefaultAdmin:Password"] = "fixture-only-not-a-real-account"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IVivaSessionRepository>(Data);
                services.AddSingleton<IVivaQuestionRepository>(Data);
                services.AddSingleton<IVivaTurnRepository>(Data);
                var ai = new RealtimeAi();
                services.AddSingleton<ISpeechToTextService>(ai);
                services.AddSingleton<ITextToSpeechService>(ai);
                services.AddSingleton<IFollowUpGeneratorService>(ai);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Fixture";
                    options.DefaultChallengeScheme = "Fixture";
                }).AddScheme<AuthenticationSchemeOptions, TestAuthentication>("Fixture", _ => { });
                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }

        public HubConnection ConnectAs(string? student)
        {
            _ = Server; // Initialize actual Program, middleware, DI and route.
            return new HubConnectionBuilder().WithUrl("https://localhost/hubs/viva", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                if (student is not null) options.Headers["X-Fixture-Student"] = student;
            }).AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
                .Build();
        }
        public async Task PublishAsync(Func<IVivaRealtimePublisher, Task> action)
        {
            await using var scope = Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<IVivaRealtimePublisher>());
        }
        public async Task<T> OrchestrateAsync<T>(Func<IVivaSessionOrchestrator, Task<T>> action)
        {
            await using var scope = Services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IVivaSessionOrchestrator>());
        }
    }

    // Exists only in this test assembly. Production authentication continues to use cookies.
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var student = Request.Headers["X-Fixture-Student"].ToString();
            if (string.IsNullOrWhiteSpace(student)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = student == "missing-claim" ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.NameIdentifier, student) };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }
    }

    private static Task<T> Next<T>(Channel<T> events) => events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    private static VivaTranscriptDto Transcript(Guid id) => new(id, Guid.NewGuid(), "Q1", "Only this student sees this.", .95m, DateTime.UtcNow);

    [Fact]
    public async Task Anonymous_connection_is_rejected_by_real_authorization_middleware()
    {
        await using var factory = new Factory();
        await using var connection = factory.ConnectAs(null);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
    }

    [Theory]
    [InlineData("student-b")]
    [InlineData("missing-claim")]
    public async Task Unauthorized_join_never_receives_private_transcript(string user)
    {
        await using var factory = new Factory();
        var id = factory.Data.AddSession("student-a");
        await using var intruder = factory.ConnectAs(user);
        await using var owner = factory.ConnectAs("student-a");
        var leaked = Channel.CreateUnbounded<VivaTranscriptDto>();
        var received = Channel.CreateUnbounded<VivaTranscriptDto>();
        intruder.On<VivaTranscriptDto>("TranscriptReady", dto => leaked.Writer.TryWrite(dto));
        owner.On<VivaTranscriptDto>("TranscriptReady", dto => received.Writer.TryWrite(dto));
        await intruder.StartAsync();
        await owner.StartAsync();
        var error = await Assert.ThrowsAsync<HubException>(() => intruder.InvokeAsync("JoinSession", id));
        Assert.Contains("Unauthorized", error.Message);
        await owner.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        await factory.PublishAsync(p => p.PublishTranscriptReadyAsync(Transcript(id)));
        Assert.Equal(id, (await Next(received)).SessionId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaked.Reader.ReadAsync(deadline.Token).AsTask());
    }

    [Fact]
    public async Task Two_sessions_are_isolated_and_leave_removes_only_the_callers_membership()
    {
        await using var factory = new Factory();
        var a = factory.Data.AddSession("student-a");
        var b = factory.Data.AddSession("student-b");
        await using var first = factory.ConnectAs("student-a");
        await using var second = factory.ConnectAs("student-b");
        var firstEvents = Channel.CreateUnbounded<VivaTranscriptDto>();
        var secondEvents = Channel.CreateUnbounded<VivaTranscriptDto>();
        first.On<VivaTranscriptDto>("TranscriptReady", dto => firstEvents.Writer.TryWrite(dto));
        second.On<VivaTranscriptDto>("TranscriptReady", dto => secondEvents.Writer.TryWrite(dto));
        await first.StartAsync(); await second.StartAsync();
        await first.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", a);
        await second.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", b);
        await factory.PublishAsync(p => p.PublishTranscriptReadyAsync(Transcript(a)));
        await factory.PublishAsync(p => p.PublishTranscriptReadyAsync(Transcript(b)));
        Assert.Equal(a, (await Next(firstEvents)).SessionId);
        Assert.Equal(b, (await Next(secondEvents)).SessionId);
        await first.InvokeAsync("LeaveSession", a);
        await factory.PublishAsync(p => p.PublishTranscriptReadyAsync(Transcript(a)));
        await factory.PublishAsync(p => p.PublishTranscriptReadyAsync(Transcript(b)));
        Assert.Equal(b, (await Next(secondEvents)).SessionId);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstEvents.Reader.ReadAsync(deadline.Token).AsTask());
        Assert.False(secondEvents.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Reconnect_reauthorizes_returns_question_and_original_timer_without_notifying_other_clients()
    {
        await using var factory = new Factory();
        var id = factory.Data.AddSession("student-a");
        await using var first = factory.ConnectAs("student-a");
        await using var peer = factory.ConnectAs("student-a");
        var peerStatuses = Channel.CreateUnbounded<VivaSessionStatusDto>();
        peer.On<VivaSessionStatusDto>("SessionStatusChanged", dto => peerStatuses.Writer.TryWrite(dto));
        await first.StartAsync(); await peer.StartAsync();
        var initial = await first.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        Assert.Equal("Q1", initial.CurrentQuestion!.QuestionId);
        Assert.Equal("/audio/question.mp3", initial.CurrentQuestion.AudioUrl);
        await peer.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        await Next(peerStatuses);
        await first.InvokeAsync<VivaTimeRemainingDto>("BeginAnswering", id);
        Assert.Equal(VivaSessionState.Answering, (await Next(peerStatuses)).State);
        var before = factory.Services.GetRequiredService<AIVES.BLL.Services.VivaInterview.VivaTimerManager>().GetDeadline(id);
        await first.StopAsync();
        await first.StartAsync();
        var recovered = await first.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        Assert.Equal(VivaSessionState.Answering, recovered.Status.State);
        Assert.Equal("Q1", recovered.CurrentQuestion!.QuestionId);
        Assert.InRange(recovered.Timer!.SecondsRemaining, 1, 60);
        Assert.Equal(before, factory.Services.GetRequiredService<AIVES.BLL.Services.VivaInterview.VivaTimerManager>().GetDeadline(id));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peerStatuses.Reader.ReadAsync(deadline.Token).AsTask());
    }

    [Fact]
    public async Task Recovery_replays_persisted_followup_and_transcript_to_caller_only()
    {
        await using var factory = new Factory();
        var id = factory.Data.AddSession("student-a", VivaSessionState.FollowUpDelivered);
        var turn = Guid.NewGuid();
        factory.Data.Turns[turn] = new() { SessionId = id, TurnId = turn, QuestionId = "Q1", TurnOrdinal = 1,
            TranscriptText = "Saved answer", ConfidenceScore = .9m, FollowUpQuestionText = "Saved follow-up",
            FollowUpAudioUrl = "/audio/saved.mp3", CreatedAtUtc = DateTime.UtcNow };
        await using var connection = factory.ConnectAs("student-a");
        var followups = Channel.CreateUnbounded<VivaFollowUpDto>();
        var transcripts = Channel.CreateUnbounded<VivaTranscriptDto>();
        connection.On<VivaFollowUpDto>("FollowUpGenerated", dto => followups.Writer.TryWrite(dto));
        connection.On<VivaTranscriptDto>("TranscriptReady", dto => transcripts.Writer.TryWrite(dto));
        await connection.StartAsync();
        var snapshot = await connection.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        Assert.Equal("Saved follow-up", snapshot.CurrentQuestion!.Content);
        Assert.Equal("/audio/saved.mp3", (await Next(followups)).AudioUrl);
        Assert.Equal("Saved answer", (await Next(transcripts)).TranscriptText);
    }

    [Fact]
    public async Task All_six_events_roundtrip_with_contract_payloads_and_string_state()
    {
        await using var factory = new Factory();
        var id = factory.Data.AddSession("student-a");
        await using var connection = factory.ConnectAs("student-a");
        var status = Channel.CreateUnbounded<System.Text.Json.JsonElement>();
        var transcript = Channel.CreateUnbounded<VivaTranscriptDto>();
        var followup = Channel.CreateUnbounded<VivaFollowUpDto>();
        var timer = Channel.CreateUnbounded<VivaTimeRemainingDto>();
        var complete = Channel.CreateUnbounded<VivaSessionStatusDto>();
        var error = Channel.CreateUnbounded<VivaErrorDto>();
        connection.On<System.Text.Json.JsonElement>("SessionStatusChanged", dto => status.Writer.TryWrite(dto));
        connection.On<VivaTranscriptDto>("TranscriptReady", dto => transcript.Writer.TryWrite(dto));
        connection.On<VivaFollowUpDto>("FollowUpGenerated", dto => followup.Writer.TryWrite(dto));
        connection.On<VivaTimeRemainingDto>("TimeRemainingChanged", dto => timer.Writer.TryWrite(dto));
        connection.On<VivaSessionStatusDto>("SessionCompleted", dto => complete.Writer.TryWrite(dto));
        connection.On<VivaErrorDto>("VivaError", dto => error.Writer.TryWrite(dto));
        await connection.StartAsync();
        await connection.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        var joinStatus = await Next(status);
        Assert.Equal("QuestionDelivered", joinStatus.GetProperty("state").GetString());
        Assert.Equal(id, joinStatus.GetProperty("sessionId").GetGuid());
        var turnId = Guid.NewGuid();
        await factory.PublishAsync(p => p.PublishStatusChangedAsync(new(id, "EXAM01", "student-a", VivaSessionState.Transcribing, 1, 2, DateTime.UtcNow)));
        await factory.PublishAsync(p => p.PublishTranscriptReadyAsync(Transcript(id)));
        await factory.PublishAsync(p => p.PublishFollowUpGeneratedAsync(new(id, turnId, "Q1-FU1", "Follow-up", 1, "/audio/fu.mp3")));
        await factory.PublishAsync(p => p.PublishTimeRemainingAsync(new(id, 12, 60)));
        await factory.PublishAsync(p => p.PublishErrorAsync(new(id, "FIXTURE_ERROR", "Retry", true)));
        Assert.Equal("Transcribing", (await Next(status)).GetProperty("state").GetString());
        Assert.Equal(id, (await Next(transcript)).SessionId);
        Assert.Equal(turnId, (await Next(followup)).TurnId);
        Assert.Equal(12, (await Next(timer)).SecondsRemaining);
        Assert.True((await Next(error)).CanRetry);
        await factory.Data.CompleteSessionAsync(id, DateTime.UtcNow);
        await factory.PublishAsync(p => p.PublishSessionCompletedAsync(id, "EXAM01", "student-a"));
        Assert.Equal(VivaSessionState.Completed, (await Next(complete)).State);
    }

    [Fact]
    public async Task Real_BLL_flow_publishes_transcript_followup_and_completion_and_stops_timers()
    {
        await using var factory = new Factory();
        var id = factory.Data.AddSession("student-a", VivaSessionState.Created);
        await using var connection = factory.ConnectAs("student-a");
        var transcripts = Channel.CreateUnbounded<VivaTranscriptDto>();
        var followups = Channel.CreateUnbounded<VivaFollowUpDto>();
        var completed = Channel.CreateUnbounded<VivaSessionStatusDto>();
        connection.On<VivaTranscriptDto>("TranscriptReady", dto => transcripts.Writer.TryWrite(dto));
        connection.On<VivaFollowUpDto>("FollowUpGenerated", dto => followups.Writer.TryWrite(dto));
        connection.On<VivaSessionStatusDto>("SessionCompleted", dto => completed.Writer.TryWrite(dto));
        await connection.StartAsync();
        Assert.Equal(VivaSessionState.Ready, (await connection.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id)).Status.State);
        var question = await factory.OrchestrateAsync(o => o.StartSessionAsync(id));
        await connection.InvokeAsync<VivaTimeRemainingDto>("BeginAnswering", id);
        var first = await factory.OrchestrateAsync(o => o.SubmitAnswerAudioAsync(new(id, question.QuestionId, new byte[] { 1 }, "audio/webm", 1)));
        Assert.Equal(first.TurnId, (await Next(transcripts)).TurnId);
        var followup = await factory.OrchestrateAsync(o => o.GenerateFollowUpAsync(id, first.TurnId));
        Assert.Equal(followup!.FollowUpQuestionId, (await Next(followups)).FollowUpQuestionId);
        await connection.InvokeAsync<VivaTimeRemainingDto>("BeginAnswering", id);
        var second = await factory.OrchestrateAsync(o => o.SubmitAnswerAudioAsync(new(id, followup.FollowUpQuestionId, new byte[] { 2 }, "audio/webm", 1)));
        Assert.Equal(second.TurnId, (await Next(transcripts)).TurnId);
        Assert.Null(await factory.OrchestrateAsync(o => o.GenerateFollowUpAsync(id, second.TurnId)));
        Assert.Equal(2, (await Next(completed)).CurrentTurnOrdinal);
        Assert.Equal((byte)VivaSessionState.Completed, factory.Data.Sessions[id].State);
        Assert.Empty(factory.Services.GetRequiredService<AIVES.BLL.Services.VivaInterview.VivaTimerManager>().GetActiveSessionIds());
    }

    [Fact]
    public async Task Hosted_worker_times_out_after_student_disconnects_and_persists_before_event()
    {
        await using var factory = new Factory();
        factory.Data.Question.TimeLimitSeconds = 1;
        var id = factory.Data.AddSession("student-a");
        await using var student = factory.ConnectAs("student-a");
        await using var peer = factory.ConnectAs("student-a");
        var statuses = Channel.CreateUnbounded<VivaSessionStatusDto>();
        peer.On<VivaSessionStatusDto>("SessionStatusChanged", dto => statuses.Writer.TryWrite(dto));
        await student.StartAsync(); await peer.StartAsync();
        await student.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        await peer.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        await Next(statuses);
        await student.InvokeAsync<VivaTimeRemainingDto>("BeginAnswering", id);
        Assert.Equal(VivaSessionState.Answering, (await Next(statuses)).State);
        await student.StopAsync();
        Assert.Equal(VivaSessionState.TimedOut, (await Next(statuses)).State);
        Assert.Equal((byte)VivaSessionState.TimedOut, factory.Data.Sessions[id].State);
        Assert.Equal(1, factory.Data.TimeoutSaves);
        Assert.Empty(factory.Services.GetRequiredService<AIVES.BLL.Services.VivaInterview.VivaTimerManager>().GetActiveSessionIds());
    }

    [Fact]
    public async Task Reconnect_after_ownership_is_revoked_cannot_rejoin()
    {
        await using var factory = new Factory();
        var id = factory.Data.AddSession("student-a");
        await using var connection = factory.ConnectAs("student-a");
        await connection.StartAsync();
        await connection.InvokeAsync<VivaSessionSnapshotDto>("JoinSession", id);
        await connection.StopAsync();
        factory.Data.Sessions[id].StudentId = "student-b";
        await connection.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", id));
    }

    [Fact]
    public async Task Malformed_session_and_failed_snapshot_return_safe_errors()
    {
        await using var factory = new Factory();
        var id = factory.Data.AddSession("student-a", VivaSessionState.Created);
        await using var connection = factory.ConnectAs("student-a");
        await connection.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", Guid.Empty));
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", Guid.NewGuid()));
        factory.Data.FailSave = true;
        var failure = await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinSession", id));
        Assert.Contains("Unable to restore", failure.Message);
        Assert.DoesNotContain("Fixture persistence", failure.Message);
        Assert.Equal((byte)VivaSessionState.Created, factory.Data.Sessions[id].State);
    }
}
