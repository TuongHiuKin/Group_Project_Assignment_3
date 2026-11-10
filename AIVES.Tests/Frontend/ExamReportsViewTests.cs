using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using GradingState = AIVES.BLL.Models.ExamReporting.ReportGradingState;

namespace AIVES.Tests.Frontend;

public sealed class ExamReportsViewTests : IClassFixture<ExamReportsViewTests.ExamReportsFactory>
{
    public sealed class ExamReportsFactory : WebApplicationFactory<global::Program>
    {
        internal FakeStudentReportService FakeStudentService { get; } = new();
        internal FakeClassReportService FakeClassService { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "false",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=ReportsFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "admin@example.test",
                ["DefaultAdmin:Password"] = "fixture-only-pass"
            }));

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStudentReportService>(FakeStudentService);
                services.AddSingleton<IClassReportService>(FakeClassService);

                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "ReportsTestAuth";
                    options.DefaultChallengeScheme = "ReportsTestAuth";
                }).AddScheme<AuthenticationSchemeOptions, ReportsTestAuthHandler>("ReportsTestAuth", _ => { });

                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }
    }

    private sealed class ReportsTestAuthHandler(
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

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "ReportsTestAuth")), "ReportsTestAuth");
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
            Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return Task.CompletedTask;
        }
    }

    public sealed class FakeStudentReportService : IStudentReportService
    {
        public Task<StudentReportDto?> GetStudentReportAsync(string studentId, string examId, CancellationToken ct = default)
        {
            if (studentId == "SV01")
            {
                // SV01: Attempt 1 is AI-graded (not yet finalized)
                return Task.FromResult<StudentReportDto?>(new StudentReportDto
                {
                    StudentId = "SV01",
                    StudentName = "Nguyễn Văn A",
                    ExamId = examId,
                    ExamTitle = "Lập trình C# Nâng cao",
                    EvaluationMode = ExamEvaluationMode.Score,
                    CompletedAttemptsCount = 1,
                    MaxAllowedAttemptsCount = 3,
                    GradedAttemptsCount = 1,
                    AverageScore = 8.5m,
                    Attempts = new List<AttemptSnapshotDto>
                    {
                        new()
                        {
                            AttemptId = "ATT-AI-1",
                            StudentId = "SV01",
                            StudentName = "Nguyễn Văn A",
                            ClassId = "SE1801",
                            ExamId = examId,
                            AttemptOrdinal = 1,
                            ExamState = ExamState.Completed,
                            GradingState = GradingState.Graded,
                            CompletedAt = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc),
                            IsPassed = true,
                            QuestionResults = new List<QuestionResultDto>
                            {
                                new()
                                {
                                    QuestionId = "Q1",
                                    QuestionTitle = "Khái niệm Dependency Injection",
                                    EarnedScore = 8.5m,
                                    MaxScore = 10m,
                                    AiSuggestedScore = 8.5m,
                                    TeacherFinalScore = 0m,
                                    IsFinalized = false,
                                    IsAiGraded = true,
                                    IsPassed = true,
                                    AiFeedback = "Nắm vững lý thuyết DI Container."
                                }
                            }
                        }
                    }
                });
            }

            if (studentId == "SV02")
            {
                // SV02: Attempt 1 is fully finalized by Teacher
                return Task.FromResult<StudentReportDto?>(new StudentReportDto
                {
                    StudentId = "SV02",
                    StudentName = "Trần Thị B",
                    ExamId = examId,
                    ExamTitle = "Lập trình C# Nâng cao",
                    EvaluationMode = ExamEvaluationMode.Score,
                    CompletedAttemptsCount = 1,
                    MaxAllowedAttemptsCount = 3,
                    GradedAttemptsCount = 1,
                    AverageScore = 9.0m,
                    Attempts = new List<AttemptSnapshotDto>
                    {
                        new()
                        {
                            AttemptId = "ATT-FIN-1",
                            StudentId = "SV02",
                            StudentName = "Trần Thị B",
                            ClassId = "SE1801",
                            ExamId = examId,
                            AttemptOrdinal = 1,
                            ExamState = ExamState.Completed,
                            GradingState = GradingState.Graded,
                            CompletedAt = new DateTime(2026, 11, 2, 14, 0, 0, DateTimeKind.Utc),
                            IsPassed = true,
                            QuestionResults = new List<QuestionResultDto>
                            {
                                new()
                                {
                                    QuestionId = "Q1",
                                    QuestionTitle = "Khái niệm Dependency Injection",
                                    EarnedScore = 9.0m,
                                    MaxScore = 10m,
                                    AiSuggestedScore = 7.5m,
                                    TeacherFinalScore = 9.0m,
                                    TeacherNotes = "Điểm chốt sau đánh giá toàn diện.",
                                    IsFinalized = true,
                                    IsAiGraded = false,
                                    IsPassed = true,
                                    AiFeedback = "Trả lời xuất sắc."
                                }
                            }
                        }
                    }
                });
            }

            return Task.FromResult<StudentReportDto?>(null);
        }

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new List<ExamSettingsSnapshot>
            {
                new() { ExamId = "EXAM01", ExamTitle = "Lập trình C# Nâng cao" }
            });
        }
    }

    public sealed class FakeClassReportService : IClassReportService
    {
        public Task<ClassStatisticsDto?> GetClassStatisticsAsync(string teacherId, string classId, string examId, int? specificAttemptOrdinal = null, CancellationToken ct = default)
        {
            return Task.FromResult<ClassStatisticsDto?>(new ClassStatisticsDto
            {
                ClassId = classId,
                ClassName = $"Lớp {classId}",
                ExamId = examId,
                ExamTitle = "Lập trình C# Nâng cao",
                TotalStudentsCount = 2,
                AverageClassScore = 8.75m,
                GradeExportRows = new List<ClassGradeExportRowDto>
                {
                    new()
                    {
                        StudentId = "SV01",
                        StudentName = "Nguyễn Văn A",
                        ClassId = classId,
                        Attempt1Score = "8.5",
                        Attempt2Score = "Chưa thi",
                        AverageScore = "8.5",
                        ResultStatus = "ĐẠT",
                        Note = "AI chấm, chờ giảng viên duyệt"
                    },
                    new()
                    {
                        StudentId = "SV02",
                        StudentName = "Trần Thị B",
                        ClassId = classId,
                        Attempt1Score = "9.0",
                        Attempt2Score = "Chưa thi",
                        AverageScore = "9.0",
                        ResultStatus = "ĐẠT",
                        Note = "Điểm chính thức"
                    }
                }
            });
        }

        public Task<StudentReportDto?> GetStudentExamDetailForTeacherAsync(string teacherId, string classId, string examId, string studentId, CancellationToken ct = default)
        {
            var studentService = new FakeStudentReportService();
            return studentService.GetStudentReportAsync(studentId, examId, ct);
        }

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new List<ExamSettingsSnapshot>
            {
                new() { ExamId = "EXAM01", ExamTitle = "Lập trình C# Nâng cao" }
            });
        }

        public Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default)
        {
            return Task.FromResult(new List<string> { "SE1801", "EXAM01" });
        }

        public Task<byte[]> ExportClassGradeSheetCsvAsync(string teacherId, string classId, string examId, CancellationToken ct = default)
        {
            return Task.FromResult(Array.Empty<byte>());
        }
    }

    private readonly ExamReportsFactory _factory;

    public ExamReportsViewTests(ExamReportsFactory factory)
    {
        _factory = factory;
    }

    // ==========================================
    // 1. STUDENT REPORTS VIEW TESTS
    // ==========================================

    [Fact]
    public async Task Get_StudentReports_WithAiGradedAttempt_RendersAiGradedBadges()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Staff);

        var response = await client.GetAsync("/StudentReports?studentId=SV01&examId=EXAM01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        // Header / Breadcrumb
        Assert.Contains("Báo Cáo Học Tập & Phản Hồi Vấn Đáp AI", html);
        Assert.Contains("Nguyễn Văn A", html);

        // Yellow AI-Graded Badge in Attempt row
        Assert.Contains("AI chấm, chờ giảng viên duyệt", html);

        // Question detail card Block 4 badge
        Assert.Contains("AI CHẤM, CHỜ DUYỆT", html);
        // Điểm AI và điểm GV giờ hiển thị tách biệt (AI gợi ý / GV chốt).
        Assert.Contains("Điểm AI (chờ duyệt): 8.5 / 10", html);
        Assert.Contains("Điểm GV: chưa chốt", html);
    }

    [Fact]
    public async Task Get_StudentReports_WithFinalizedAttempt_RendersFinalizedScoreBadges()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "SV02");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Staff);

        var response = await client.GetAsync("/StudentReports?studentId=SV02&examId=EXAM01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        // Green Finalized Score Badge
        Assert.Contains("Điểm chính thức", html);

        // Question detail card Block 4 badge
        Assert.Contains("ĐIỂM CHÍNH THỨC", html);
        // Điểm AI và điểm GV hiển thị riêng: AI đề xuất 8, GV chốt 9.
        Assert.Contains("Điểm GV: 9 / 10", html);
    }

    [Fact]
    public async Task Get_StudentReports_OtherStudentId_AsStaff_ReturnsForbid()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Staff);

        // SV01 attempts to view SV02's report -> Anti-IDOR check must return 403 Forbidden
        var response = await client.GetAsync("/StudentReports?studentId=SV02&examId=EXAM01");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_StudentReports_AnonymousUser_IsRedirectedToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/StudentReports?studentId=SV01&examId=EXAM01");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/Login", response.Headers.Location.ToString());
    }

    // ==========================================
    // 2. CLASS REPORTS & STUDENT DETAIL VIEW TESTS
    // ==========================================

    [Fact]
    public async Task Get_ClassReportsStudentDetail_LecturerRole_WithAiGraded_RendersBadges()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync("/ClassReports/StudentDetail?studentId=SV01&classId=EXAM01&examId=EXAM01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        // Tab badge and overview header badge
        Assert.Contains("AI chấm chờ duyệt", html);
        Assert.Contains("AI chấm, chờ giảng viên duyệt", html);
    }

    [Fact]
    public async Task Get_ClassReportsStudentDetail_LecturerRole_WithFinalized_RendersBadges()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync("/ClassReports/StudentDetail?studentId=SV02&classId=EXAM01&examId=EXAM01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        // Tab badge and overview header badge
        Assert.Contains("Điểm chính thức", html);
    }

    [Fact]
    public async Task Get_ClassReportsIndex_LecturerRole_RendersStatusNotesAndBadges()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync("/ClassReports?classId=EXAM01&examId=EXAM01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        Assert.Contains("Bảng Điểm Tổng Hợp", html);
        Assert.Contains("AI chấm, chờ giảng viên duyệt", html);
        Assert.Contains("Điểm chính thức", html);
    }

    [Fact]
    public async Task Get_ClassReportsIndex_StudentRole_IsForbidden()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Staff);

        var response = await client.GetAsync("/ClassReports?classId=EXAM01&examId=EXAM01");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
