using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Implementations;
using Microsoft.EntityFrameworkCore;

namespace AIVES.Tests.Data;

public class VivaGradingRepositoryTests
{
    private static AIVESDbContext CreateInMemoryContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AIVESDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        return new AIVESDbContext(options);
    }

    [Fact]
    public async Task UpsertAiResultAsync_creates_new_result_when_none_exists()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var repo = new VivaGradingRepository(context);

        context.VivaQuestions.Add(new VivaQuestion { QuestionId = "Q01", ExamId = "EXAM01", Title = "Title", Content = "Content" });
        await context.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var result = new VivaGradingResult
        {
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 8.5m,
            AiFeedback = "Tốt",
            Strengths = "Rõ ràng",
            Weaknesses = "Thiếu ví dụ",
            AiProvider = "Mock",
            AiModel = "v1",
            GradingState = 1 // AiGraded
        };

        var saved = await repo.UpsertAiResultAsync(result);

        Assert.NotEqual(Guid.Empty, saved.Id);
        var fetched = await repo.GetByQuestionAsync(sessionId, "Q01");
        Assert.NotNull(fetched);
        Assert.Equal(8.5m, fetched.AiScore);
        Assert.Equal(1, fetched.GradingState);
    }

    [Fact]
    public async Task UpsertAiResultAsync_updates_existing_result_when_not_finalized()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var repo = new VivaGradingRepository(context);

        context.VivaQuestions.Add(new VivaQuestion { QuestionId = "Q01", ExamId = "EXAM01", Title = "Title", Content = "Content" });
        await context.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var initial = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 7.0m,
            GradingState = 1
        };
        context.VivaGradingResults.Add(initial);
        await context.SaveChangesAsync();

        var updated = new VivaGradingResult
        {
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 9.0m,
            AiFeedback = "Cải thiện",
            GradingState = 1
        };

        var saved = await repo.UpsertAiResultAsync(updated);

        Assert.Equal(initial.Id, saved.Id);
        Assert.Equal(9.0m, saved.AiScore);
        Assert.Equal("Cải thiện", saved.AiFeedback);
    }

    [Fact]
    public async Task UpsertAiResultAsync_does_not_overwrite_finalized_score()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var repo = new VivaGradingRepository(context);

        context.VivaQuestions.Add(new VivaQuestion { QuestionId = "Q01", ExamId = "EXAM01", Title = "Title", Content = "Content" });
        await context.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var finalized = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 7.0m,
            TeacherFinalScore = 8.0m,
            GradingState = 2 // Finalized
        };
        context.VivaGradingResults.Add(finalized);
        await context.SaveChangesAsync();

        var regradeAttempt = new VivaGradingResult
        {
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 10.0m,
            GradingState = 1
        };

        var result = await repo.UpsertAiResultAsync(regradeAttempt);

        Assert.Equal(8.0m, result.TeacherFinalScore);
        Assert.Equal(2, result.GradingState);
        Assert.Equal(7.0m, result.AiScore);
    }

    [Fact]
    public async Task FinalizeScoreAsync_creates_audit_log_and_marks_finalized()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var repo = new VivaGradingRepository(context);

        context.VivaQuestions.Add(new VivaQuestion { QuestionId = "Q01", ExamId = "EXAM01", Title = "Title", Content = "Content" });
        await context.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var grading = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 8.0m,
            GradingState = 1 // AiGraded
        };
        context.VivaGradingResults.Add(grading);
        await context.SaveChangesAsync();

        var finalized = await repo.FinalizeScoreAsync(
            sessionId,
            "Q01",
            finalScore: 8.5m,
            teacherNotes: "Ghi nhận trả lời tốt",
            teacherId: "teacher-01",
            expectedRowVersion: null,
            changeReason: null);

        Assert.Equal(2, finalized.GradingState);
        Assert.Equal(8.5m, finalized.TeacherFinalScore);
        Assert.Equal("teacher-01", finalized.FinalizedBy);
        Assert.NotNull(finalized.FinalizedAtUtc);

        var logs = await repo.GetChangeLogsAsync(grading.Id);
        var log = Assert.Single(logs);
        Assert.Equal(8.0m, log.OldScore);
        Assert.Equal(8.5m, log.NewScore);
        Assert.Equal("teacher-01", log.ChangedBy);
    }

    [Fact]
    public async Task FinalizeScoreAsync_requires_reason_when_modifying_already_finalized_score()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var repo = new VivaGradingRepository(context);

        context.VivaQuestions.Add(new VivaQuestion { QuestionId = "Q01", ExamId = "EXAM01", Title = "Title", Content = "Content" });
        await context.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var grading = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 8.0m,
            TeacherFinalScore = 8.0m,
            GradingState = 2 // Finalized
        };
        context.VivaGradingResults.Add(grading);
        await context.SaveChangesAsync();

        // Attempting to modify without reason must throw ArgumentException
        await Assert.ThrowsAsync<ArgumentException>(() => repo.FinalizeScoreAsync(
            sessionId,
            "Q01",
            finalScore: 9.0m,
            teacherNotes: "Sửa điểm",
            teacherId: "teacher-01",
            expectedRowVersion: null,
            changeReason: "   "));

        // With valid reason, modification succeeds
        var updated = await repo.FinalizeScoreAsync(
            sessionId,
            "Q01",
            finalScore: 9.0m,
            teacherNotes: "Sửa điểm sau khi phúc khảo",
            teacherId: "teacher-01",
            expectedRowVersion: null,
            changeReason: "Phúc khảo câu hỏi");

        Assert.Equal(9.0m, updated.TeacherFinalScore);
        var logs = await repo.GetChangeLogsAsync(grading.Id);
        var log = Assert.Single(logs);
        Assert.Equal("Phúc khảo câu hỏi", log.Reason);
        Assert.Equal(8.0m, log.OldScore);
        Assert.Equal(9.0m, log.NewScore);
    }

    [Fact]
    public async Task FinalizeScoreAsync_detects_concurrency_mismatch()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var repo = new VivaGradingRepository(context);

        context.VivaQuestions.Add(new VivaQuestion { QuestionId = "Q01", ExamId = "EXAM01", Title = "Title", Content = "Content" });
        await context.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var grading = new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            QuestionId = "Q01",
            AiScore = 8.0m,
            RowVersion = new byte[] { 1, 2, 3, 4 }
        };
        context.VivaGradingResults.Add(grading);
        await context.SaveChangesAsync();

        var staleRowVersion = new byte[] { 9, 9, 9, 9 };

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => repo.FinalizeScoreAsync(
            sessionId,
            "Q01",
            finalScore: 8.5m,
            teacherNotes: "Notes",
            teacherId: "teacher-01",
            expectedRowVersion: staleRowVersion,
            changeReason: null));
    }

    [Fact]
    public async Task GetPendingOrFailedSessionIdsAsync_finds_completed_sessions_needing_grading()
    {
        var dbName = Guid.NewGuid().ToString();
        using var context = CreateInMemoryContext(dbName);
        var repo = new VivaGradingRepository(context);

        var session1 = new VivaSession { SessionId = Guid.NewGuid(), ExamId = "EXAM01", StudentId = "S1", State = 8, StartedAtUtc = DateTime.UtcNow };
        var session2 = new VivaSession { SessionId = Guid.NewGuid(), ExamId = "EXAM01", StudentId = "S2", State = 8, StartedAtUtc = DateTime.UtcNow };
        var session3 = new VivaSession { SessionId = Guid.NewGuid(), ExamId = "EXAM01", StudentId = "S3", State = 1, StartedAtUtc = DateTime.UtcNow }; // Not completed
        context.VivaSessions.AddRange(session1, session2, session3);

        // session2 has failed grading
        context.VivaGradingResults.Add(new VivaGradingResult
        {
            Id = Guid.NewGuid(),
            SessionId = session2.SessionId,
            QuestionId = "Q01",
            GradingState = 3 // Failed
        });

        await context.SaveChangesAsync();

        var pendingIds = await repo.GetPendingOrFailedSessionIdsAsync(10);

        Assert.Contains(session1.SessionId, pendingIds); // No grading result at all
        Assert.Contains(session2.SessionId, pendingIds); // Has failed grading result
        Assert.DoesNotContain(session3.SessionId, pendingIds); // Session not completed
    }
}
