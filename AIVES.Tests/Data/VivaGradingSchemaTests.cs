using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AIVES.Tests.Data;

/// <summary>
/// G4-DB-04 (#9): the grading tables only existed in the EF model, never in a SQL script, so a fresh
/// database created from database/*.sql could not open /Grading at all.
/// These run against the reviewed scripts loaded by <see cref="TemporarySqlServerDatabase"/>.
/// </summary>
[Trait("Category", "SqlServerWrite")]
public sealed class VivaGradingSchemaTests(TemporarySqlServerDatabase database) : IClassFixture<TemporarySqlServerDatabase>
{
    private const string QuestionId = "EXAM01-Q01";

    private static async Task<(Guid SessionId, string QuestionId)> SeedSessionAsync(AIVESDbContext context)
    {
        var sessionId = Guid.NewGuid();
        context.VivaSessions.Add(new VivaSession
        {
            SessionId = sessionId,
            ExamId = "EXAM01",
            StudentId = "sv-schema-test",
            State = 8,
            StartedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return (sessionId, QuestionId);
    }

    [SqlServerWriteFact]
    public async Task Script_005_creates_both_grading_tables()
    {
        await using var context = database.CreateContext();
        await context.Database.OpenConnectionAsync();

        var tables = await context.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables WHERE name IN ('VivaGradingResult', 'VivaGradeChangeLog') ORDER BY name")
            .ToListAsync();

        Assert.Equal(new[] { "VivaGradeChangeLog", "VivaGradingResult" }, tables);
    }

    [SqlServerWriteFact]
    public async Task Grading_result_round_trips_through_a_real_rowversion_token()
    {
        await using var context = database.CreateContext();
        var (sessionId, questionId) = await SeedSessionAsync(context);

        var repo = new VivaGradingRepository(context);
        var saved = await repo.UpsertAiResultAsync(new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = questionId,
            AiScore = 7.5m,
            AiFeedback = "Ổn",
            AiProvider = "Mock",
            AiModel = "v1",
            GradingState = 1
        });

        Assert.NotNull(saved.RowVersion);
        Assert.NotEmpty(saved.RowVersion!);

        var reloaded = await repo.GetByQuestionAsync(sessionId, questionId);
        Assert.NotNull(reloaded);
        Assert.Equal(7.5m, reloaded!.AiScore);
    }

    [SqlServerWriteFact]
    public async Task Finalizing_a_score_writes_an_audit_row_and_requires_a_reason_on_edit()
    {
        await using var context = database.CreateContext();
        var (sessionId, questionId) = await SeedSessionAsync(context);
        var repo = new VivaGradingRepository(context);

        await repo.UpsertAiResultAsync(new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = questionId,
            AiScore = 6m,
            GradingState = 1
        });

        var finalized = await repo.FinalizeScoreAsync(
            sessionId, questionId, 8.5m, "Nhận xét của GV", "gv01", expectedRowVersion: null,
            changeReason: "Xác nhận điểm AI");
        Assert.Equal(2, finalized.GradingState);
        Assert.Equal(8.5m, finalized.TeacherFinalScore);

        // Editing an already-finalized score without a reason must be rejected (locked business decision 3).
        await Assert.ThrowsAsync<ArgumentException>(() => repo.FinalizeScoreAsync(
            sessionId, questionId, 9m, "ghi chú", "gv01", expectedRowVersion: null, changeReason: "  "));

        var logs = await repo.GetChangeLogsAsync(finalized.Id);
        Assert.Single(logs);
        Assert.Equal("Xác nhận điểm AI", logs[0].Reason);
    }

    [SqlServerWriteFact]
    public async Task Check_constraint_rejects_a_blank_audit_reason()
    {
        await using var context = database.CreateContext();
        var (sessionId, questionId) = await SeedSessionAsync(context);

        var result = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = questionId,
            AiScore = 5m,
            GradingState = 2
        };
        context.VivaGradingResults.Add(result);
        await context.SaveChangesAsync();

        context.VivaGradeChangeLogs.Add(new VivaGradeChangeLog
        {
            GradingResultId = result.Id,
            NewScore = 6m,
            ChangedBy = "gv01",
            Reason = "   ",
            ChangedAtUtc = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [SqlServerWriteFact]
    public async Task Script_006_seeds_rubric_criteria_that_the_grader_reads()
    {
        await using var context = database.CreateContext();

        var seeded = await context.VivaRubricCriteria.AsNoTracking().ToListAsync();
        Assert.Equal(9, seeded.Count);

        var service = new AIVES.BLL.Services.ExamReporting.RubricGradingService(
            new VivaRubricRepository(context));
        var rubric = await service.GetRubricAsync(QuestionId);

        Assert.NotNull(rubric);
        Assert.Equal(3, rubric!.Criteria.Count);
        Assert.Equal(10m, rubric.Criteria.Sum(rubric.PointsFor));
        Assert.All(rubric.Criteria, c => Assert.NotEmpty(c.Keywords));
    }
}
