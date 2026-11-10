using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AIVES.BLL;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Repositories.ExamReporting;
using AIVES.BLL.Services.Ai.Mocks;
using AIVES.BLL.Services.ExamReporting;
using AIVES.BLL.Services.Grading;
using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Implementations;
using AIVES.DAL.Repositories.Interfaces;
using Xunit;
using GradingState = AIVES.BLL.Models.Grading.GradingState;

namespace AIVES.Tests.Integration;

public sealed class GradingE2ETests
{
    private static ServiceProvider BuildServiceProvider(string dbName, IAnswerGradingService? customAiService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddDbContext<AIVESDbContext>(options =>
            options.UseInMemoryDatabase(databaseName: dbName));

        services.AddScoped<IVivaSessionRepository, VivaSessionRepository>();
        services.AddScoped<IVivaQuestionRepository, VivaQuestionRepository>();
        services.AddScoped<IVivaTurnRepository, VivaTurnRepository>();
        services.AddScoped<IVivaGradingRepository, VivaGradingRepository>();
        services.AddScoped<ISystemAccountRepository, SystemAccountRepository>();

        services.AddSingleton<IRubricGradingService, RubricGradingService>();

        if (customAiService != null)
        {
            services.AddScoped<IAnswerGradingService>(_ => customAiService);
        }
        else
        {
            services.AddScoped<IAnswerGradingService, MockAnswerGradingService>();
        }

        // BLL Group 4 services
        services.AddVivaGradingServices();

        // Reporting services
        services.AddScoped<IExamReportRepository, ExamReportRepository>();
        services.AddScoped<IStudentReportService, StudentReportService>();
        services.AddScoped<IClassReportService, ClassReportService>();

        return services.BuildServiceProvider();
    }

    private static async Task SeedExamAndSessionAsync(
        AIVESDbContext db, 
        Guid sessionId, 
        string studentId = "10")
    {
        // 1. Seed Questions
        if (!await db.VivaQuestions.AnyAsync(q => q.QuestionId == "EXAM01-Q01"))
        {
            db.VivaQuestions.AddRange(
                new VivaQuestion
                {
                    QuestionId = "EXAM01-Q01",
                    ExamId = "EXAM01",
                    Title = "DbContext Lifetime",
                    Content = "Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime thay vì Singleton?"
                },
                new VivaQuestion
                {
                    QuestionId = "EXAM01-Q02",
                    ExamId = "EXAM01",
                    Title = "Repository Pattern",
                    Content = "Trình bày mục đích và lợi ích của Repository Pattern trong kiến trúc 3 lớp."
                }
            );
        }

        // 2. Seed Student Account
        if (!await db.SystemAccounts.AnyAsync(a => a.AccountId == 10))
        {
            db.SystemAccounts.Add(new SystemAccount
            {
                AccountId = 10,
                AccountName = "Phạm Minh Khoa",
                AccountEmail = "khoa@fpt.edu.vn",
                AccountPasswordHash = "hashedpassword",
                AccountRole = 1,
                IsDeleted = false
            });
        }

        // 3. Seed Session & Turns
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = studentId,
            State = (byte)VivaSessionState.Completed,
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-15),
            CompletedAtUtc = DateTime.UtcNow.AddMinutes(-2),
            Turns = new List<VivaSessionTurn>
            {
                new()
                {
                    TurnId = Guid.NewGuid(),
                    SessionId = sessionId,
                    QuestionId = "EXAM01-Q01",
                    TurnOrdinal = 1,
                    TranscriptText = "DbContext là Scoped vì mỗi HTTP request có một instance riêng. Nếu giữ trong Singleton thì ChangeTracker tích lũy entity gây rò rỉ bộ nhớ, và DbContext không thread-safe.",
                    ConfidenceScore = 0.95m,
                    FollowUpQuestionText = "Điều gì xảy ra nếu service Singleton tiêm DbContext?"
                },
                new()
                {
                    TurnId = Guid.NewGuid(),
                    SessionId = sessionId,
                    QuestionId = "EXAM01-Q01",
                    TurnOrdinal = 2,
                    TranscriptText = "Sẽ gây captive dependency và race condition khi nhiều request chạy đồng thời.",
                    ConfidenceScore = 0.92m
                },
                new()
                {
                    TurnId = Guid.NewGuid(),
                    SessionId = sessionId,
                    QuestionId = "EXAM01-Q02",
                    TurnOrdinal = 3,
                    TranscriptText = "Repository Pattern trừu tượng hóa tầng dữ liệu, tách biệt truy vấn Entity Framework khỏi BLL, giúp dễ unit test bằng mock và tuân thủ DIP.",
                    ConfidenceScore = 0.94m
                }
            }
        };

        db.VivaSessions.Add(session);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task E2E_FullFlow_VivaExam_AiGrading_LecturerFinalization_StudentReport()
    {
        var dbName = $"GradingE2E_{Guid.NewGuid()}";
        var sp = BuildServiceProvider(dbName);
        var sessionId = Guid.NewGuid();

        // Bước 0: Khởi tạo dữ liệu đề thi và phiên thi của sinh viên
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIVESDbContext>();
            await SeedExamAndSessionAsync(db, sessionId, "10");
        }

        // =========================================================================
        // Bước 1: Background AI Grading Job chạy (Sinh viên thi xong -> AI chấm nền)
        // =========================================================================
        using (var scope = sp.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IGradingOrchestrator>();
            var summary = await orchestrator.GradeSessionAsync(sessionId);

            Assert.NotNull(summary);
            Assert.Equal(sessionId, summary.SessionId);
            Assert.Equal(2, summary.QuestionResults.Count);
            Assert.Equal(GradingState.AiGraded, summary.OverallState);
            Assert.True(summary.EffectiveTotalScore > 0);
        }

        // =========================================================================
        // Bước 2: Sinh viên xem báo cáo ngay sau khi thi (Tentative AI score)
        // =========================================================================
        using (var scope = sp.CreateScope())
        {
            var studentService = scope.ServiceProvider.GetRequiredService<IStudentReportService>();
            var report = await studentService.GetStudentReportAsync("10", "EXAM01");

            Assert.NotNull(report);
            Assert.Equal(1, report.CompletedAttemptsCount);
            Assert.Equal(1, report.GradedAttemptsCount);
            Assert.NotNull(report.AverageScore);
            Assert.True(report.AverageScore.Value >= 6.0m);

            var attempt = Assert.Single(report.Attempts);
            Assert.Equal(AIVES.BLL.Models.ExamReporting.ReportGradingState.Graded, attempt.GradingState);
            Assert.Equal(2, attempt.QuestionResults.Count);

            // Cả 2 câu hỏi đều có điểm AI gợi ý
            var q1 = attempt.QuestionResults.First(q => q.QuestionId == "EXAM01-Q01");
            Assert.True(q1.AiSuggestedScore > 0);
            Assert.Equal(q1.AiSuggestedScore, q1.EarnedScore); // Điểm hiện tại = điểm AI
            Assert.NotEmpty(q1.AiFeedback);

            var q2 = attempt.QuestionResults.First(q => q.QuestionId == "EXAM01-Q02");
            Assert.True(q2.AiSuggestedScore > 0);
        }

        // =========================================================================
        // Bước 3: Giảng viên xem review và chốt điểm (Human-in-the-Loop)
        // =========================================================================
        using (var scope = sp.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IGradingOrchestrator>();
            var sessionDetail = await orchestrator.GetSessionGradingAsync(sessionId);

            Assert.NotNull(sessionDetail);
            Assert.Equal(2, sessionDetail.QuestionResults.Count);

            var q1 = sessionDetail.QuestionResults.First(q => q.QuestionId == "EXAM01-Q01");
            var q2 = sessionDetail.QuestionResults.First(q => q.QuestionId == "EXAM01-Q02");

            // Thao tác 1 của Giảng viên: "Xác nhận điểm AI" cho câu 1
            var finalizeQ1 = await orchestrator.FinalizeScoreAsync(new FinalizeScoreRequestDto
            {
                SessionId = sessionId,
                QuestionId = "EXAM01-Q01",
                FinalScore = q1.AiScore!.Value,
                TeacherNotes = "Xác nhận điểm AI đánh giá chính xác.",
                ChangeReason = "Đồng thuận với kết quả AI",
                ExpectedRowVersion = q1.RowVersion
            }, teacherId: "GV01");

            Assert.Equal(GradingState.Finalized, finalizeQ1.State);
            Assert.Equal(q1.AiScore.Value, finalizeQ1.TeacherFinalScore);
            Assert.Equal("GV01", finalizeQ1.FinalizedBy);

            // Thao tác 2 của Giảng viên: Override điểm cho câu 2 (nhập điểm GV = 9.5 kèm lý do)
            var finalizeQ2 = await orchestrator.FinalizeScoreAsync(new FinalizeScoreRequestDto
            {
                SessionId = sessionId,
                QuestionId = "EXAM01-Q02",
                FinalScore = 9.5m,
                TeacherNotes = "Sinh viên phân tích rất sâu về DIP và unit test.",
                ChangeReason = "Cộng điểm trả lời xuất sắc câu hỏi xoáy",
                ExpectedRowVersion = q2.RowVersion
            }, teacherId: "GV01");

            Assert.Equal(GradingState.Finalized, finalizeQ2.State);
            Assert.Equal(9.5m, finalizeQ2.TeacherFinalScore);
            Assert.Equal("GV01", finalizeQ2.FinalizedBy);
        }

        // =========================================================================
        // Bước 4: Kiểm tra Audit Trail log được ghi nhận
        // =========================================================================
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIVESDbContext>();
            var changeLogs = await db.VivaGradeChangeLogs
                .Include(c => c.GradingResult)
                .OrderBy(c => c.ChangedAtUtc)
                .ToListAsync();

            Assert.True(changeLogs.Count >= 2);
            var logQ2 = changeLogs.FirstOrDefault(l => l.GradingResult?.QuestionId == "EXAM01-Q02");
            Assert.NotNull(logQ2);
            Assert.Equal(9.5m, logQ2.NewScore);
            Assert.Equal("GV01", logQ2.ChangedBy);
            Assert.Equal("Cộng điểm trả lời xuất sắc câu hỏi xoáy", logQ2.Reason);
        }

        // =========================================================================
        // Bước 5: Sinh viên xem báo cáo chính thức ("Điểm chính thức")
        // =========================================================================
        using (var scope = sp.CreateScope())
        {
            var studentService = scope.ServiceProvider.GetRequiredService<IStudentReportService>();
            var report = await studentService.GetStudentReportAsync("10", "EXAM01");

            Assert.NotNull(report);
            var attempt = Assert.Single(report.Attempts);

            var q1 = attempt.QuestionResults.First(q => q.QuestionId == "EXAM01-Q01");
            Assert.Equal(q1.AiSuggestedScore, q1.EarnedScore); // Giữ nguyên điểm AI đã xác nhận

            var q2 = attempt.QuestionResults.First(q => q.QuestionId == "EXAM01-Q02");
            Assert.Equal(9.5m, q2.EarnedScore); // Điểm chính thức ưu tiên điểm GV override
            Assert.Equal(9.5m, q2.TeacherFinalScore);

            // Báo cáo lớp học cũng cập nhật theo điểm chính thức
            var classService = scope.ServiceProvider.GetRequiredService<IClassReportService>();
            var classStats = await classService.GetClassStatisticsAsync("GV01", "EXAM01", "EXAM01");
            Assert.NotNull(classStats);
            Assert.True(classStats.AverageClassScore >= 6.0m);
        }
    }

    [Fact]
    public async Task E2E_FaultIsolation_WhenAiThrows_RubricFallbackActivated()
    {
        var dbName = $"GradingE2E_Fallback_{Guid.NewGuid()}";
        var sessionId = Guid.NewGuid();

        // Tạo AI service luôn ném lỗi để kiểm tra fault isolation & keyword fallback
        var failingAi = new FailingAnswerGradingService();
        var sp = BuildServiceProvider(dbName, failingAi);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIVESDbContext>();
            await SeedExamAndSessionAsync(db, sessionId, "10");
        }

        using (var scope = sp.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IGradingOrchestrator>();
            var result = await orchestrator.GradeQuestionAsync(sessionId, "EXAM01-Q01");

            Assert.NotNull(result);
            Assert.Equal("KeywordFallback", result.AiProvider);
            Assert.Equal(GradingState.AiGraded, result.State);
            Assert.True(result.AiScore > 0, "Keyword fallback phải tính được điểm khi AI ném lỗi.");
        }
    }

    [Fact]
    public async Task E2E_Protection_FinalizedQuestions_NotOverwrittenByAiRegrade()
    {
        var dbName = $"GradingE2E_Protection_{Guid.NewGuid()}";
        var sessionId = Guid.NewGuid();
        var sp = BuildServiceProvider(dbName);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AIVESDbContext>();
            await SeedExamAndSessionAsync(db, sessionId, "10");
        }

        // Chấm lần 1 và Giảng viên chốt điểm
        using (var scope = sp.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IGradingOrchestrator>();
            await orchestrator.GradeSessionAsync(sessionId);

            var question = await orchestrator.GradeQuestionAsync(sessionId, "EXAM01-Q01");
            await orchestrator.FinalizeScoreAsync(new FinalizeScoreRequestDto
            {
                SessionId = sessionId,
                QuestionId = "EXAM01-Q01",
                FinalScore = 10.0m,
                TeacherNotes = "Điểm tuyệt đối",
                ExpectedRowVersion = question.RowVersion
            }, teacherId: "GV01");
        }

        // Kích hoạt regrade cả phiên (hoặc background job chạy lại)
        using (var scope = sp.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IGradingOrchestrator>();
            var summary = await orchestrator.GradeSessionAsync(sessionId);

            var finalizedQ = summary.QuestionResults.First(q => q.QuestionId == "EXAM01-Q01");
            Assert.Equal(GradingState.Finalized, finalizedQ.State);
            Assert.Equal(10.0m, finalizedQ.TeacherFinalScore); // Bảo vệ không bị AI ghi đè
        }
    }

    [Fact]
    public void E2E_DI_ServiceCollection_Resolves_AllGradingDependencies()
    {
        var dbName = $"GradingE2E_DI_{Guid.NewGuid()}";
        var sp = BuildServiceProvider(dbName);

        using var scope = sp.CreateScope();
        var orchestrator = scope.ServiceProvider.GetService<IGradingOrchestrator>();
        var gradingRepo = scope.ServiceProvider.GetService<IVivaGradingRepository>();
        var aiGrading = scope.ServiceProvider.GetService<IAnswerGradingService>();
        var rubricGrading = scope.ServiceProvider.GetService<IRubricGradingService>();

        Assert.NotNull(orchestrator);
        Assert.NotNull(gradingRepo);
        Assert.NotNull(aiGrading);
        Assert.NotNull(rubricGrading);
    }

    private sealed class FailingAnswerGradingService : IAnswerGradingService
    {
        public Task<QuestionGradingResultDto> GradeAnswerAsync(GradingInputDto input, CancellationToken ct = default)
        {
            throw new HttpRequestException("Gemini / OpenAI API rate limit exceeded (503 Service Unavailable).");
        }
    }
}
