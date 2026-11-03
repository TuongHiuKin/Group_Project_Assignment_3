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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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
    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

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
                    IsFullyFinalized = false,
                    QuestionResults = new List<QuestionGradingResultDto>
                    {
                        new()
                        {
                            Id = Guid.NewGuid(),
                            SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                            QuestionId = "Q1",
                            QuestionTitle = "Khái niệm Dependency Injection",
                            QuestionContent = "Trình bày các loại Dependency Injection và ưu điểm của chúng trong ASP.NET Core.",
                            BloomLevel = "Understand",
                            MaxPoints = 10m,
                            AiScore = 8.5m,
                            AiFeedback = "Thí sinh nắm chắc khái niệm, trình bày mạch lạc 3 loại lifetime Scoped, Singleton, Transient.",
                            Strengths = "Giải thích rõ ràng cơ chế IoC Container và vòng đời Scoped.",
                            Weaknesses = "Chưa nêu ví dụ cụ thể về rủi ro Captive Dependency.",
                            AiProvider = "Gemini",
                            AiModel = "gemini-1.5-flash",
                            State = GradingState.AiGraded,
                            CriteriaOutcomes = new List<CriterionOutcomeDto>
                            {
                                new()
                                {
                                    CriterionCode = "C1",
                                    CriterionName = "Hiểu khái niệm DI",
                                    MaxPoints = 3m,
                                    AwardedPoints = 3m,
                                    IsMet = true,
                                    Explanation = "Nêu đúng định nghĩa Inversion of Control."
                                },
                                new()
                                {
                                    CriterionCode = "C2",
                                    CriterionName = "Phân biệt 3 lifetime",
                                    MaxPoints = 4m,
                                    AwardedPoints = 3.5m,
                                    IsMet = true,
                                    Explanation = "Nêu đủ Transient, Scoped, Singleton."
                                },
                                new()
                                {
                                    CriterionCode = "C3",
                                    CriterionName = "Ưu điểm thực tế",
                                    MaxPoints = 3m,
                                    AwardedPoints = 2m,
                                    IsMet = true,
                                    Explanation = "Nêu được tính testability và loose coupling."
                                }
                            },
                            SpeechMetrics = new SpeechMetricsDto
                            {
                                AnswerDurationSeconds = 65.0,
                                SpeakingSpeedWpm = 135.0,
                                FillerWordsCount = 3,
                                FillerWordsRatio = 0.02,
                                IdentifiedFillerWords = new List<string> { "à (2)", "ừm (1)" }
                            },
                            Turns = new List<QuestionTurnDetailDto>
                            {
                                new()
                                {
                                    TurnId = Guid.NewGuid(),
                                    TurnOrdinal = 1,
                                    TranscriptText = "Dependency Injection là kỹ thuật cung cấp các phụ thuộc cho đối tượng...",
                                    FollowUpQuestionText = "Vậy Captive Dependency xảy ra khi nào?",
                                    ConfidenceScore = 0.95m,
                                    CreatedAtUtc = new DateTime(2026, 11, 1, 10, 1, 0, DateTimeKind.Utc),
                                    CompletedAtUtc = new DateTime(2026, 11, 1, 10, 2, 5, DateTimeKind.Utc)
                                },
                                new()
                                {
                                    TurnId = Guid.NewGuid(),
                                    TurnOrdinal = 2,
                                    TranscriptText = "Dạ Captive Dependency xảy ra khi singleton service tham chiếu tới scoped service...",
                                    ConfidenceScore = 0.92m,
                                    CreatedAtUtc = new DateTime(2026, 11, 1, 10, 2, 10, DateTimeKind.Utc),
                                    CompletedAtUtc = new DateTime(2026, 11, 1, 10, 3, 0, DateTimeKind.Utc)
                                }
                            }
                        }
                    }
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
                    IsFullyFinalized = true,
                    QuestionResults = new List<QuestionGradingResultDto>
                    {
                        new()
                        {
                            Id = Guid.NewGuid(),
                            SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                            QuestionId = "Q1",
                            QuestionTitle = "Khái niệm Dependency Injection",
                            QuestionContent = "Trình bày các loại Dependency Injection và ưu điểm của chúng trong ASP.NET Core.",
                            BloomLevel = "Understand",
                            MaxPoints = 10m,
                            AiScore = 7.0m,
                            TeacherFinalScore = 9.0m,
                            TeacherNotes = "Thí sinh diễn đạt rất tự tin và thực tế, chấm 9.",
                            FinalizedBy = "GV01",
                            FinalizedAtUtc = new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc),
                            State = GradingState.Finalized
                        }
                    }
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

        public Task<QuestionGradingResultDto> FinalizeScoreAsync(FinalizeScoreRequestDto request, string teacherId, CancellationToken ct = default)
        {
            var session = Sessions.FirstOrDefault(s => s.SessionId == request.SessionId)
                ?? throw new InvalidOperationException($"Session {request.SessionId} not found");

            var question = session.QuestionResults.FirstOrDefault(q => string.Equals(q.QuestionId, request.QuestionId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Question {request.QuestionId} not found");

            if (string.Equals(request.ExpectedRowVersion, "CONFLICT", StringComparison.OrdinalIgnoreCase))
            {
                throw new DbUpdateConcurrencyException("The grading result was modified by another instructor.");
            }

            if (question.State == GradingState.Finalized && string.IsNullOrWhiteSpace(request.ChangeReason))
            {
                throw new ArgumentException("A non-empty reason is mandatory when modifying an already finalized score.", nameof(request.ChangeReason));
            }

            question.TeacherFinalScore = request.FinalScore;
            question.TeacherNotes = request.TeacherNotes;
            question.FinalizedBy = teacherId;
            question.FinalizedAtUtc = DateTime.UtcNow;
            question.State = GradingState.Finalized;

            session.TotalFinalScore = session.QuestionResults.Sum(q => q.TeacherFinalScore ?? q.AiScore ?? 0);
            session.IsFullyFinalized = session.QuestionResults.All(q => q.IsFinalized);
            if (session.IsFullyFinalized)
            {
                session.OverallState = GradingState.Finalized;
            }

            return Task.FromResult(question);
        }
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

        Assert.Single(model.Sessions);
        Assert.Equal(GradingState.AiGraded, model.Sessions[0].OverallState);
        Assert.Equal("SV01", model.Sessions[0].StudentId);
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
    // 2. UNIT TESTS: ReviewModel PageModel Logic
    // ==========================================

    [Fact]
    public async Task ReviewModel_OnGetAsync_ValidSessionId_PopulatesSessionAndTitles()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance);

        var validId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var result = await model.OnGetAsync(validId);

        Assert.IsType<PageResult>(result);
        Assert.NotNull(model.Session);
        Assert.Equal(validId, model.Session.SessionId);
        Assert.Equal("Lập trình C# Nâng cao (EXAM01)", model.ExamTitle);
        Assert.Equal("Nguyễn Văn A (SV01)", model.StudentDisplayName);
        Assert.Single(model.Session.QuestionResults);
    }

    [Fact]
    public async Task ReviewModel_OnGetAsync_MissingSessionId_RedirectsToIndex()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new EmptyTempDataProvider())
        };

        var result = await model.OnGetAsync(null);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Grading/Index", redirect.PageName);
        Assert.Equal("Không tìm thấy mã phiên thi cần xem.", model.TempData["ErrorMessage"]);
    }

    [Fact]
    public async Task ReviewModel_OnGetAsync_NonExistentSessionId_RedirectsToIndexWithErrorMessage()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            TempData = new TempDataDictionary(new DefaultHttpContext(), new EmptyTempDataProvider())
        };

        var randomId = Guid.NewGuid();
        var result = await model.OnGetAsync(randomId);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Grading/Index", redirect.PageName);
        Assert.Contains(randomId.ToString(), model.TempData["ErrorMessage"]?.ToString());
    }

    [Theory]
    [InlineData(0, "Chưa xác định")]
    [InlineData(70, "Chậm (Cần tăng tốc độ phản xạ)")]
    [InlineData(130, "Chuẩn (Nhịp độ tự nhiên, rõ ràng)")]
    [InlineData(180, "Nhanh (Cần kiểm soát ngắt nghỉ)")]
    public void ReviewModel_GetSpeakingSpeedAssessment_ReturnsExpectedAssessment(double wpm, string expected)
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance);

        var assessment = model.GetSpeakingSpeedAssessment(wpm);
        Assert.Equal(expected, assessment);
    }

    // ==========================================
    // 3. UNIT TESTS: Review Form Actions (UI-03)
    // ==========================================

    [Fact]
    public async Task ReviewModel_OnPostAcceptAiScoreAsync_ValidRequest_FinalizesScoreAndSetsSuccessMessage()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var httpContext = new DefaultHttpContext();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new EmptyTempDataProvider())
        };

        var result = await model.OnPostAcceptAiScoreAsync("Q1", 8.5m, null);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(model.TempData["SuccessMessage"]);
        Assert.Contains("8.5", model.TempData["SuccessMessage"]?.ToString());

        var session = await fakeOrchestrator.GetSessionGradingAsync(model.SessionId.Value);
        Assert.NotNull(session);
        var q1 = session.QuestionResults.First(q => q.QuestionId == "Q1");
        Assert.Equal(8.5m, q1.TeacherFinalScore);
        Assert.Equal(GradingState.Finalized, q1.State);
    }

    [Fact]
    public async Task ReviewModel_OnPostOverrideScoreAsync_ValidScore_OverridesAndFinalizes()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var httpContext = new DefaultHttpContext();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new EmptyTempDataProvider())
        };

        var result = await model.OnPostOverrideScoreAsync("Q1", 9.5m, "Trả lời xuất sắc", null, null, isAlreadyFinalized: false);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(model.TempData["SuccessMessage"]);
        Assert.Contains("9.5", model.TempData["SuccessMessage"]?.ToString());

        var session = await fakeOrchestrator.GetSessionGradingAsync(model.SessionId.Value);
        Assert.NotNull(session);
        var q1 = session.QuestionResults.First(q => q.QuestionId == "Q1");
        Assert.Equal(9.5m, q1.TeacherFinalScore);
        Assert.Equal("Trả lời xuất sắc", q1.TeacherNotes);
        Assert.Equal(GradingState.Finalized, q1.State);
    }

    [Fact]
    public async Task ReviewModel_OnPostOverrideScoreAsync_InvalidScore_FailsWithErrorMessage()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var httpContext = new DefaultHttpContext();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new EmptyTempDataProvider())
        };

        // Score 15 is > maxPoints (10)
        await model.OnPostOverrideScoreAsync("Q1", 15m, "Ghi chú", null, null, isAlreadyFinalized: false, maxPoints: 10m);

        Assert.NotNull(model.TempData["ErrorMessage"]);
        Assert.Contains("không hợp lệ", model.TempData["ErrorMessage"]?.ToString());
    }

    [Fact]
    public async Task ReviewModel_OnPostOverrideScoreAsync_AlreadyFinalizedWithoutReason_FailsWithMandatoryReasonError()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var httpContext = new DefaultHttpContext();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new EmptyTempDataProvider())
        };

        // Modifying an already finalized score with EMPTY reason
        await model.OnPostOverrideScoreAsync("Q1", 8.0m, "Giảm điểm", "", null, isAlreadyFinalized: true);

        Assert.NotNull(model.TempData["ErrorMessage"]);
        Assert.Contains("Bắt buộc phải nhập lý do điều chỉnh", model.TempData["ErrorMessage"]?.ToString());
    }

    [Fact]
    public async Task ReviewModel_OnPostOverrideScoreAsync_AlreadyFinalizedWithReason_Succeeds()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var httpContext = new DefaultHttpContext();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            SessionId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new EmptyTempDataProvider())
        };

        // Modifying an already finalized score WITH reason
        await model.OnPostOverrideScoreAsync("Q1", 8.5m, "Giảm điểm sau hội đồng", "Phúc khảo theo đơn sinh viên", null, isAlreadyFinalized: true);

        Assert.NotNull(model.TempData["SuccessMessage"]);
        Assert.Contains("8.5", model.TempData["SuccessMessage"]?.ToString());

        var session = await fakeOrchestrator.GetSessionGradingAsync(model.SessionId.Value);
        Assert.NotNull(session);
        var q1 = session.QuestionResults.First(q => q.QuestionId == "Q1");
        Assert.Equal(8.5m, q1.TeacherFinalScore);
    }

    [Fact]
    public async Task ReviewModel_OnPostOverrideScoreAsync_ConcurrencyConflict_CatchesAndSetsWarning()
    {
        var fakeOrchestrator = new FakeGradingOrchestrator();
        var fakeReportService = new FakeClassReportService();
        var httpContext = new DefaultHttpContext();
        var model = new ReviewModel(fakeOrchestrator, fakeReportService, NullLogger<ReviewModel>.Instance)
        {
            SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new EmptyTempDataProvider())
        };

        // Pass expectedRowVersion = "CONFLICT" to simulate DbUpdateConcurrencyException
        await model.OnPostOverrideScoreAsync("Q1", 9.0m, "Chấm điểm", null, "CONFLICT", isAlreadyFinalized: false);

        Assert.NotNull(model.TempData["ErrorMessage"]);
        Assert.Contains("Xung đột dữ liệu", model.TempData["ErrorMessage"]?.ToString());
    }

    // ==========================================
    // 4. INTEGRATION TESTS: HTTP & Security
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

    [Fact]
    public async Task Get_GradingReview_AnonymousUser_IsRedirectedToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Grading/Review?sessionId=11111111-1111-1111-1111-111111111111");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/Login", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Get_GradingReview_StudentRole_IsForbidden()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Staff);

        var response = await client.GetAsync("/Grading/Review?sessionId=11111111-1111-1111-1111-111111111111");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Get_GradingReview_LecturerRole_ReturnsOkAndRendersReviewPageDetails()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync("/Grading/Review?sessionId=11111111-1111-1111-1111-111111111111");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);

        // 1. Header & Student Information
        Assert.Contains("Chi Tiết Đánh Giá & Duyệt Điểm Vấn Đáp", html);
        Assert.Contains("Nguyễn Văn A (SV01)", html);
        Assert.Contains("Lập trình C# Nâng cao (EXAM01)", html);

        // 2. Question Details & Turns
        Assert.Contains("Khái niệm Dependency Injection", html);
        Assert.Contains("Trình bày các loại Dependency Injection", html);
        Assert.Contains("Dependency Injection là kỹ thuật cung cấp", html);
        Assert.Contains("Vậy Captive Dependency xảy ra khi nào?", html);

        // 3. Rubric Criteria Outcomes
        Assert.Contains("Hiểu khái niệm DI", html);
        Assert.Contains("Phân biệt 3 lifetime", html);
        Assert.Contains("Ưu điểm thực tế", html);

        // 4. AI Feedback, Strengths & Weaknesses
        Assert.Contains("Thí sinh nắm chắc khái niệm", html);
        Assert.Contains("Giải thích rõ ràng cơ chế IoC Container", html);
        Assert.Contains("Chưa nêu ví dụ cụ thể về rủi ro Captive Dependency", html);

        // 5. Speech Metrics
        Assert.Contains("Thời lượng trả lời", html);
        Assert.Contains("Tốc độ phát âm", html);
        Assert.Contains("135", html);
        Assert.Contains("WPM", html);
        Assert.Contains("Số từ đệm phát hiện", html);

        // 6. Lecturer Decision Actions
        Assert.Contains("Xác nhận điểm AI", html);
        Assert.Contains("Nhập điểm GV", html);
        Assert.Contains("Chấm lại bằng AI", html);
    }

    [Fact]
    public async Task Get_GradingReview_AdminRole_ReturnsOk()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User", "admin");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Admin);

        var response = await client.GetAsync("/Grading/Review?sessionId=11111111-1111-1111-1111-111111111111");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawHtml = await response.Content.ReadAsStringAsync();
        var html = WebUtility.HtmlDecode(rawHtml);
        Assert.Contains("Chi Tiết Đánh Giá & Duyệt Điểm Vấn Đáp", html);
    }

    [Fact]
    public async Task Get_GradingReview_MissingSessionId_RedirectsToIndex()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var response = await client.GetAsync("/Grading/Review");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Grading", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Get_GradingReview_NonExistentSessionId_RedirectsToIndex()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var randomId = Guid.NewGuid();
        var response = await client.GetAsync($"/Grading/Review?sessionId={randomId}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Grading", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Post_GradingReview_AcceptAiScore_LecturerRole_FinalizesScoreAndRedirects()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var sessionId = "11111111-1111-1111-1111-111111111111";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["questionId"] = "Q1",
            ["aiScore"] = "8.5"
        });

        var response = await client.PostAsync($"/Grading/Review?handler=AcceptAiScore&sessionId={sessionId}", content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains(sessionId, response.Headers.Location.ToString());

        var session = await _factory.FakeOrchestrator.GetSessionGradingAsync(Guid.Parse(sessionId));
        Assert.NotNull(session);
        var q1 = session.QuestionResults.First(q => q.QuestionId == "Q1");
        Assert.Equal(8.5m, q1.TeacherFinalScore);
        Assert.Equal(GradingState.Finalized, q1.State);
    }

    [Fact]
    public async Task Post_GradingReview_OverrideScore_LecturerRole_ValidScore_FinalizesScore()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "GV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Lecturer);

        var sessionId = "11111111-1111-1111-1111-111111111111";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["questionId"] = "Q1",
            ["finalScore"] = "9.0",
            ["teacherNotes"] = "Rất tốt",
            ["isAlreadyFinalized"] = "false",
            ["maxPoints"] = "10"
        });

        var response = await client.PostAsync($"/Grading/Review?handler=OverrideScore&sessionId={sessionId}", content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var session = await _factory.FakeOrchestrator.GetSessionGradingAsync(Guid.Parse(sessionId));
        Assert.NotNull(session);
        var q1 = session.QuestionResults.First(q => q.QuestionId == "Q1");
        Assert.Equal(9.0m, q1.TeacherFinalScore);
        Assert.Equal("Rất tốt", q1.TeacherNotes);
        Assert.Equal(GradingState.Finalized, q1.State);
    }

    [Fact]
    public async Task Post_GradingReview_StudentRole_IsForbidden()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Add("X-Test-User", "SV01");
        client.DefaultRequestHeaders.Add("X-Test-Role", ApplicationRoles.Staff);

        var sessionId = "11111111-1111-1111-1111-111111111111";
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["questionId"] = "Q1",
            ["aiScore"] = "8.5"
        });

        var response = await client.PostAsync($"/Grading/Review?handler=AcceptAiScore&sessionId={sessionId}", content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/AccessDenied", response.Headers.Location.ToString());
    }
}
