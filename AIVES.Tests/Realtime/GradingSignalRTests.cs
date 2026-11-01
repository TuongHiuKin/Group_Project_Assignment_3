using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Security;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StudentNameMVC.Hubs;

namespace AIVES.Tests.Realtime;

// G4-RT-02 / RT-06: lecturer group security, payloads and the full queue → worker → SignalR pipeline.
public sealed class GradingSignalRTests
{
    private sealed class Factory : WebApplicationFactory<global::Program>
    {
        public RealtimeData Data { get; } = new();
        public FakeGradingOrchestrator Grader { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("VivaInterview:Enabled", "true");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "true",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=SignalRFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "fixture@example.test",
                ["DefaultAdmin:Password"] = "fixture-only-not-a-real-account",
                ["Grading:Worker:RetryBaseDelay"] = "00:00:00",
                ["Grading:Worker:RecoverOnStartup"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IVivaSessionRepository>(Data);
                services.AddSingleton<IVivaQuestionRepository>(Data);
                services.AddSingleton<IVivaTurnRepository>(Data);
                services.AddSingleton<IGradingOrchestrator>(Grader);
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

        public HubConnection ConnectAs(string user, string? role)
        {
            _ = Server;
            return new HubConnectionBuilder().WithUrl("https://localhost/hubs/viva", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.Headers["X-Fixture-User"] = user;
                if (role is not null) options.Headers["X-Fixture-Role"] = role;
            }).AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
                .Build();
        }

        public async Task PublishAsync(Func<IVivaRealtimePublisher, Task> action)
        {
            await using var scope = Services.CreateAsyncScope();
            await action(scope.ServiceProvider.GetRequiredService<IVivaRealtimePublisher>());
        }
    }

    // Test-only authentication: user id and role come from headers.
    private sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Fixture-User"].ToString();
            if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
            var role = Request.Headers["X-Fixture-Role"].ToString();
            if (!string.IsNullOrWhiteSpace(role)) claims.Add(new Claim(ClaimTypes.Role, role));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name, ClaimTypes.NameIdentifier, ClaimTypes.Role));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            return Task.CompletedTask;
        }
    }

    private static Task<T> Next<T>(Channel<T> events) => events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

    private static async Task AssertSilent<T>(Channel<T> events)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => events.Reader.ReadAsync(deadline.Token).AsTask());
    }

    [Theory]
    [InlineData("student-a", "Student")]
    [InlineData("staff-a", ApplicationRoles.Staff)]
    [InlineData("no-role", null)]
    public async Task Non_lecturers_cannot_join_grading_group_or_receive_events(string user, string? role)
    {
        await using var factory = new Factory();
        await using var intruder = factory.ConnectAs(user, role);
        var leaked = Channel.CreateUnbounded<GradingCompletedDto>();
        intruder.On<GradingCompletedDto>("GradingCompleted", dto => leaked.Writer.TryWrite(dto));
        await intruder.StartAsync();

        var error = await Assert.ThrowsAsync<HubException>(() => intruder.InvokeAsync("JoinGradingGroup", "EXAM01"));
        Assert.Contains("Unauthorized", error.Message);
        await factory.PublishAsync(p => p.PublishGradingCompletedAsync("EXAM01",
            new GradingCompletedDto(Guid.NewGuid(), "Q1", 1, 7m, 10m, true)));
        await AssertSilent(leaked);

        var regrade = await Assert.ThrowsAsync<HubException>(() => intruder.InvokeAsync<string>("RequestRegrade", Guid.NewGuid(), "Q1"));
        Assert.Contains("Unauthorized", regrade.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EXAM 01")]
    [InlineData("../EXAM01")]
    public async Task Invalid_exam_id_is_rejected(string examId)
    {
        await using var factory = new Factory();
        await using var lecturer = factory.ConnectAs("lecturer-1", ApplicationRoles.Lecturer);
        await lecturer.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => lecturer.InvokeAsync("JoinGradingGroup", examId));
    }

    [Fact]
    public async Task Lecturer_receives_contract_payloads_only_for_the_joined_exam()
    {
        await using var factory = new Factory();
        await using var exam1 = factory.ConnectAs("lecturer-1", ApplicationRoles.Lecturer);
        await using var exam2 = factory.ConnectAs("admin-1", ApplicationRoles.Admin);
        var started = Channel.CreateUnbounded<GradingStartedDto>();
        var completed = Channel.CreateUnbounded<JsonElement>();
        var failed = Channel.CreateUnbounded<GradingFailedDto>();
        var other = Channel.CreateUnbounded<object>();
        exam1.On<GradingStartedDto>("GradingStarted", dto => started.Writer.TryWrite(dto));
        exam1.On<JsonElement>("GradingCompleted", json => completed.Writer.TryWrite(json));
        exam1.On<GradingFailedDto>("GradingFailed", dto => failed.Writer.TryWrite(dto));
        exam2.On<GradingStartedDto>("GradingStarted", dto => other.Writer.TryWrite(dto));
        exam2.On<GradingCompletedDto>("GradingCompleted", dto => other.Writer.TryWrite(dto));
        await exam1.StartAsync();
        await exam2.StartAsync();
        await exam1.InvokeAsync("JoinGradingGroup", "exam01"); // case-insensitive group
        await exam2.InvokeAsync("JoinGradingGroup", "EXAM02");

        var session = Guid.NewGuid();
        var at = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        await factory.PublishAsync(p => p.PublishGradingStartedAsync("EXAM01", new GradingStartedDto(session, "Q1", at)));
        await factory.PublishAsync(p => p.PublishGradingCompletedAsync("EXAM01", new GradingCompletedDto(session, "Q1", 1, 7.5m, 10m, true)));
        await factory.PublishAsync(p => p.PublishGradingFailedAsync("EXAM01", new GradingFailedDto(session, "Q2", "Cần chấm lại", true)));

        Assert.Equal(new GradingStartedDto(session, "Q1", at), await Next(started));
        var json = await Next(completed);
        Assert.Equal(session, json.GetProperty("sessionId").GetGuid());
        Assert.Equal("Q1", json.GetProperty("questionId").GetString());
        Assert.Equal(1, json.GetProperty("state").GetByte()); // numeric GradingState, as in the contract
        Assert.Equal(7.5m, json.GetProperty("earnedScore").GetDecimal());
        Assert.Equal(10m, json.GetProperty("maxScore").GetDecimal());
        Assert.True(json.GetProperty("isAiGraded").GetBoolean());
        Assert.Equal(new GradingFailedDto(session, "Q2", "Cần chấm lại", true), await Next(failed));
        await AssertSilent(other);

        await exam1.InvokeAsync("LeaveGradingGroup", "EXAM01");
        await factory.PublishAsync(p => p.PublishGradingStartedAsync("EXAM01", new GradingStartedDto(session, "Q3", at)));
        await AssertSilent(started);
    }

    [Fact]
    public async Task Regrade_from_hub_runs_in_background_and_streams_started_then_completed()
    {
        await using var factory = new Factory();
        var session = factory.Data.AddCompletedSession("student-a", "Q1");
        factory.Grader.Seed(session, "Q1", GradingState.Failed);
        factory.Grader.Score = 8m;
        await using var lecturer = factory.ConnectAs("lecturer-1", ApplicationRoles.Lecturer);
        var events = Channel.CreateUnbounded<string>();
        lecturer.On<GradingStartedDto>("GradingStarted", dto => events.Writer.TryWrite($"started:{dto.SessionId}:{dto.QuestionId}"));
        lecturer.On<GradingCompletedDto>("GradingCompleted", dto => events.Writer.TryWrite($"completed:{dto.QuestionId}:{dto.State}:{dto.EarnedScore}"));
        await lecturer.StartAsync();
        await lecturer.InvokeAsync("JoinGradingGroup", "EXAM01");

        var result = await lecturer.InvokeAsync<string>("RequestRegrade", session, "Q1");

        Assert.Equal(nameof(RegradeRequestResult.Queued), result);
        Assert.Equal($"started:{session}:Q1", await Next(events));
        Assert.Equal("completed:Q1:1:8", await Next(events));
        Assert.Equal(new[] { "regrade:Q1" }, factory.Grader.Calls);
        Assert.Equal(GradingState.AiGraded, factory.Grader.Results[(session, "Q1")].State);
    }

    [Fact]
    public async Task Regrade_of_finalized_question_is_refused_and_nothing_is_broadcast()
    {
        await using var factory = new Factory();
        var session = factory.Data.AddCompletedSession("student-a", "Q1");
        factory.Grader.Seed(session, "Q1", GradingState.Finalized, 9m);
        await using var lecturer = factory.ConnectAs("lecturer-1", ApplicationRoles.Lecturer);
        var events = Channel.CreateUnbounded<object>();
        lecturer.On<GradingStartedDto>("GradingStarted", dto => events.Writer.TryWrite(dto));
        await lecturer.StartAsync();
        await lecturer.InvokeAsync("JoinGradingGroup", "EXAM01");

        Assert.Equal(nameof(RegradeRequestResult.AlreadyFinalized), await lecturer.InvokeAsync<string>("RequestRegrade", session, "Q1"));
        await AssertSilent(events);
        Assert.Empty(factory.Grader.Calls);
    }

    [Fact]
    public void Grading_group_name_is_normalized_and_validated()
    {
        Assert.Equal("grading-EXAM01", GradingGroup.Name("exam01"));
        Assert.Equal("grading-EXAM_2-B", GradingGroup.Name(" EXAM_2-b "));
        Assert.Throws<ArgumentException>(() => GradingGroup.Name("bad id"));
        Assert.Throws<ArgumentException>(() => GradingGroup.Name(null!));
    }
}
