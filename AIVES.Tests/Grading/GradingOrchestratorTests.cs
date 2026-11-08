using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.ExamReporting;
using AIVES.BLL.Services.Grading;
using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Implementations;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using GradingState = AIVES.BLL.Models.Grading.GradingState;

namespace AIVES.Tests.Grading;

public class GradingOrchestratorTests
{
    private static AIVESDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AIVESDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new AIVESDbContext(options);
    }

    private sealed class FakeAnswerGradingService : IAnswerGradingService
    {
        public Func<GradingInputDto, Task<QuestionGradingResultDto>>? Handler { get; set; }

        public Task<QuestionGradingResultDto> GradeAnswerAsync(GradingInputDto input, CancellationToken ct = default)
        {
            if (Handler != null)
            {
                return Handler(input);
            }

            return Task.FromResult(new QuestionGradingResultDto
            {
                SessionId = input.SessionId,
                QuestionId = input.QuestionId,
                AiScore = 8.5m,
                AiFeedback = "AI trả lời tốt",
                AiProvider = "MockAI",
                AiModel = "v1",
                State = GradingState.AiGraded,
                CriteriaOutcomes = new List<CriterionOutcomeDto>
                {
                    new() { CriterionCode = "C1", CriterionName = "Khái niệm", IsMet = true, AwardedPoints = 4m }
                }
            });
        }
    }

    [Fact]
    public async Task GradeSessionAsync_grades_all_questions_successfully_with_ai()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var sessionRepo = new VivaSessionRepository(context);
        var questionRepo = new VivaQuestionRepository(context);
        var gradingRepo = new VivaGradingRepository(context);

        var q1 = new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "OOP", Content = "OOP là gì?" };
        var q2 = new VivaQuestion { QuestionId = "EXAM01-Q02", ExamId = "EXAM01", Title = "SOLID", Content = "SOLID là gì?" };
        context.VivaQuestions.AddRange(q1, q2);

        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-1",
            State = 8,
            StartedAtUtc = DateTime.UtcNow
        };
        context.VivaSessions.Add(session);

        var turn1 = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            TranscriptText = "Lập trình hướng đối tượng gồm đóng gói kế thừa đa hình",
            CreatedAtUtc = DateTime.UtcNow
        };
        var turn2 = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q02",
            TurnOrdinal = 2,
            TranscriptText = "SOLID gồm 5 nguyên lý thiết kế phần mềm",
            CreatedAtUtc = DateTime.UtcNow
        };
        context.VivaSessionTurns.AddRange(turn1, turn2);
        await context.SaveChangesAsync();

        var aiService = new FakeAnswerGradingService();
        var rubricService = new RubricGradingService();
        var orchestrator = new GradingOrchestrator(
            sessionRepo, questionRepo, gradingRepo, aiService, rubricService, NullLogger<GradingOrchestrator>.Instance);

        var summary = await orchestrator.GradeSessionAsync(sessionId);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.QuestionResults.Count);
        Assert.All(summary.QuestionResults, q =>
        {
            Assert.Equal(GradingState.AiGraded, q.State);
            Assert.Equal("MockAI", q.AiProvider);
            Assert.NotNull(q.AiScore);
        });
        Assert.Equal(GradingState.AiGraded, summary.OverallState);
    }

    [Fact]
    public async Task GradeQuestion_activates_keyword_fallback_when_ai_throws()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var sessionRepo = new VivaSessionRepository(context);
        var questionRepo = new VivaQuestionRepository(context);
        var gradingRepo = new VivaGradingRepository(context);

        var q1 = new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "OOP", Content = "OOP là gì?" };
        context.VivaQuestions.Add(q1);

        var sessionId = Guid.NewGuid();
        var session = new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-1",
            State = 8,
            StartedAtUtc = DateTime.UtcNow
        };
        context.VivaSessions.Add(session);

        var turn = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            TranscriptText = "DbContext sống trong phạm vi mỗi request, nếu dùng Singleton sẽ gây memory leak trong change tracker và không thread-safe",
            CreatedAtUtc = DateTime.UtcNow
        };
        context.VivaSessionTurns.Add(turn);
        await context.SaveChangesAsync();

        var aiService = new FakeAnswerGradingService
        {
            Handler = _ => throw new HttpRequestException("Gemini API Timeout 504")
        };
        var rubricService = new RubricGradingService();
        var orchestrator = new GradingOrchestrator(
            sessionRepo, questionRepo, gradingRepo, aiService, rubricService, NullLogger<GradingOrchestrator>.Instance);

        var result = await orchestrator.GradeQuestionAsync(sessionId, "EXAM01-Q01");

        Assert.NotNull(result);
        Assert.Equal("KeywordFallback", result.AiProvider);
        Assert.Equal("RubricCatalog", result.AiModel);
        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.True(result.AiScore > 0, "Fallback rubric score should be calculated from matched keywords.");
    }

    [Fact]
    public async Task GradeQuestion_does_not_overwrite_finalized_score()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var sessionRepo = new VivaSessionRepository(context);
        var questionRepo = new VivaQuestionRepository(context);
        var gradingRepo = new VivaGradingRepository(context);

        var q1 = new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "OOP", Content = "OOP là gì?" };
        context.VivaQuestions.Add(q1);

        var sessionId = Guid.NewGuid();
        var session = new VivaSession { SessionId = sessionId, ExamId = "EXAM01", StudentId = "student-1", State = 8, StartedAtUtc = DateTime.UtcNow };
        context.VivaSessions.Add(session);

        // Pre-seed finalized question
        var finalizedResult = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            AiScore = 7.0m,
            TeacherFinalScore = 9.5m,
            FinalizedBy = "teacher-kien",
            FinalizedAtUtc = DateTime.UtcNow,
            GradingState = (byte)GradingState.Finalized
        };
        context.VivaGradingResults.Add(finalizedResult);
        await context.SaveChangesAsync();

        var aiServiceCalled = false;
        var aiService = new FakeAnswerGradingService
        {
            Handler = _ =>
            {
                aiServiceCalled = true;
                return Task.FromResult(new QuestionGradingResultDto { AiScore = 10m });
            }
        };
        var rubricService = new RubricGradingService();
        var orchestrator = new GradingOrchestrator(
            sessionRepo, questionRepo, gradingRepo, aiService, rubricService, NullLogger<GradingOrchestrator>.Instance);

        var result = await orchestrator.GradeQuestionAsync(sessionId, "EXAM01-Q01");

        Assert.False(aiServiceCalled, "AI service should not be called for already finalized question.");
        Assert.Equal(9.5m, result.TeacherFinalScore);
        Assert.Equal(GradingState.Finalized, result.State);
        Assert.Equal("teacher-kien", result.FinalizedBy);
    }

    [Fact]
    public async Task FinalizeScoreAsync_confirms_score_and_records_audit_trail()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var sessionRepo = new VivaSessionRepository(context);
        var questionRepo = new VivaQuestionRepository(context);
        var gradingRepo = new VivaGradingRepository(context);

        var q1 = new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "OOP", Content = "OOP là gì?" };
        context.VivaQuestions.Add(q1);

        var sessionId = Guid.NewGuid();
        var session = new VivaSession { SessionId = sessionId, ExamId = "EXAM01", StudentId = "student-1", State = 8, StartedAtUtc = DateTime.UtcNow };
        context.VivaSessions.Add(session);

        var initialGrading = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            AiScore = 8.0m,
            GradingState = (byte)GradingState.AiGraded
        };
        context.VivaGradingResults.Add(initialGrading);
        await context.SaveChangesAsync();

        var orchestrator = new GradingOrchestrator(
            sessionRepo, questionRepo, gradingRepo, new FakeAnswerGradingService(), new RubricGradingService(), NullLogger<GradingOrchestrator>.Instance);

        var finalizeReq = new FinalizeScoreRequestDto
        {
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            FinalScore = 8.5m,
            TeacherNotes = "Duyệt điểm tốt"
        };

        var finalized = await orchestrator.FinalizeScoreAsync(finalizeReq, "lecturer-01");

        Assert.Equal(GradingState.Finalized, finalized.State);
        Assert.Equal(8.5m, finalized.TeacherFinalScore);
        Assert.Equal("lecturer-01", finalized.FinalizedBy);

        var logs = await gradingRepo.GetChangeLogsAsync(initialGrading.Id);
        var log = Assert.Single(logs);
        Assert.Equal(8.0m, log.OldScore);
        Assert.Equal(8.5m, log.NewScore);
    }

    [Fact]
    public async Task GetSessionGradingAsync_computes_overall_state_correctly()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var sessionRepo = new VivaSessionRepository(context);
        var questionRepo = new VivaQuestionRepository(context);
        var gradingRepo = new VivaGradingRepository(context);

        var q1 = new VivaQuestion { QuestionId = "EXAM01-Q01", ExamId = "EXAM01", Title = "Q1", Content = "Content 1" };
        var q2 = new VivaQuestion { QuestionId = "EXAM01-Q02", ExamId = "EXAM01", Title = "Q2", Content = "Content 2" };
        context.VivaQuestions.AddRange(q1, q2);

        var sessionId = Guid.NewGuid();
        var session = new VivaSession { SessionId = sessionId, ExamId = "EXAM01", StudentId = "student-1", State = 8, StartedAtUtc = DateTime.UtcNow };
        context.VivaSessions.Add(session);

        // Q1 is finalized, Q2 is pending
        context.VivaGradingResults.Add(new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            AiScore = 8.0m,
            TeacherFinalScore = 8.0m,
            GradingState = (byte)GradingState.Finalized
        });
        await context.SaveChangesAsync();

        var orchestrator = new GradingOrchestrator(
            sessionRepo, questionRepo, gradingRepo, new FakeAnswerGradingService(), new RubricGradingService(), NullLogger<GradingOrchestrator>.Instance);

        var summary = await orchestrator.GetSessionGradingAsync(sessionId);

        Assert.NotNull(summary);
        Assert.Equal(GradingState.Pending, summary.OverallState);
        Assert.False(summary.IsFullyFinalized);

        // Finalize Q2 as well
        context.VivaGradingResults.Add(new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q02",
            AiScore = 7.0m,
            TeacherFinalScore = 7.5m,
            GradingState = (byte)GradingState.Finalized
        });
        await context.SaveChangesAsync();

        var finalizedSummary = await orchestrator.GetSessionGradingAsync(sessionId);
        Assert.NotNull(finalizedSummary);
        Assert.Equal(GradingState.Finalized, finalizedSummary.OverallState);
        Assert.True(finalizedSummary.IsFullyFinalized);
        Assert.Equal(15.5m, finalizedSummary.TotalFinalScore);
    }

    /// <summary>
    /// Regression: when AI is not configured, the resolver returns
    /// <c>UnavailableAnswerGradingService</c>. It used to report AiScore = 0m, which the orchestrator
    /// read as "grading succeeded" and persisted as Failed/0 points. It must fall back to keywords.
    /// </summary>
    [Fact]
    public async Task GradeQuestion_falls_back_to_keywords_when_ai_service_is_unavailable()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var sessionRepo = new VivaSessionRepository(context);
        var questionRepo = new VivaQuestionRepository(context);
        var gradingRepo = new VivaGradingRepository(context);

        context.VivaQuestions.Add(new VivaQuestion
        {
            QuestionId = "EXAM01-Q01",
            ExamId = "EXAM01",
            Title = "OOP",
            Content = "DbContext lifetime?"
        });

        var sessionId = Guid.NewGuid();
        context.VivaSessions.Add(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "student-1",
            State = 8,
            StartedAtUtc = DateTime.UtcNow
        });

        context.VivaSessionTurns.Add(new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            TurnOrdinal = 1,
            TranscriptText = "DbContext sống trong phạm vi mỗi request, dùng Singleton sẽ gây rò rỉ bộ nhớ trong change tracker và không thread-safe",
            CreatedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var unavailable = new AIVES.BLL.Services.Ai.Unavailable.UnavailableAnswerGradingService(
            AIVES.BLL.Services.Ai.Unavailable.AiUnavailableMessages.RealAiDisabled);

        var orchestrator = new GradingOrchestrator(
            sessionRepo, questionRepo, gradingRepo, unavailable, new RubricGradingService(),
            NullLogger<GradingOrchestrator>.Instance);

        var result = await orchestrator.GradeQuestionAsync(sessionId, "EXAM01-Q01");

        Assert.Equal("KeywordFallback", result.AiProvider);
        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.True(result.AiScore > 0, "Keyword fallback must award rubric points instead of persisting 0.");
    }

    [Fact]
    public void Unavailable_grading_service_reports_no_score_so_the_orchestrator_can_fallback()
    {
        var service = new AIVES.BLL.Services.Ai.Unavailable.UnavailableAnswerGradingService(
            AIVES.BLL.Services.Ai.Unavailable.AiUnavailableMessages.RealAiDisabled);

        var result = service.GradeAnswerAsync(new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "EXAM01-Q01",
            QuestionText = "Q",
            MaxPoints = 10m
        }).GetAwaiter().GetResult();

        Assert.Null(result.AiScore);
        Assert.Equal(GradingState.Failed, result.State);
    }
}
