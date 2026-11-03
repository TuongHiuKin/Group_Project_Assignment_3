using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudentNameMVC.Pages.Grading;
using Xunit;
using GradingState = AIVES.BLL.Models.Grading.GradingState;

namespace AIVES.Tests.Frontend;

public sealed class GradingPagesTests : IClassFixture<GradingPagesTests.GradingFactory>
{
    public sealed class GradingFactory : WebApplicationFactory<global::Program>
    {
        internal FakeGradingOrchestrator FakeOrchestrator { get; } = new();
        internal FakeClassReportService FakeReportService { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "false",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=GradingFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "admin@example.test",
                ["DefaultAdmin:Password"] = "fixture-only-pass"
            }));

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IGradingOrchestrator>(FakeOrchestrator);
                services.AddSingleton<IClassReportService>(FakeReportService);

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "GradingTestAuth";
                    options.DefaultChallengeScheme = "GradingTestAuth";
                }).AddScheme<AuthenticationSchemeOptions, GradingTestAuthHandler>("GradingTestAuth", _ => { });

                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }
    }

    private sealed class GradingTestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());

            var role = Request.Headers["X-Test-Role"].FirstOrDefault() ?? ApplicationRoles.Staff;
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user),
                new Claim(ClaimTypes.Name, $"Test User {user}"),
                new Claim(ClaimTypes.Role, role)
            };

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "GradingTestAuth")), "GradingTestAuth");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Redirect;
            Response.Headers.Location = "/Account/Login";
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = (int)HttpStatusCode.Redirect;
            Response.Headers.Location = "/Account/AccessDenied";
            return Task.CompletedTask;
        }
    }

    public sealed class FakeGradingOrchestrator : IGradingOrchestrator
    {
        public List<SessionGradingSummaryDto> Sessions { get; set; } = new();

        public FakeGradingOrchestrator()
        {
            ResetData();
        }

        public void ResetData()
        {
            Sessions = new List<SessionGradingSummaryDto>
            {
                new()
                {
                    SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ExamId = "EXAM01",
                    StudentId = "SV01",
                    StudentName = "Nguyễn Văn A",
                    SessionCompletedAtUtc = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc),
                    TotalMaxScore = 10m,
                    TotalAiScore = 8.5m,
                    TotalFinalScore = null,
                    OverallState = GradingState.AiGraded,
                    IsFullyFinalized = false
                },
                new()
                {
                    SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    ExamId = "EXAM01",
                    StudentId = "SV02",
                    StudentName = "Trần Thị B",
                    SessionCompletedAtUtc = new DateTime(2026, 11, 2, 14, 30, 0, DateTimeKind.Utc),
                    TotalMaxScore = 10m,
                    TotalAiScore = 7.0m,
                    TotalFinalScore = 9.0m,
                    OverallState = GradingState.Finalized,
                    IsFullyFinalized = true
                },
                new()
                {
                    SessionId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    ExamId = "EXAM02",
                    StudentId = "SV03",
                    StudentName = "Lê Văn C",
                    SessionCompletedAtUtc = new DateTime(2026, 11, 3, 9, 15, 0, DateTimeKind.Utc),
                    TotalMaxScore = 10m,
                    TotalAiScore = null,
                    TotalFinalScore = null,
                    OverallState = GradingState.Pending,
                    IsFullyFinalized = false
                },
                new()
                {
                    SessionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    ExamId = "EXAM01",
                    StudentId = "SV04",
                    StudentName = "Phạm Văn D",
                    SessionCompletedAtUtc = new DateTime(2026, 11, 3, 11, 0, 0, DateTimeKind.Utc),
                    TotalMaxScore = 10m,
                    TotalAiScore = null,
                    TotalFinalScore = null,
                    OverallState = GradingState.Failed,
                    IsFullyFinalized = false
                }
            };
        }

        public Task<List<SessionGradingSummaryDto>> GetGradingListByExamAsync(
            string examId,
            string? classId = null,
            GradingState? filterState = null,
            CancellationToken ct = default)
        {
            var query = Sessions.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(examId))
            {
                query = query.Where(s => string.Equals(s.ExamId, examId, StringComparison.OrdinalIgnoreCase));
            }
            if (filterState.HasValue)
            {
                query = query.Where(s => s.OverallState == filterState.Value);
            }
            return Task.FromResult(query.ToList());
        }

        public Task<SessionGradingSummaryDto?> GetSessionGradingAsync(Guid sessionId, CancellationToken ct = default)
        {
            return Task.FromResult(Sessions.FirstOrDefault(s => s.SessionId == sessionId));
        }

        public Task<SessionGradingSummaryDto> GradeSessionAsync(Guid sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QuestionGradingResultDto> GradeQuestionAsync(Guid sessionId, string questionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QuestionGradingResultDto> RegradeQuestionAsync(Guid sessionId, string questionId, string requestedBy, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<QuestionGradingResultDto> FinalizeScoreAsync(FinalizeScoreRequestDto request, string teacherId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    public sealed class FakeClassReportService : IClassReportService
    {
        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new List<ExamSettingsSnapshot>
            {
                new() { ExamId = "EXAM01", ExamTitle = "Lập trình C# Nâng cao" },
                new() { ExamId = "EXAM02", ExamTitle = "Kiến trúc Phần mềm" }
            });
        }

        public Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default)
            => Task.FromResult(new List<string> { "SE1801", "SE1802" });

        public Task<ClassStatisticsDto?> GetClassStatisticsAsync(string teacherId, string classId, string examId, int? specificAttemptOrdinal = null, CancellationToken ct = default)
            => Task.FromResult<ClassStatisticsDto?>(null);

        public Task<byte[]> ExportClassGradeSheetCsvAsync(string teacherId, string classId, string examId, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());

        public Task<StudentReportDto?> GetStudentExamDetailForTeacherAsync(string teacherId, string classId, string examId, string studentId, CancellationToken ct = default)
            => Task.FromResult<StudentReportDto?>(null);
    }

    private readonly GradingFactory _factory;

    public GradingPagesTests(GradingFactory factory)
    {
        _factory = factory;
        _factory.FakeOrchestrator.ResetData();
    }

    // ==========================================
    // 1. UNIT TESTS: IndexModel PageModel Logic
    // ==========================================

    [Fact]
    public async Task IndexModel_OnGetAsync_PopulatesAllMetricsAndSessions()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new IndexModel(fakeOrchestrator, fakeReportService, NullLogger<IndexModel>.Instance);

        var result = await model.OnGetAsync();

        Assert.IsType<PageResult>(result);
        Assert.Equal(4, model.TotalSessionsCount);
        Assert.Equal(1, model.PendingCount);
        Assert.Equal(1, model.AiGradedCount);
        Assert.Equal(1, model.FinalizedCount);
        Assert.Equal(1, model.FailedCount);
        Assert.Equal(4, model.Sessions.Count);
        Assert.Equal(2, model.AvailableExams.Count);
    }

    [Fact]
    public async Task IndexModel_OnGetAsync_FiltersByState()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new IndexModel(fakeOrchestrator, fakeReportService, NullLogger<IndexModel>.Instance)
        {
            SelectedState = GradingState.AiGraded
        };

        await model.OnGetAsync();

        Assert.Equal(4, model.TotalSessionsCount); // Total remains 4
        Assert.Single(model.Sessions);
        Assert.Equal("SV01", model.Sessions[0].StudentId);
        Assert.Equal(GradingState.AiGraded, model.Sessions[0].OverallState);
    }

    [Fact]
    public async Task IndexModel_OnGetAsync_FiltersBySearchKeyword()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new IndexModel(fakeOrchestrator, fakeReportService, NullLogger<IndexModel>.Instance)
        {
            SearchKeyword = "Trần Thị B"
        };

        await model.OnGetAsync();

        Assert.Single(model.Sessions);
        Assert.Equal("SV02", model.Sessions[0].StudentId);
    }

    [Fact]
    public async Task IndexModel_OnGetAsync_FiltersByExam()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new IndexModel(fakeOrchestrator, fakeReportService, NullLogger<IndexModel>.Instance)
        {
            SelectedExamId = "EXAM02"
        };

        await model.OnGetAsync();

        Assert.Equal(1, model.TotalSessionsCount);
        Assert.Single(model.Sessions);
        Assert.Equal("SV03", model.Sessions[0].StudentId);
    }

    [Fact]
    public void IndexModel_GetExamTitle_ReturnsExpectedFormattedTitle()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new IndexModel(fakeOrchestrator, fakeReportService, NullLogger<IndexModel>.Instance)
        {
            AvailableExams = new List<ExamSettingsSnapshot>
            {
                new() { ExamId = "EXAM01", ExamTitle = "Lập trình C#" }
            }
        };

        var title = model.GetExamTitle("EXAM01");
        Assert.Equal("Lập trình C# (EXAM01)", title);

        var unknown = model.GetExamTitle("EXAM99");
        Assert.Equal("EXAM99", unknown);
    }

    // ==========================================
    // 2. INTEGRATION TESTS: HTTP & Security
    // ==========================================

    [Fact]
    public async Task Get_GradingIndex_AnonymousUser_IsRedirectedToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Grading/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/Login", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Get_GradingIndex_StudentRole_IsForbidden()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Staff);

        var response = await client.GetAsync("/Grading/Index");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Get_GradingIndex_LecturerRole_ReturnsOkAndRendersGradingTable()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync("/Grading/Index");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        // 1. Check title & headers
        Assert.Contains("Danh Sách Bài Thi Cần Chấm & Duyệt Điểm", html);
        Assert.Contains("NHÓM 4 AI GRADING", html);
        Assert.Contains("HUMAN-IN-THE-LOOP", html);

        // 2. Check table rendered
        Assert.Contains("Nguyễn Văn A", html);
        Assert.Contains("MSSV: SV01", html);
        Assert.Contains("Lập trình C# Nâng cao (EXAM01)", html);

        // 3. Check badges
        Assert.Contains("AI chấm, chờ giảng viên duyệt", html);
        Assert.Contains("Điểm chính thức", html);
        Assert.Contains("Chờ chấm điểm", html);
        Assert.Contains("Cần chấm lại", html);

        // 4. Check score display
        Assert.Contains("8.5", html);
        Assert.Contains("9", html);

        // 5. Check action link to Review page
        Assert.Contains("/Grading/Review?sessionId=11111111-1111-1111-1111-111111111111", html);
    }

    [Fact]
    public async Task Get_GradingIndex_AdminRole_ReturnsOk()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Admin);

        var response = await client.GetAsync("/Grading/Index");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains("Danh Sách Bài Thi Cần Chấm & Duyệt Điểm", html);
    }

    [Fact]
    public async Task Get_GradingIndex_WithStateFilter_RendersMatchingRowsOnly()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        // Filter for State = 1 (AiGraded)
        var response = await client.GetAsync("/Grading/Index?SelectedState=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        Assert.Contains("Nguyễn Văn A", html);
        Assert.Contains("AI chấm, chờ giảng viên duyệt", html);
        Assert.DoesNotContain("Trần Thị B", html);
        Assert.DoesNotContain("Lê Văn C", html);
    }
}
