using System.Security.Claims;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Repositories.ExamReporting;
using AIVES.BLL.Security;
using AIVES.BLL.Services.ExamReporting;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using AIVES.DAL.Repositories.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using CriterionOutcomeDto = AIVES.BLL.Models.Grading.CriterionOutcomeDto;
using GradingState = AIVES.BLL.Models.ExamReporting.ReportGradingState;

namespace AIVES.Tests.ExamReporting;

public class ExamReportingTests
{
    private readonly IExamReportRepository _repository;
    private readonly StudentReportService _studentService;
    private readonly ClassReportService _classService;

    public ExamReportingTests()
    {
        _repository = new TestSeededExamReportRepository();
        _studentService = new StudentReportService(_repository);
        _classService = new ClassReportService(_repository);
    }

    [Fact]
    public async Task StudentReport_Counts_Only_Completed_Attempts_And_Excludes_Abandoned_DEC01()
    {
        // SV03 has 1 completed & graded, 1 completed & pending, 1 abandoned (total 3 in db)
        var report = await _studentService.GetStudentReportAsync("SV03", "EXAM01");

        Assert.NotNull(report);
        // DEC-01: Chỉ lượt Completed mới tính vào số lượt hoàn thành (1 + 1 = 2), bỏ dở (Abandoned) không được tính
        Assert.Equal(2, report.CompletedAttemptsCount);
        Assert.Equal(3, report.MaxAllowedAttemptsCount);
        Assert.Equal(1, report.GradedAttemptsCount);
        Assert.Equal(1, report.PendingAttemptsCount);
    }

    [Fact]
    public async Task StudentReport_Calculates_Average_Only_Over_Graded_Completed_Attempts_DEC05_DEC10()
    {
        // SV01 has 2 completed and fully graded attempts:
        // Attempt 1: Q1=8, Q2=4, Q3=5 => Total=17/30 => Normalized=5.67
        // Attempt 2: Q1=9, Q2=8, Q3=7 => Total=24/30 => Normalized=8.00
        // Expected Average = (5.67 + 8.00) / 2 = 6.84
        var report = await _studentService.GetStudentReportAsync("SV01", "EXAM01");

        Assert.NotNull(report);
        Assert.Equal(2, report.GradedAttemptsCount);
        Assert.NotNull(report.AverageScore);
        Assert.Equal(6.84m, report.AverageScore.Value);
        // DEC-05: Mẫu số chia là số lượt có kết quả đầy đủ (2), KHÔNG chia cho số lượt tối đa (3)
    }

    [Fact]
    public async Task StudentReport_Evaluates_PassFail_Mode_With_Min_Pass_Percentage_And_Mandatory_Criteria_DEC04()
    {
        var fakeRepo = new FakeExamReportRepository();
        var examSettings = new ExamSettingsSnapshot
        {
            ExamId = "PASSFAIL_EXAM",
            ExamTitle = "Bài thi Pass/Fail",
            EvaluationMode = ExamEvaluationMode.PassFail,
            MinPassPercentage = 66.6m,
            RequiredCriteriaKeys = new List<string> { "MandatoryCrit" }
        };
        fakeRepo.Exams.Add(examSettings);

        // Attempt A: 2/3 questions passed (66.7% >= 66.6%), BUT MandatoryCrit is false => FAIL
        fakeRepo.Attempts.Add(new AttemptSnapshotDto
        {
            AttemptId = "ATT-PF-1",
            StudentId = "SV99",
            ExamId = "PASSFAIL_EXAM",
            AttemptOrdinal = 1,
            ExamState = ExamState.Completed,
            GradingState = GradingState.Graded,
            QuestionResults = new List<QuestionResultDto>
            {
                new() { QuestionId = "Q1", IsPassed = true, CriteriaOutcomes = new() { { "MandatoryCrit", false } } },
                new() { QuestionId = "Q2", IsPassed = true, CriteriaOutcomes = new() },
                new() { QuestionId = "Q3", IsPassed = false, CriteriaOutcomes = new() }
            }
        });

        var service = new StudentReportService(fakeRepo);
        var report = await service.GetStudentReportAsync("SV99", "PASSFAIL_EXAM");

        Assert.NotNull(report);
        Assert.Single(report.Attempts);
        Assert.False(report.Attempts[0].IsPassed, "Lượt thi phải Không đạt vì không thỏa tiêu chí bắt buộc (DEC-04).");
    }

    [Fact]
    public async Task ClassReport_Throws_Unauthorized_When_Teacher_Not_Assigned_To_Class_DEC11()
    {
        // GV02 is assigned to SE1702, NOT SE1701
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _classService.GetClassStatisticsAsync("GV02", "SE1701", "EXAM01"));
    }

    [Fact]
    public async Task ClassReport_Identifies_Hardest_Questions_With_Highest_Fail_Rate_And_Includes_All_Ties_DEC09()
    {
        // For SE1701 on Attempt 1:
        // SV01: Q1=8 (Đạt), Q2=4 (Không đạt), Q3=5 (Không đạt) (ngưỡng 6.0)
        // SV02: Q1=4 (Không đạt), Q2=2 (Không đạt), Q3=3 (Không đạt)
        // SV03: Q1=6 (Đạt), Q2=6 (Đạt), Q3=7 (Đạt)
        // Total Assessed for each: 3
        // Q1: Passed=2, Failed=1 => FailRate = 33.33%
        // Q2: Passed=1, Failed=2 => FailRate = 66.67%
        // Q3: Passed=1, Failed=2 => FailRate = 66.67%
        // DEC-09: Q2 and Q3 are tied for hardest! Both must be present in HardestQuestions list!
        var stats = await _classService.GetClassStatisticsAsync("GV01", "SE1701", "EXAM01", specificAttemptOrdinal: 1);

        Assert.NotNull(stats);
        Assert.Equal(2, stats.HardestQuestions.Count);
        Assert.Contains(stats.HardestQuestions, q => q.QuestionId == "Q2");
        Assert.Contains(stats.HardestQuestions, q => q.QuestionId == "Q3");
        Assert.Equal(66.67m, stats.HardestQuestions[0].FailRate);
        Assert.Equal(66.67m, stats.HardestQuestions[1].FailRate);
    }

    [Fact]
    public async Task ClassReport_Orders_Best_Answered_Questions_By_Pass_Rate_Descending_DEC09()
    {
        var stats = await _classService.GetClassStatisticsAsync("GV01", "SE1701", "EXAM01", specificAttemptOrdinal: 1);

        Assert.NotNull(stats);
        // Q1 has PassRate = 66.67%, Q2 and Q3 have PassRate = 33.33%
        // Rank 1 must be Q1
        Assert.Equal("Q1", stats.QuestionStats[0].QuestionId);
        Assert.Equal(1, stats.QuestionStats[0].Rank);
        Assert.Equal(66.67m, stats.QuestionStats[0].PassRate);
    }

    [Fact]
    public async Task ClassReport_Distribution_Counts_Each_Student_Exactly_Once_On_X_Axis_DEC06()
    {
        // SE1701 has 3 students:
        // SV01 average = (5.67 + 8.00) / 2 = 6.8
        // SV02 average = (3.00) / 1 = 3.0
        // SV03 average = (6.33) / 1 = 6.3
        // DEC-06: Each student counted once, sum of student counts in bins must equal 3
        var stats = await _classService.GetClassStatisticsAsync("GV01", "SE1701", "EXAM01");

        Assert.NotNull(stats);
        Assert.Equal(3, stats.TotalStudentsCount);
        var totalCountInBins = stats.ScoreDistribution.Sum(bin => bin.StudentCount);
        Assert.Equal(3, totalCountInBins);
    }

    [Fact]
    public async Task ClassReport_Exports_Valid_Csv_With_Utf8_Bom_And_Student_Data()
    {
        var csvBytes = await _classService.ExportClassGradeSheetCsvAsync("GV01", "SE1701", "EXAM01");

        Assert.NotNull(csvBytes);
        Assert.NotEmpty(csvBytes);

        // UTF-8 BOM check: 0xEF, 0xBB, 0xBF
        Assert.Equal(0xEF, csvBytes[0]);
        Assert.Equal(0xBB, csvBytes[1]);
        Assert.Equal(0xBF, csvBytes[2]);

        var csvContent = System.Text.Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("Mã sinh viên", csvContent);
        Assert.Contains("SV01", csvContent);
        Assert.Contains("SV02", csvContent);
        Assert.Contains("SV03", csvContent);
        Assert.Contains("Nguyễn Văn A", csvContent);
    }

    [Fact]
    public async Task GetStudentExamDetail_AuthorizedTeacher_ReturnsReportWithAttempts()
    {
        // GV01 is assigned to SE1701, SV01 is enrolled in SE1701
        var report = await _classService.GetStudentExamDetailForTeacherAsync("GV01", "SE1701", "EXAM01", "SV01");

        Assert.NotNull(report);
        Assert.Equal("SV01", report.StudentId);
        Assert.Equal("Nguyễn Văn A", report.StudentName);
        Assert.Equal(2, report.CompletedAttemptsCount);
        Assert.Equal(2, report.Attempts.Count);
        Assert.NotNull(report.AverageScore);
        Assert.Equal(6.84m, report.AverageScore.Value);

        // Check attempt details
        var attempt1 = report.Attempts.FirstOrDefault(a => a.AttemptOrdinal == 1);
        Assert.NotNull(attempt1);
        Assert.Equal(3, attempt1.QuestionResults.Count);
        Assert.Equal("Q1", attempt1.QuestionResults[0].QuestionId);
        Assert.Equal(8.0m, attempt1.QuestionResults[0].TeacherFinalScore);
        Assert.Equal(7.5m, attempt1.QuestionResults[0].AiSuggestedScore);
    }

    [Fact]
    public async Task GetStudentExamDetail_UnauthorizedTeacher_ThrowsUnauthorizedAccessException_DEC11()
    {
        // GV02 is assigned to SE1702, NOT SE1701
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _classService.GetStudentExamDetailForTeacherAsync("GV02", "SE1701", "EXAM01", "SV01"));
    }

    [Fact]
    public async Task GetStudentExamDetail_StudentNotInClass_ThrowsInvalidOperationException()
    {
        // GV01 is authorized for SE1701, but SV999 is not in SE1701
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _classService.GetStudentExamDetailForTeacherAsync("GV01", "SE1701", "EXAM01", "SV999"));
    }

    [Theory]
    [InlineData("", "SE1701", "EXAM01", "SV01")]
    [InlineData("GV01", "", "EXAM01", "SV01")]
    [InlineData("GV01", "SE1701", "", "SV01")]
    [InlineData("GV01", "SE1701", "EXAM01", "")]
    public async Task GetStudentExamDetail_EmptyArguments_ThrowsArgumentException(
        string teacherId, string classId, string examId, string studentId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _classService.GetStudentExamDetailForTeacherAsync(teacherId, classId, examId, studentId));
    }

    [Fact]
    public async Task StudentReport_Propagates_EvaluationMode_Correctly_To_Attempts_And_Questions()
    {
        // 1. Score Mode Test (EXAM01)
        var scoreReport = await _studentService.GetStudentReportAsync("SV01", "EXAM01");
        Assert.NotNull(scoreReport);
        Assert.Equal(ExamEvaluationMode.Score, scoreReport.EvaluationMode);
        Assert.All(scoreReport.Attempts, a =>
        {
            Assert.Equal(ExamEvaluationMode.Score, a.EvaluationMode);
            Assert.All(a.QuestionResults, q => Assert.Equal(ExamEvaluationMode.Score, q.EvaluationMode));
        });

        // 2. Pass/Fail Mode Test (EXAM02)
        var passFailReport = await _studentService.GetStudentReportAsync("SV01", "EXAM02");
        Assert.NotNull(passFailReport);
        Assert.Equal(ExamEvaluationMode.PassFail, passFailReport.EvaluationMode);
        Assert.Null(passFailReport.AverageScore); // Pass/Fail mode does not compute numerical average score
        Assert.All(passFailReport.Attempts, a =>
        {
            Assert.Equal(ExamEvaluationMode.PassFail, a.EvaluationMode);
            Assert.All(a.QuestionResults, q => Assert.Equal(ExamEvaluationMode.PassFail, q.EvaluationMode));
        });
    }

    [Fact]
    public async Task StudentReportService_GetAvailableExamsAsync_DelegatesToRepository()
    {
        var exams = await _studentService.GetAvailableExamsAsync();
        Assert.NotNull(exams);
        Assert.NotEmpty(exams);
    }

    [Fact]
    public async Task ClassReportService_GetAvailableExamsAndClasses_DelegatesToRepository()
    {
        var exams = await _classService.GetAvailableExamsAsync();
        var classes = await _classService.GetAvailableClassesForTeacherAsync("GV01");

        Assert.NotNull(exams);
        Assert.NotEmpty(exams);
        Assert.NotNull(classes);
        Assert.NotEmpty(classes);
    }

    [Fact]
    public async Task StudentReportsPage_StudentCannotViewOtherStudentReport_ReturnsForbid()
    {
        var page = new StudentNameMVC.Pages.StudentReports.IndexModel(_studentService);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "SV01"),
            new Claim(ClaimTypes.Email, "student1@aives.test"),
            new Claim(ClaimTypes.Role, ApplicationRoles.Staff)
        }, "TestAuth"));

        page.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Sinh viên SV01 cố tình truyền studentId="SV02" để xem bài của bạn
        var result = await page.OnGetAsync(studentId: "SV02", examId: "EXAM01");

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task StudentReportsPage_StudentCanViewOwnReport_ReturnsPageResult()
    {
        var page = new StudentNameMVC.Pages.StudentReports.IndexModel(_studentService);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "SV01"),
            new Claim(ClaimTypes.Email, "student1@aives.test"),
            new Claim(ClaimTypes.Role, ApplicationRoles.Staff)
        }, "TestAuth"));

        page.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Sinh viên xem bài thi của chính mình (hoặc truyền null)
        var result = await page.OnGetAsync(studentId: null, examId: "EXAM01");

        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(result);
        Assert.NotNull(page.Report);
        Assert.Equal("SV01", page.Report.StudentId);
    }

    [Fact]
    public async Task StudentReportsPage_AdminCanViewAnyStudentReport_ReturnsPageResult()
    {
        var page = new StudentNameMVC.Pages.StudentReports.IndexModel(_studentService);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "admin"),
            new Claim(ClaimTypes.Email, "admin@aives.test"),
            new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
        }, "TestAuth"));

        page.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };

        // Admin có thể xem bài của bất kỳ sinh viên nào (SV02)
        var result = await page.OnGetAsync(studentId: "SV02", examId: "EXAM01");

        Assert.IsType<Microsoft.AspNetCore.Mvc.RazorPages.PageResult>(result);
        Assert.NotNull(page.Report);
        Assert.Equal("SV02", page.Report.StudentId);
    }

    [Fact]
    public void ReportingPageModels_DoNotDependOnExamReportRepository()
    {
        // Kiểm tra kiến trúc: PageModel không được nhận IExamReportRepository trong constructor
        var studentCtors = typeof(StudentNameMVC.Pages.StudentReports.IndexModel).GetConstructors();
        Assert.All(studentCtors, c =>
            Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(IExamReportRepository)));

        var classCtors = typeof(StudentNameMVC.Pages.ClassReports.IndexModel).GetConstructors();
        Assert.All(classCtors, c =>
            Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType == typeof(IExamReportRepository)));
    }

    [Fact]
    public async Task ExamReportRepository_Maps_VivaSessions_And_Turns_From_Real_Repositories_With_Pending_Grading()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "10",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            Turns = new List<VivaSessionTurn>
            {
                new()
                {
                    TurnId = Guid.NewGuid(),
                    SessionId = sessionId,
                    QuestionId = "Q1",
                    TurnOrdinal = 1,
                    TranscriptText = "Em xin trình bày về Dependency Injection.",
                    ConfidenceScore = 0.90m,
                    FollowUpQuestionText = "Tại sao DbContext dùng Scoped?"
                },
                new()
                {
                    TurnId = Guid.NewGuid(),
                    SessionId = sessionId,
                    QuestionId = "Q1",
                    TurnOrdinal = 2,
                    TranscriptText = "Để mỗi HTTP request có 1 DbContext riêng biệt.",
                    ConfidenceScore = 0.92m
                }
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([
            new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "DI Lifetime", Content = "Giải thích vòng đời DI." }
        ]);
        var accountRepo = new StubAccountRepo([
            new SystemAccount { AccountId = 10, AccountName = "Phạm Minh Khoa", AccountEmail = "khoa@fpt.edu.vn", AccountRole = 1 }
        ]);

        var realRepo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo);
        var studentService = new StudentReportService(realRepo);

        var report = await studentService.GetStudentReportAsync("10", "EXAM01");

        Assert.NotNull(report);
        Assert.Equal("10", report.StudentId);
        Assert.Equal("Phạm Minh Khoa", report.StudentName);
        Assert.Equal(1, report.CompletedAttemptsCount);
        Assert.Equal(0, report.GradedAttemptsCount);
        Assert.Equal(1, report.PendingAttemptsCount);
        Assert.Null(report.AverageScore); // Chưa chấm (Group 4 chưa triển khai)

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal(GradingState.Pending, attempt.GradingState);
        var q = Assert.Single(attempt.QuestionResults);
        Assert.Equal("Q1", q.QuestionId);
        Assert.Equal("Em xin trình bày về Dependency Injection.", q.StudentInitialAnswerTranscript);
        Assert.Equal("Tại sao DbContext dùng Scoped?", q.AiFollowUpQuestion);
        Assert.Equal("Để mỗi HTTP request có 1 DbContext riêng biệt.", q.StudentFollowUpAnswerTranscript);
    }

    [Fact]
    public async Task ExamReportRepository_GetAvailableExamsAsync_UsesGetDistinctExamIdsWithoutLoadingAllSessions()
    {
        var session = new VivaSession
        {
            SessionId = Guid.NewGuid(),
            ExamId = "EXAM01",
            StudentId = "10",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow
        };
        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([
            new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "DI Lifetime", Content = "Giải thích vòng đời DI." }
        ]);
        var accountRepo = new StubAccountRepo([]);

        var realRepo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo);

        var exams = await realRepo.GetAvailableExamsAsync();

        Assert.Single(exams);
        Assert.Equal("EXAM01", exams[0].ExamId);
        Assert.Equal(1, sessionRepo.GetDistinctExamIdsCallCount);
        Assert.Equal(0, sessionRepo.GetSessionsWithDetailsCallCount);
    }

    [Fact]
    public async Task ClassReportService_WhenAllAttemptsArePending_SetsChuaChamInsteadOfChuaDatOrZero()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "10",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            Turns = new List<VivaSessionTurn>
            {
                new()
                {
                    TurnId = Guid.NewGuid(),
                    SessionId = sessionId,
                    QuestionId = "Q1",
                    TurnOrdinal = 1,
                    TranscriptText = "Trả lời câu 1."
                }
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([
            new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "DI Lifetime", Content = "Giải thích vòng đời DI." }
        ]);
        var accountRepo = new StubAccountRepo([
            new SystemAccount { AccountId = 10, AccountName = "Phạm Minh Khoa", AccountEmail = "khoa@fpt.edu.vn", AccountRole = 1 }
        ]);

        var realRepo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo);
        var classService = new ClassReportService(realRepo);

        var stats = await classService.GetClassStatisticsAsync("GV01", "EXAM01", "EXAM01");

        Assert.NotNull(stats);
        var row = Assert.Single(stats.GradeExportRows);
        Assert.Equal("Chưa chấm", row.Attempt1Score);
        Assert.Equal("Chưa chấm", row.AverageScore);
        Assert.Equal("Chưa chấm", row.ResultStatus);
    }

    private const string Exam01Q01Content = "Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime thay vì Singleton, và điều gì sẽ xảy ra với Entity Tracker và kết nối cơ sở dữ liệu nếu một service Singleton giữ tham chiếu đến DbContext?";

    private static ExamReportRepository CreateRubricRepo(string transcript, VivaSessionState state = VivaSessionState.Completed)
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "1",
            State = (byte)state,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "EXAM01-Q01", TurnOrdinal = 1, TranscriptText = transcript }
            }
        };

        return new ExamReportRepository(
            new StubSessionRepo([session]),
            new StubQuestionRepo([new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "DbContext Lifetime", Content = Exam01Q01Content }]),
            new StubAccountRepo([new SystemAccount { AccountId = 1, AccountName = "Kieen", AccountEmail = "kin@gmail.com", AccountRole = 2 }]),
            new RubricGradingService());
    }

    /// <summary>
    /// Repo có sẵn một bản ghi VivaGradingResult (mô phỏng GradingOrchestrator đã chấm nền).
    /// Đây là nguồn điểm duy nhất của báo cáo từ nay.
    /// </summary>
    private static ExamReportRepository CreateRepoWithGrading(Guid sessionId, string transcript, decimal aiScore)
    {
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "1",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "EXAM01-Q01", TurnOrdinal = 1, TranscriptText = transcript }
            }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "EXAM01-Q01",
                AiScore = aiScore,
                AiProvider = "Stub",
                AiModel = "stub-model",
                GradingState = (byte)AIVES.BLL.Models.Grading.GradingState.AiGraded,
                CriteriaOutcomesJson = "[]",
                RowVersion = new byte[] { 1 }
            }
        };

        return new ExamReportRepository(
            new StubSessionRepo([session]),
            new StubQuestionRepo([new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "DbContext Lifetime", Content = Exam01Q01Content }]),
            new StubAccountRepo([new SystemAccount { AccountId = 1, AccountName = "Kieen", AccountEmail = "kin@gmail.com", AccountRole = 2 }]),
            new RubricGradingService(),
            new StubGradingRepo(gradingResults));
    }

    [Fact]
    public async Task ExamReportRepository_CompletedSessionWithoutPersistedGrading_StaysPendingNotGradedAtRuntime()
    {
        // Hành vi mới (Nhóm 6 sửa lệch #2): báo cáo KHÔNG tự chấm bằng RubricGradingService.
        // Phiên đã Completed nhưng chưa có VivaGradingResult thì để Pending và chờ
        // GradingOrchestrator chấm nền; nếu chấm runtime ở đây, sinh viên sẽ thấy một điểm
        // khác với điểm AI thật và giảng viên không có gì để duyệt.
        var repo = CreateRubricRepo(Exam01Q01Content);

        var attempt = Assert.Single(await repo.GetStudentAttemptsAsync("1", "EXAM01"));
        var q = Assert.Single(attempt.QuestionResults);

        Assert.Equal(GradingState.Pending, attempt.GradingState);
        Assert.Equal(0m, q.EarnedScore);
        Assert.False(q.IsPassed);
        Assert.False(attempt.IsPassed);
        Assert.Equal(GradingState.Pending, q.GradingState);
        Assert.True(string.IsNullOrEmpty(q.AiFeedback));
    }

    [Fact]
    public async Task ExamReportRepository_ScoreChangesWithAnswerContent_OnlyWhenPersistedGradingExists()
    {
        // Điểm giờ lấy từ VivaGradingResult đã lưu (do GradingOrchestrator chấm), không chấm lại
        // lúc xem báo cáo. Hai câu trả lời khác nhau vẫn ra điểm khác nhau, nhưng vì bản ghi
        // grading khác nhau — đây mới là nguồn sự thật.
        const string full = "DbContext là Scoped vì mỗi HTTP request có một instance riêng.";

        var fullSessionId = Guid.NewGuid();
        var fullAttempt = Assert.Single(await CreateRepoWithGrading(fullSessionId, full, aiScore: 10m)
            .GetStudentAttemptsAsync("1", "EXAM01"));

        var partialSessionId = Guid.NewGuid();
        var partialAttempt = Assert.Single(await CreateRepoWithGrading(partialSessionId, "Vì mỗi request cần một instance riêng.", aiScore: 3.5m)
            .GetStudentAttemptsAsync("1", "EXAM01"));

        // Cùng một input transcript nhưng KHÔNG có bản ghi grading -> Pending, 0 điểm.
        var noGradingAttempt = Assert.Single(await CreateRubricRepo(full).GetStudentAttemptsAsync("1", "EXAM01"));

        Assert.Equal(10m, fullAttempt.NormalizedScore);
        Assert.True(fullAttempt.IsPassed);
        Assert.Equal(3.5m, partialAttempt.NormalizedScore);
        Assert.False(partialAttempt.IsPassed);
        Assert.Equal(0m, noGradingAttempt.NormalizedScore);
        Assert.Equal(GradingState.Pending, noGradingAttempt.GradingState);
    }

    [Fact]
    public async Task ExamReportRepository_SessionNotCompleted_StaysPendingOrFailed()
    {
        var inProgress = Assert.Single(await CreateRubricRepo("Mỗi request một DbContext riêng biệt.", VivaSessionState.Answering).GetStudentAttemptsAsync("1", "EXAM01"));
        var timedOut = Assert.Single(await CreateRubricRepo("Mỗi request một DbContext riêng biệt.", VivaSessionState.TimedOut).GetStudentAttemptsAsync("1", "EXAM01"));

        Assert.Equal(GradingState.Pending, inProgress.GradingState);
        Assert.Equal(GradingState.Failed, timedOut.GradingState);
        Assert.False(inProgress.IsPassed);
        Assert.Equal(0m, Assert.Single(inProgress.QuestionResults).EarnedScore);
    }

    [Theory]
    [InlineData(8.0, true)]   // trên ngưỡng 6.0 -> đạt
    [InlineData(3.5, false)]  // dưới ngưỡng -> không đạt
    [InlineData(6.0, true)]   // đúng ngưỡng -> đạt (so sánh >=)
    public async Task StudentAndTeacherViews_AgreeOnPassResultAndThreshold(decimal aiScore, bool expectedPassed)
    {
        // Sinh viên và giảng viên phải thấy cùng một kết quả trên cùng một bản ghi grading.
        const string transcript = "DbContext là Scoped vì mỗi HTTP request có một instance riêng.";
        var repo = CreateRepoWithGrading(Guid.NewGuid(), transcript, aiScore);

        var studentReport = await new StudentReportService(repo).GetStudentReportAsync("1", "EXAM01");
        var teacherReport = await new ClassReportService(repo).GetStudentExamDetailForTeacherAsync("GV01", "EXAM01", "EXAM01", "1");

        Assert.NotNull(studentReport);
        Assert.NotNull(teacherReport);
        var studentAttempt = Assert.Single(studentReport.Attempts);
        var teacherAttempt = Assert.Single(teacherReport.Attempts);

        Assert.Equal(expectedPassed, studentAttempt.IsPassed);
        Assert.Equal(expectedPassed, teacherAttempt.IsPassed);
        Assert.Equal(studentAttempt.NormalizedScore, teacherAttempt.NormalizedScore);
        Assert.Equal(studentReport.PassThresholdScore, teacherReport.PassThresholdScore);
        Assert.Equal(ExamReportRepository.DefaultPassThresholdScore, teacherReport.PassThresholdScore);
        Assert.Equal(expectedPassed, teacherReport.AverageScore >= teacherReport.PassThresholdScore);
        Assert.Equal(expectedPassed, Assert.Single(teacherAttempt.QuestionResults).IsPassed);
    }

    [Fact]
    public async Task ExamReportRepository_Prioritizes_TeacherFinalScore_Over_AiScore_G4()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "SV01",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "Câu 1 trả lời tốt." },
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "Q2", TurnOrdinal = 2, TranscriptText = "Câu 2 trả lời khá." }
            }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            // Q1: AI chấm 7.5, nhưng Giảng viên chốt 9.0 (ưu tiên điểm GV)
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "Q1",
                AiScore = 7.5m,
                TeacherFinalScore = 9.0m,
                TeacherNotes = "Sinh viên hiểu sâu kiến trúc.",
                GradingState = 2, // Finalized
                Session = session
            },
            // Q2: AI chấm 8.0, Giảng viên chưa chốt (TeacherFinalScore = null) => lấy điểm AI
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "Q2",
                AiScore = 8.0m,
                TeacherFinalScore = null,
                GradingState = 1, // AiGraded
                Session = session
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([
            new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "Câu hỏi 1", Content = "Chi tiết câu 1" },
            new VivaQuestion { QuestionId = "Q2", ExamId = "EXAM01", Title = "Câu hỏi 2", Content = "Chi tiết câu 2" }
        ]);
        var accountRepo = new StubAccountRepo([
            new SystemAccount { AccountId = 1, AccountName = "Nguyễn Văn A", AccountEmail = "SV01", AccountRole = 1 }
        ]);
        var gradingRepo = new StubGradingRepo(gradingResults);

        var repo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo, gradingRepo: gradingRepo);
        var attempts = await repo.GetStudentAttemptsAsync("SV01", "EXAM01");

        var attempt = Assert.Single(attempts);
        Assert.Equal(GradingState.Graded, attempt.GradingState);
        Assert.Equal(2, attempt.QuestionResults.Count);

        var q1 = attempt.QuestionResults.First(q => q.QuestionId == "Q1");
        Assert.Equal(9.0m, q1.EarnedScore); // Ưu tiên điểm GV!
        Assert.Equal(9.0m, q1.TeacherFinalScore);
        Assert.Equal(7.5m, q1.AiSuggestedScore);
        Assert.Equal("Sinh viên hiểu sâu kiến trúc.", q1.TeacherNotes);
        Assert.True(q1.IsPassed);

        var q2 = attempt.QuestionResults.First(q => q.QuestionId == "Q2");
        Assert.Equal(8.0m, q2.EarnedScore); // Điểm AI khi GV chưa chấm!
        Assert.Equal(0m, q2.TeacherFinalScore);
        Assert.Equal(8.0m, q2.AiSuggestedScore);
        Assert.True(q2.IsPassed);

        // Normalized score = (9.0 + 8.0) / 20.0 * 10 = 8.5
        Assert.Equal(8.5m, attempt.NormalizedScore);
        Assert.True(attempt.IsPassed);

        // Group 4 Badge Flags
        Assert.True(q1.IsFinalized);
        Assert.False(q1.IsAiGraded);
        Assert.False(q2.IsFinalized);
        Assert.True(q2.IsAiGraded);
        Assert.False(attempt.IsFullyFinalized);
        Assert.True(attempt.IsAiGraded);
    }

    [Fact]
    public async Task ExamReportRepository_AllQuestionsFinalized_Sets_IsFullyFinalized_True()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "SV01",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "Trả lời câu 1" }
            }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "Q1",
                AiScore = 8.0m,
                TeacherFinalScore = 8.5m,
                GradingState = 2, // Finalized
                Session = session
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([
            new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "Câu 1", Content = "Nội dung câu 1" }
        ]);
        var accountRepo = new StubAccountRepo([
            new SystemAccount { AccountId = 1, AccountName = "SV 1", AccountEmail = "SV01", AccountRole = 1 }
        ]);
        var gradingRepo = new StubGradingRepo(gradingResults);

        var repo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo, gradingRepo: gradingRepo);
        var attempts = await repo.GetStudentAttemptsAsync("SV01", "EXAM01");

        var attempt = Assert.Single(attempts);
        Assert.True(attempt.IsFullyFinalized);
        Assert.False(attempt.IsAiGraded);
        var q1 = Assert.Single(attempt.QuestionResults);
        Assert.True(q1.IsFinalized);
        Assert.False(q1.IsAiGraded);
    }

    [Fact]
    public async Task ClassReportService_WithAiGradedAttempts_SetsAiChamNote()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "SV01",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "Trả lời câu 1" }
            }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "Q1",
                AiScore = 8.0m,
                GradingState = 1, // AiGraded
                Session = session
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([
            new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "Câu 1", Content = "Nội dung câu 1" }
        ]);
        var accountRepo = new StubAccountRepo([
            new SystemAccount { AccountId = 1, AccountName = "SV 1", AccountEmail = "SV01", AccountRole = 1 }
        ]);
        var gradingRepo = new StubGradingRepo(gradingResults);

        var repo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo, gradingRepo: gradingRepo);
        var classService = new ClassReportService(repo);
        var stats = await classService.GetClassStatisticsAsync("GV01", "EXAM01", "EXAM01");

        Assert.NotNull(stats);
        var row = Assert.Single(stats.GradeExportRows);
        Assert.Equal("AI chấm, chờ giảng viên duyệt", row.Note);
    }

    [Fact]
    public async Task ExamReportRepository_Pending_GradingResult_Keeps_Attempt_Pending_G4()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "SV02",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "Đang chờ chấm." }
            }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            // Q1 đang trong hàng đợi AI (Pending = 0), chưa có điểm
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "Q1",
                AiScore = null,
                TeacherFinalScore = null,
                GradingState = 0, // Pending
                Session = session
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "Câu 1" }]);
        var accountRepo = new StubAccountRepo([new SystemAccount { AccountId = 2, AccountName = "Trần Thị B", AccountEmail = "SV02", AccountRole = 1 }]);
        var gradingRepo = new StubGradingRepo(gradingResults);

        var repo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo, gradingRepo: gradingRepo);
        var attempts = await repo.GetStudentAttemptsAsync("SV02", "EXAM01");

        var attempt = Assert.Single(attempts);
        Assert.Equal(GradingState.Pending, attempt.GradingState);
        var q1 = Assert.Single(attempt.QuestionResults);
        Assert.Equal(GradingState.Pending, q1.GradingState);
        Assert.Equal(0m, q1.EarnedScore);
        Assert.False(q1.IsPassed);
        Assert.False(attempt.IsPassed);
    }

    [Fact]
    public async Task ExamReportRepository_Failed_GradingResult_Marks_Question_And_Attempt_Failed_G4()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "SV03",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "Lỗi khi chấm." }
            }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "Q1",
                AiScore = null,
                TeacherFinalScore = null,
                GradingState = 3, // Failed
                Session = session
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "Câu 1" }]);
        var accountRepo = new StubAccountRepo([new SystemAccount { AccountId = 3, AccountName = "Lê Văn C", AccountEmail = "SV03", AccountRole = 1 }]);
        var gradingRepo = new StubGradingRepo(gradingResults);

        var repo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo, gradingRepo: gradingRepo);
        var attempts = await repo.GetStudentAttemptsAsync("SV03", "EXAM01");

        var attempt = Assert.Single(attempts);
        Assert.Equal(GradingState.Failed, attempt.GradingState);
        var q1 = Assert.Single(attempt.QuestionResults);
        Assert.Equal(GradingState.Failed, q1.GradingState);
        Assert.Equal(0m, q1.EarnedScore);
        Assert.False(attempt.IsPassed);
    }

    [Fact]
    public async Task ExamReportRepository_Parses_Persisted_Feedback_And_CriteriaOutcomes_G4()
    {
        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "SV04",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTime.UtcNow,
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), SessionId = sessionId, QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "Chi tiết trả lời." }
            }
        };

        var criteria = new List<CriterionOutcomeDto>
        {
            new() { CriterionCode = "C1", CriterionName = "Hiểu khái niệm", MaxPoints = 5m, AwardedPoints = 5m, IsMet = true, Explanation = "Tốt" },
            new() { CriterionCode = "C2", CriterionName = "Vận dụng thực tế", MaxPoints = 5m, AwardedPoints = 3m, IsMet = false, Explanation = "Thiếu ví dụ" }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                QuestionId = "Q1",
                AiScore = 8.0m,
                AiFeedback = "Sinh viên nắm tốt lý thuyết.",
                Strengths = "Trả lời lưu loát.",
                Weaknesses = "Cần thêm ví dụ thực tế.",
                CriteriaOutcomesJson = System.Text.Json.JsonSerializer.Serialize(criteria),
                GradingState = 1,
                Session = session
            }
        };

        var sessionRepo = new StubSessionRepo([session]);
        var questionRepo = new StubQuestionRepo([new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "Câu 1" }]);
        var accountRepo = new StubAccountRepo([new SystemAccount { AccountId = 4, AccountName = "Vũ D", AccountEmail = "SV04", AccountRole = 1 }]);
        var gradingRepo = new StubGradingRepo(gradingResults);

        var repo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo, gradingRepo: gradingRepo);
        var attempts = await repo.GetStudentAttemptsAsync("SV04", "EXAM01");

        var attempt = Assert.Single(attempts);
        var q1 = Assert.Single(attempt.QuestionResults);
        Assert.Equal("Sinh viên nắm tốt lý thuyết.", q1.AiFeedback);
        Assert.Equal("Trả lời lưu loát.", q1.Strengths);
        Assert.Equal("Cần thêm ví dụ thực tế.", q1.Weaknesses);
        Assert.True(q1.CriteriaOutcomes["Hiểu khái niệm"]);
        Assert.False(q1.CriteriaOutcomes["Vận dụng thực tế"]);
    }

    [Fact]
    public async Task ClassReportService_Uses_Persisted_GradingResults_For_ClassStatistics_G4()
    {
        var session1 = new VivaSession
        {
            SessionId = Guid.NewGuid(),
            ExamId = "EXAM01",
            StudentId = "101",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-20),
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "SV 101 trả lời Q1" }
            }
        };
        var session2 = new VivaSession
        {
            SessionId = Guid.NewGuid(),
            ExamId = "EXAM01",
            StudentId = "102",
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-20),
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            Turns = new List<VivaSessionTurn>
            {
                new() { TurnId = Guid.NewGuid(), QuestionId = "Q1", TurnOrdinal = 1, TranscriptText = "SV 102 trả lời Q1" }
            }
        };

        var gradingResults = new List<VivaGradingResult>
        {
            // SV 101: GV chốt 9.0 (AI gợi ý 7.0)
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = session1.SessionId,
                QuestionId = "Q1",
                AiScore = 7.0m,
                TeacherFinalScore = 9.0m,
                GradingState = 2,
                Session = session1
            },
            // SV 102: AI chấm 8.0, GV chưa chốt
            new()
            {
                Id = Guid.NewGuid(),
                SessionId = session2.SessionId,
                QuestionId = "Q1",
                AiScore = 8.0m,
                TeacherFinalScore = null,
                GradingState = 1,
                Session = session2
            }
        };

        var sessionRepo = new StubSessionRepo([session1, session2]);
        var questionRepo = new StubQuestionRepo([new VivaQuestion { QuestionId = "Q1", ExamId = "EXAM01", Title = "Câu 1" }]);
        var accountRepo = new StubAccountRepo([
            new SystemAccount { AccountId = 101, AccountName = "Sinh viên A", AccountEmail = "101@fpt.edu.vn", AccountRole = 1 },
            new SystemAccount { AccountId = 102, AccountName = "Sinh viên B", AccountEmail = "102@fpt.edu.vn", AccountRole = 1 }
        ]);
        var gradingRepo = new StubGradingRepo(gradingResults);

        var repo = new ExamReportRepository(sessionRepo, questionRepo, accountRepo, gradingRepo: gradingRepo);
        var classService = new ClassReportService(repo);

        var stats = await classService.GetClassStatisticsAsync("GV01", "EXAM01", "EXAM01");

        Assert.NotNull(stats);
        Assert.Equal(2, stats.TotalStudentsCount);
        // SV101: 9.0, SV102: 8.0 => Class average = (9.0 + 8.0) / 2 = 8.5
        Assert.Equal(8.5m, stats.AverageClassScore);

        var row1 = stats.GradeExportRows.First(r => r.StudentId == "101");
        Assert.Equal("9.0", row1.Attempt1Score);
        Assert.Equal("9.0", row1.AverageScore);
        Assert.Equal("ĐẠT", row1.ResultStatus);

        var row2 = stats.GradeExportRows.First(r => r.StudentId == "102");
        Assert.Equal("8.0", row2.Attempt1Score);
        Assert.Equal("8.0", row2.AverageScore);
        Assert.Equal("ĐẠT", row2.ResultStatus);
    }

    private sealed class StubSessionRepo(List<VivaSession> sessions) : IVivaSessionRepository
    {
        public int GetSessionsWithDetailsCallCount { get; private set; }
        public int GetDistinctExamIdsCallCount { get; private set; }

        public Task<VivaSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(sessions.FirstOrDefault(s => s.SessionId == sessionId));
        public Task<VivaSession?> GetWithDetailsAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(sessions.FirstOrDefault(s => s.SessionId == sessionId));
        public Task<List<VivaSession>> GetSessionsWithDetailsAsync(string? examId = null, string? studentId = null, CancellationToken ct = default)
        {
            GetSessionsWithDetailsCallCount++;
            var q = sessions.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(examId)) q = q.Where(s => s.ExamId == examId);
            if (!string.IsNullOrWhiteSpace(studentId)) q = q.Where(s => s.StudentId == studentId);
            return Task.FromResult(q.OrderBy(s => s.StartedAtUtc).ToList());
        }
        public Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default)
        {
            GetDistinctExamIdsCallCount++;
            return Task.FromResult(sessions.Select(s => s.ExamId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList());
        }
        public Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default) => Task.FromResult(session.SessionId);
        public Task UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default) => Task.CompletedTask;
        public Task CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubQuestionRepo(List<VivaQuestion> questions) : IVivaQuestionRepository
    {
        public Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default) =>
            Task.FromResult(questions.Where(q => q.ExamId == examId).ToList());
        public Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default) =>
            Task.FromResult(questions.Select(q => q.ExamId).Distinct().ToList());
        public Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default) =>
            Task.FromResult(questions.FirstOrDefault(q => q.QuestionId == questionId));

        public Task<bool> SaveAudioUrlAsync(string questionId, string audioUrl, CancellationToken ct = default)
        {
            var q = questions.FirstOrDefault(x => x.QuestionId == questionId);
            if (q is null) return Task.FromResult(false);
            q.AudioUrl = audioUrl;
            return Task.FromResult(true);
        }
    }

    private sealed class StubAccountRepo(List<SystemAccount> accounts) : ISystemAccountRepository
    {
        public Task<bool> ResetPasswordAsync(int id, string email, string expectedHash, string newHash, CancellationToken ct = default) => Task.FromResult(false);
        public Task<List<SystemAccount>> SearchAsync(string? keyword = null, byte? role = null, bool includeDeleted = false, CancellationToken ct = default) =>
            Task.FromResult(accounts);
        public Task<SystemAccount?> GetByIdAsync(int id, bool includeDeleted = false, CancellationToken ct = default) =>
            Task.FromResult(accounts.FirstOrDefault(a => a.AccountId == id));
        public Task<SystemAccount?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult(accounts.FirstOrDefault(a => string.Equals(a.AccountEmail, email, StringComparison.OrdinalIgnoreCase)));
        public Task<bool> EmailExistsAsync(string email, int? exceptId = null, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsReferencedAsync(int id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> CreateAsync(AccountCreate input, CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> UpdateAsync(AccountUpdate input, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> UpdateProfileAsync(ProfileUpdate input, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> SoftDeleteAsync(int id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<DeleteResult> HardDeleteAsync(int id, CancellationToken ct = default) => Task.FromResult(DeleteResult.NotFound);
    }

    private sealed class StubGradingRepo(List<VivaGradingResult> results) : IVivaGradingRepository
    {
        public Task<List<VivaGradingResult>> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(results.Where(r => r.SessionId == sessionId).ToList());

        public Task<VivaGradingResult?> GetByQuestionAsync(Guid sessionId, string questionId, CancellationToken ct = default) =>
            Task.FromResult(results.FirstOrDefault(r => r.SessionId == sessionId && r.QuestionId == questionId));

        public Task<List<VivaGradingResult>> GetByExamIdAsync(string examId, CancellationToken ct = default) =>
            Task.FromResult(results.Where(r => r.Session == null || string.Equals(r.Session.ExamId, examId, StringComparison.OrdinalIgnoreCase)).ToList());

        public Task<List<Guid>> GetPendingOrFailedSessionIdsAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult(new List<Guid>());

        public Task<VivaGradingResult> UpsertAiResultAsync(VivaGradingResult result, CancellationToken ct = default) =>
            Task.FromResult(result);

        public Task<VivaGradingResult> FinalizeScoreAsync(Guid sessionId, string questionId, decimal finalScore, string teacherNotes, string teacherId, byte[]? expectedRowVersion, string? changeReason, CancellationToken ct = default) =>
            Task.FromResult(results.First(r => r.SessionId == sessionId && r.QuestionId == questionId));

        public Task<List<VivaGradeChangeLog>> GetChangeLogsAsync(Guid gradingResultId, CancellationToken ct = default) =>
            Task.FromResult(new List<VivaGradeChangeLog>());
    }

    private sealed class TestSeededExamReportRepository : IExamReportRepository
    {
        private readonly List<AttemptSnapshotDto> _attempts = new();
        private readonly List<ExamSettingsSnapshot> _exams = new();
        private readonly Dictionary<string, List<string>> _teacherAssignments = new();

        public TestSeededExamReportRepository()
        {
            _exams.Add(new ExamSettingsSnapshot
            {
                ExamId = "EXAM01",
                ExamTitle = "Thi Vấn Đáp - PRN222 .NET Core & Kiến trúc Ba lớp",
                SubjectCode = "PRN222",
                SettingsVersion = "1.0",
                EvaluationMode = ExamEvaluationMode.Score,
                MaxAllowedAttempts = 3,
                PassThresholdScore = 6.0m,
                MinPassPercentage = 60.0m
            });

            _exams.Add(new ExamSettingsSnapshot
            {
                ExamId = "EXAM02",
                ExamTitle = "Thi Vấn Đáp - SWE302 Thiết kế & Kiến trúc Phần mềm",
                SubjectCode = "SWE302",
                SettingsVersion = "1.0",
                EvaluationMode = ExamEvaluationMode.PassFail,
                MaxAllowedAttempts = 2,
                PassThresholdScore = 5.0m,
                MinPassPercentage = 66.6m,
                RequiredCriteriaKeys = new List<string> { "ArchitectureBoundary" }
            });

            _teacherAssignments["GV01"] = new List<string> { "SE1701", "SE1703" };
            _teacherAssignments["GV02"] = new List<string> { "SE1702" };

            _attempts.Add(new AttemptSnapshotDto
            {
                AttemptId = "ATT-001",
                StudentId = "SV01",
                StudentName = "Nguyễn Văn A",
                ClassId = "SE1701",
                ExamId = "EXAM01",
                AttemptOrdinal = 1,
                ExamState = ExamState.Completed,
                GradingState = GradingState.Graded,
                CompletedAt = DateTime.UtcNow.AddDays(-2),
                QuestionResults = new List<QuestionResultDto>
                {
                    new() { QuestionId = "Q1", QuestionTitle = "Q1", EarnedScore = 8.0m, MaxScore = 10.0m, AiSuggestedScore = 7.5m, TeacherFinalScore = 8.0m, IsPassed = true, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q2", QuestionTitle = "Q2", EarnedScore = 4.0m, MaxScore = 10.0m, AiSuggestedScore = 4.0m, TeacherFinalScore = 4.0m, IsPassed = false, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q3", QuestionTitle = "Q3", EarnedScore = 5.0m, MaxScore = 10.0m, AiSuggestedScore = 5.0m, TeacherFinalScore = 5.0m, IsPassed = false, GradingState = GradingState.Graded }
                }
            });

            _attempts.Add(new AttemptSnapshotDto
            {
                AttemptId = "ATT-002",
                StudentId = "SV01",
                StudentName = "Nguyễn Văn A",
                ClassId = "SE1701",
                ExamId = "EXAM01",
                AttemptOrdinal = 2,
                ExamState = ExamState.Completed,
                GradingState = GradingState.Graded,
                CompletedAt = DateTime.UtcNow.AddDays(-1),
                QuestionResults = new List<QuestionResultDto>
                {
                    new() { QuestionId = "Q1", QuestionTitle = "Q1", EarnedScore = 9.0m, MaxScore = 10.0m, AiSuggestedScore = 9.0m, TeacherFinalScore = 9.0m, IsPassed = true, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q2", QuestionTitle = "Q2", EarnedScore = 8.0m, MaxScore = 10.0m, AiSuggestedScore = 8.0m, TeacherFinalScore = 8.0m, IsPassed = true, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q3", QuestionTitle = "Q3", EarnedScore = 7.0m, MaxScore = 10.0m, AiSuggestedScore = 7.0m, TeacherFinalScore = 7.0m, IsPassed = true, GradingState = GradingState.Graded }
                }
            });

            _attempts.Add(new AttemptSnapshotDto
            {
                AttemptId = "ATT-003",
                StudentId = "SV02",
                StudentName = "Trần Thị B",
                ClassId = "SE1701",
                ExamId = "EXAM01",
                AttemptOrdinal = 1,
                ExamState = ExamState.Completed,
                GradingState = GradingState.Graded,
                CompletedAt = DateTime.UtcNow.AddDays(-2),
                QuestionResults = new List<QuestionResultDto>
                {
                    new() { QuestionId = "Q1", QuestionTitle = "Q1", EarnedScore = 4.0m, MaxScore = 10.0m, IsPassed = false, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q2", QuestionTitle = "Q2", EarnedScore = 2.0m, MaxScore = 10.0m, IsPassed = false, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q3", QuestionTitle = "Q3", EarnedScore = 3.0m, MaxScore = 10.0m, IsPassed = false, GradingState = GradingState.Graded }
                }
            });

            _attempts.Add(new AttemptSnapshotDto
            {
                AttemptId = "ATT-004",
                StudentId = "SV03",
                StudentName = "Lê Văn C",
                ClassId = "SE1701",
                ExamId = "EXAM01",
                AttemptOrdinal = 1,
                ExamState = ExamState.Completed,
                GradingState = GradingState.Graded,
                CompletedAt = DateTime.UtcNow.AddDays(-2),
                QuestionResults = new List<QuestionResultDto>
                {
                    new() { QuestionId = "Q1", QuestionTitle = "Q1", EarnedScore = 6.0m, MaxScore = 10.0m, IsPassed = true, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q2", QuestionTitle = "Q2", EarnedScore = 6.0m, MaxScore = 10.0m, IsPassed = true, GradingState = GradingState.Graded },
                    new() { QuestionId = "Q3", QuestionTitle = "Q3", EarnedScore = 7.0m, MaxScore = 10.0m, IsPassed = true, GradingState = GradingState.Graded }
                }
            });

            _attempts.Add(new AttemptSnapshotDto
            {
                AttemptId = "ATT-005",
                StudentId = "SV03",
                StudentName = "Lê Văn C",
                ClassId = "SE1701",
                ExamId = "EXAM01",
                AttemptOrdinal = 2,
                ExamState = ExamState.Completed,
                GradingState = GradingState.Pending,
                CompletedAt = DateTime.UtcNow.AddHours(-1),
                QuestionResults = new List<QuestionResultDto>
                {
                    new() { QuestionId = "Q1", QuestionTitle = "Q1", EarnedScore = 0m, MaxScore = 10.0m, GradingState = GradingState.Pending }
                }
            });

            _attempts.Add(new AttemptSnapshotDto
            {
                AttemptId = "ATT-006",
                StudentId = "SV03",
                StudentName = "Lê Văn C",
                ClassId = "SE1701",
                ExamId = "EXAM01",
                AttemptOrdinal = 3,
                ExamState = ExamState.Abandoned,
                GradingState = GradingState.Failed,
                CompletedAt = null
            });

            _attempts.Add(new AttemptSnapshotDto
            {
                AttemptId = "ATT-007",
                StudentId = "SV01",
                StudentName = "Nguyễn Văn A",
                ClassId = "SE1701",
                ExamId = "EXAM02",
                AttemptOrdinal = 1,
                ExamState = ExamState.Completed,
                GradingState = GradingState.Graded,
                EvaluationMode = ExamEvaluationMode.PassFail,
                CompletedAt = DateTime.UtcNow.AddDays(-1),
                QuestionResults = new List<QuestionResultDto>
                {
                    new() { QuestionId = "Q1", QuestionTitle = "Q1", EvaluationMode = ExamEvaluationMode.PassFail, IsPassed = true, GradingState = GradingState.Graded, CriteriaOutcomes = new() { { "ArchitectureBoundary", true } } },
                    new() { QuestionId = "Q2", QuestionTitle = "Q2", EvaluationMode = ExamEvaluationMode.PassFail, IsPassed = true, GradingState = GradingState.Graded, CriteriaOutcomes = new() { { "ArchitectureBoundary", true } } },
                    new() { QuestionId = "Q3", QuestionTitle = "Q3", EvaluationMode = ExamEvaluationMode.PassFail, IsPassed = false, GradingState = GradingState.Graded }
                }
            });
        }

        public Task<List<AttemptSnapshotDto>> GetStudentAttemptsAsync(string studentId, string examId, CancellationToken ct = default) =>
            Task.FromResult(_attempts.Where(a => a.StudentId == studentId && a.ExamId == examId).ToList());

        public Task<List<AttemptSnapshotDto>> GetClassAttemptsAsync(string classId, string examId, CancellationToken ct = default) =>
            Task.FromResult(_attempts.Where(a => a.ClassId == classId && a.ExamId == examId).ToList());

        public Task<bool> IsTeacherAssignedToClassAsync(string teacherId, string classId, CancellationToken ct = default) =>
            Task.FromResult(_teacherAssignments.TryGetValue(teacherId, out var classes) && classes.Contains(classId));

        public Task<ExamSettingsSnapshot?> GetExamSettingsAsync(string examId, CancellationToken ct = default) =>
            Task.FromResult(_exams.FirstOrDefault(e => e.ExamId == examId));

        public Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default) =>
            Task.FromResult(_teacherAssignments.TryGetValue(teacherId, out var classes) ? classes : new List<string>());

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default) =>
            Task.FromResult(_exams.ToList());
    }

    private class FakeExamReportRepository : IExamReportRepository
    {
        public List<ExamSettingsSnapshot> Exams { get; } = new();
        public List<AttemptSnapshotDto> Attempts { get; } = new();

        public Task<List<AttemptSnapshotDto>> GetStudentAttemptsAsync(string studentId, string examId, CancellationToken ct = default) =>
            Task.FromResult(Attempts.Where(a => a.StudentId == studentId && a.ExamId == examId).ToList());

        public Task<List<AttemptSnapshotDto>> GetClassAttemptsAsync(string classId, string examId, CancellationToken ct = default) =>
            Task.FromResult(Attempts.Where(a => a.ClassId == classId && a.ExamId == examId).ToList());

        public Task<bool> IsTeacherAssignedToClassAsync(string teacherId, string classId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<ExamSettingsSnapshot?> GetExamSettingsAsync(string examId, CancellationToken ct = default) =>
            Task.FromResult(Exams.FirstOrDefault(e => e.ExamId == examId));

        public Task<List<string>> GetAvailableClassesForTeacherAsync(string teacherId, CancellationToken ct = default) =>
            Task.FromResult(new List<string> { "TEST_CLASS" });

        public Task<List<ExamSettingsSnapshot>> GetAvailableExamsAsync(CancellationToken ct = default) =>
            Task.FromResult(Exams);
    }
}
