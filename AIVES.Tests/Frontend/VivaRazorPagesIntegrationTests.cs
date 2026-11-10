using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Security;
using AIVES.DAL.Repositories.Interfaces;
using AIVES.Tests.Realtime;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StudentNameMVC.Pages;
using Xunit;
using GradingState = AIVES.BLL.Models.ExamReporting.ReportGradingState;

namespace AIVES.Tests.Frontend;

public sealed class VivaRazorPagesIntegrationTests : IClassFixture<VivaRazorPagesIntegrationTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<global::Program>
    {
        internal RealtimeData Data { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("VivaInterview:Enabled", "true");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
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
                services.AddSingleton<IStudentReportService>(new TestStudentReportService());
                services.AddSingleton<IClassReportService>(new TestClassReportService());

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "FrontendFixture";
                    options.DefaultChallengeScheme = "FrontendFixture";
                }).AddScheme<AuthenticationSchemeOptions, FrontendTestAuth>("FrontendFixture", _ => { });

                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }
    }

    private sealed class FrontendTestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(header)) return Task.FromResult(AuthenticateResult.NoResult());

            var role = Request.Headers["X-Test-Role"].FirstOrDefault() ?? ApplicationRoles.Staff;
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, header),
                new Claim(ClaimTypes.Name, $"Candidate {header}"),
                new Claim(ClaimTypes.Role, role)
            };
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "FrontendFixture")), "FrontendFixture");
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

    private readonly Factory _factory;

    public VivaRazorPagesIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_VivaIndex_WithoutAuth_RedirectsToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Viva");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_VivaIndex_WithAuth_ReturnsSuccessAndRendersPage()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync("/Viva");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Phòng thi Vấn đáp AI", html);
        Assert.Contains("Kiểm tra Micro", html);
        Assert.Contains("Tham gia phòng thi theo mã phiên", html);
        Assert.Contains("Về Dashboard", html);
    }

    [Fact]
    public async Task Get_VivaExam_WithoutSessionId_RedirectsToIndex()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync("/Viva/Exam");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Viva", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_VivaExam_WithAuthorizedStudent_ReturnsSuccessAndRendersRoom()
    {
        var sessionId = _factory.Data.AddSession("SV01", VivaSessionState.Ready);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync($"/Viva/Exam/{sessionId}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(sessionId.ToString(), html);
        Assert.Contains("visualizerCanvas", html);
        Assert.Contains("btnStartRecord", html);
        Assert.Contains("countdownTimerDisplay", html);
        Assert.Contains("transcriptCard", html);
    }

    [Theory]
    [InlineData(VivaSessionState.Ready, true)]
    [InlineData(VivaSessionState.QuestionDelivered, true)]
    [InlineData(VivaSessionState.TranscriptReady, true)]
    [InlineData(VivaSessionState.FollowUpDelivered, true)]
    [InlineData(VivaSessionState.Answering, false)]
    public async Task Get_VivaExam_RecordButtonLockedUntilAnsweringWindowIsOpen(VivaSessionState state, bool expectDisabled)
    {
        var sessionId = _factory.Data.AddSession("SV01", state);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync($"/Viva/Exam/{sessionId}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        var button = System.Text.RegularExpressions.Regex.Match(html, "<button[^>]*id=\"btnStartRecord\"[^>]*>").Value;
        Assert.NotEmpty(button);
        Assert.Equal(expectDisabled, button.Contains("disabled=\"disabled\""));
    }

    [Fact]
    public void ExamClientScript_UnlocksMicOnlyAfterBeginAnswering_AndAnnouncesBrowserVoiceFallback()
    {
        // Static checks of the browser client (no JS test runner in this solution).
        var scriptPath = System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "StudentNameMVC", "wwwroot", "js", "viva-signalr-client.js");
        var script = System.IO.File.ReadAllText(scriptPath);

        static string MethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Missing {signature}");
            var next = System.Text.RegularExpressions.Regex.Match(source[(start + signature.Length)..], @"\n    (async )?[a-zA-Z]+\(.*\) \{\r?\n");
            return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
        }

        var begin = MethodBody(script, "async requestBeginAnswering(");
        var invokeAt = begin.IndexOf("invoke('BeginAnswering'", StringComparison.Ordinal);
        var readyAt = begin.IndexOf("setRecordingUiState('ready'", StringComparison.Ordinal);
        Assert.True(invokeAt > 0 && readyAt > invokeAt, "Mic must be unlocked only after BeginAnswering succeeds.");

        var followUp = MethodBody(script, "handleFollowUpGenerated(followUp) {");
        Assert.DoesNotContain("setRecordingUiState('ready'", followUp);
        Assert.Contains("setRecordingUiState('disabled'", followUp);

        var render = MethodBody(script, "renderQuestion(question, autoPlayTts = true) {");
        Assert.Contains("setRecordingUiState('disabled'", render);

        Assert.Contains("Giọng AI lỗi, đang dùng giọng trình duyệt.", script);
        var playTts = MethodBody(script, "playTts(text, audioEl, audioUrl, onEndCallback) {");
        Assert.Contains("notifyBrowserVoiceFallback(", playTts);
    }

    [Fact]
    public async Task Get_VivaExam_WithUnauthorizedStudent_RedirectsToAccessDenied()
    {
        var sessionId = _factory.Data.AddSession("SV01", VivaSessionState.Ready);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV_OTHER_STUDENT");

        var response = await client.GetAsync($"/Viva/Exam/{sessionId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Get_VivaExam_RendersLeaveRoomModalWithTwoButtons()
    {
        var sessionId = _factory.Data.AddSession("SV01", VivaSessionState.Ready);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync($"/Viva/Exam/{sessionId}");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"leaveRoomModal\"", html);
        Assert.Contains("Ở lại phòng", html);
        Assert.Contains("id=\"btnConfirmLeaveRoom\"", html);
        Assert.Contains("href=\"/Account/Dashboard\"", html);
    }

    [Fact]
    public async Task Get_Layout_WithAuth_RendersUserDropdownAndRemovesSidebarSettings()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");

        var response = await client.GetAsync("/Account/StudentDashboard");

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();

        // Dropdown toggle button with chevron & data-bs-toggle
        Assert.Contains("id=\"userProfileDropdown\"", html);
        Assert.Contains("data-bs-toggle=\"dropdown\"", html);
        Assert.Contains("bi-chevron-down", html);

        // Dropdown items
        Assert.Contains("/Account/Dashboard", html);
        Assert.Contains("Cài đặt", html);
        Assert.Contains("/Account/Logout", html);
        Assert.Contains("Đăng xuất", html);

        // Sidebar SETTINGS removed
        Assert.DoesNotContain("sidebar-category\">SETTINGS</div>", html);
    }

    [Fact]
    public async Task Get_ErrorPage_WithoutAuth_ReturnsSuccessAndRendersSharedLayout()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/Error");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("An error occurred while processing your request.", html);
        Assert.Contains("Request ID:", html);
        Assert.Contains("AIVES Viva Exam", html);
    }

    [Fact]
    public void ErrorModel_HasRequiredSecurityAndCacheAttributes_AndSetsRequestId()
    {
        Assert.NotNull(typeof(ErrorModel).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.NotNull(typeof(ErrorModel).GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        var cacheAttr = typeof(ErrorModel).GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cacheAttr);
        Assert.Equal(0, cacheAttr.Duration);
        Assert.Equal(ResponseCacheLocation.None, cacheAttr.Location);
        Assert.True(cacheAttr.NoStore);

        var model = new ErrorModel
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext { TraceIdentifier = "trace-step0-test" }
            }
        };

        model.OnGet();

        Assert.Equal("trace-step0-test", model.RequestId);
        Assert.True(model.ShowRequestId);
    }

    [Fact]
    public async Task Get_RootRoute_RendersLoginWithoutAmbiguousMatch()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/ClassReports?classId=EXAM01&examId=EXAM01")]
    [InlineData("/ClassReports?classId=EXAM01&examId=EXAM01&attemptOrdinal=1")]
    [InlineData("/ClassReports?classId=EXAM01&examId=EXAM01&attemptOrdinal=2")]
    public async Task Get_ClassReports_StudentDetailLinks_DoNotPinAnAttempt(string url)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var links = System.Text.RegularExpressions.Regex.Matches(html, "href=\"(/ClassReports/StudentDetail[^\"]*)\"")
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))
            .ToList();

        Assert.Equal(2, links.Count);
        Assert.Contains("/ClassReports/StudentDetail?studentId=1&classId=EXAM01&examId=EXAM01", links);
        Assert.All(links, link => Assert.DoesNotContain("attemptOrdinal", link, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Get_StudentDetail_WithoutAttemptOrdinal_SelectsNewestAttempt()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync("/ClassReports/StudentDetail?studentId=1&classId=EXAM01&examId=EXAM01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Matches("class=\"tab-pane fade show active\" id=\"pane-att-3\"", html);

        // B9: Bootstrap 5 only switches tabs inside [role="tablist"] and tracks the selection with .active.
        var tabList = System.Text.RegularExpressions.Regex.Match(html, "<div[^>]*role=\"tablist\"[^>]*>([\\s\\S]*?)</div>\\s*<div class=\"tab-content\"");
        Assert.True(tabList.Success, "Attempt buttons must be wrapped in a role=\"tablist\" container.");
        var tabs = System.Text.RegularExpressions.Regex.Matches(tabList.Groups[1].Value, "<button[^>]*>")
            .Select(m => m.Value).ToList();
        Assert.Equal(3, tabs.Count);
        Assert.All(tabs, t =>
        {
            Assert.Contains("role=\"tab\"", t);
            Assert.Contains("data-bs-toggle=\"pill\"", t);
        });
        var selected = Assert.Single(tabs, t => t.Contains(" active") && t.Contains("aria-selected=\"true\""));
        Assert.Contains("data-bs-target=\"#pane-att-3\"", selected);
        Assert.DoesNotMatch("show active\" id=\"pane-att-1\"", html);
        Assert.True(html.IndexOf("Lượt thi số 3", StringComparison.Ordinal) < html.IndexOf("Lượt thi số 1", StringComparison.Ordinal));
    }

    private sealed class TestClassReportService : IClassReportService
    {
        public Task<ClassStatisticsDto?> GetClassStatisticsAsync(string teacherId, string classId, string examId, int? specificAttemptOrdinal = null, CancellationToken ct = default)
            => Task.FromResult<ClassStatisticsDto?>(new ClassStatisticsDto
            {
                ClassId = classId,
                ExamId = examId,
                TotalStudentsCount = 2,
                FilteredAttemptOrdinal = specificAttemptOrdinal,
                GradeExportRows =
                [
                    new ClassGradeExportRowDto { StudentId = "1", StudentName = "Kieen", ClassId = classId, ResultStatus = "CHƯA ĐẠT" },
                    new ClassGradeExportRowDto { StudentId = "2", StudentName = "Sinh viên 2", ClassId = classId, ResultStatus = "Chưa chấm" }
                ]
            });

        public Task<byte[]> ExportClassGradeSheetCsvAsync(string teacherId, string classId, string examId, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<byte>());

        public Task<StudentReportDto?> GetStudentExamDetailForTeacherAsync(string teacherId, string classId, string examId, string studentId, CancellationToken ct = default)
            => Task.FromResult<StudentReportDto?>(new StudentReportDto
            {
                StudentId = studentId,
                StudentName = "Kieen",
                ExamId = examId,
                ExamTitle = "Đề thi EXAM01",
                // ClassReportService returns attempts newest first.
                Attempts = new[] { 3, 2, 1 }.Select(n => new AttemptSnapshotDto
                {
                    AttemptId = $"attempt-{n}",
                    StudentId = studentId,
                    ClassId = classId,
                    ExamId = examId,
                    AttemptOrdinal = n,
                    ExamState = ExamState.Completed,
                    GradingState = GradingState.Pending
                }).ToList()
            });

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<ExamSettingsSnapshot> { new() { ExamId = "EXAM01", ExamTitle = "Đề thi EXAM01" } });

        public Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default)
            => Task.FromResult(new List<string> { "EXAM01" });
    }

    private sealed class TestStudentReportService : IStudentReportService
    {
        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
            => Task.FromResult(new List<ExamSettingsSnapshot>());

        public Task<StudentReportDto?> GetStudentReportAsync(string studentId, string examId, CancellationToken ct = default)
            => Task.FromResult<StudentReportDto?>(null);
    }
}