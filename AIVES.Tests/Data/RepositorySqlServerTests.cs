using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Implementations;
using AIVES.DAL.Repositories.Models;
using Microsoft.EntityFrameworkCore;

namespace AIVES.Tests.Data;

[Trait("Category", "SqlServerWrite")]
public sealed class RepositorySqlServerTests(TemporarySqlServerDatabase database) : IClassFixture<TemporarySqlServerDatabase>
{
    private static string Key() => Guid.NewGuid().ToString("N");
    private static Task<int> Account(AIVESDbContext db, string? email = null) =>
        new SystemAccountRepository(db).CreateAsync(new AccountCreate("Test " + Key(), email ?? Key() + "@example.test", "opaque-test-hash", 1));

    [SqlServerWriteFact]
    public async Task Accounts_normalize_email_preserve_profile_role_and_soft_delete_reserves_email()
    {
        await using var db = database.CreateContext();
        var repo = new SystemAccountRepository(db);
        var email = Key() + "@example.test";
        var id = await Account(db, "  " + email.ToUpperInvariant() + "  ");
        Assert.Equal(id, (await repo.GetByEmailAsync(email.ToUpperInvariant()))!.AccountId);
        Assert.False((await repo.GetByIdAsync(id))!.IsDeleted);
        Assert.True(await repo.EmailExistsAsync(email));
        Assert.False(await repo.EmailExistsAsync(email, id));
        Assert.True(await repo.UpdateAsync(new(id, "Admin edited", email, 2)));
        Assert.True(await repo.UpdateProfileAsync(new(id, "Own profile", email)));
        Assert.Equal(2, (await repo.GetByIdAsync(id))!.AccountRole);
        Assert.Equal("opaque-test-hash", (await repo.GetByIdAsync(id))!.AccountPasswordHash);
        Assert.True(await repo.SoftDeleteAsync(id));
        Assert.Null(await repo.GetByIdAsync(id));
        Assert.Null(await repo.GetByEmailAsync(email));
        Assert.Empty(await repo.SearchAsync(email));
        Assert.True((await repo.GetByIdAsync(id, includeDeleted: true))!.IsDeleted);
        Assert.True(await repo.EmailExistsAsync(email));
        Assert.False(await repo.UpdateAsync(new(id, "Cannot restore", email, 1)));
        await Assert.ThrowsAsync<DbUpdateException>(() => Account(db, email));
        Assert.Equal(DeleteResult.Deleted, await repo.HardDeleteAsync(id));
        Assert.False(await repo.EmailExistsAsync(email));

        // Vai trò ngoài 1..3 bị ràng buộc DB chặn.
        await Assert.ThrowsAsync<DbUpdateException>(() => repo.CreateAsync(new("Bad role", Key() + "@example.test", "opaque", 3)));
    }

    /// <summary>
    /// Tài khoản có dữ liệu thi/chấm thì không xoá cứng được. Trước đây IsReferencedAsync kiểm tra
    /// bảng NewsArticle; nay kiểm tra VivaSession (sinh viên dự thi), VivaGradingResult.FinalizedBy
    /// (người chốt điểm) và VivaGradeChangeLog.ChangedBy (người sửa điểm).
    /// </summary>
    [SqlServerWriteFact]
    public async Task HardDelete_is_blocked_by_viva_references()
    {
        await using var db = database.CreateContext();
        var repo = new SystemAccountRepository(db);

        var student = await Account(db);
        var teacher = await Account(db);
        var auditor = await Account(db);
        var empty = await Account(db);

        Assert.False(await repo.IsReferencedAsync(empty));

        db.VivaSessions.Add(new VivaSession
        {
            SessionId = Guid.NewGuid(),
            ExamId = "EXAM01",
            StudentId = student.ToString(),
            State = 1,
            StartedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.True(await repo.IsReferencedAsync(student));
        Assert.Equal(DeleteResult.InUse, await repo.HardDeleteAsync(student));

        var sessionId = Guid.NewGuid();
        var gradingId = Guid.NewGuid();
        db.VivaGradingResults.Add(new VivaGradingResult
        {
            Id = gradingId,
            SessionId = sessionId,
            QuestionId = "EXAM01-Q01",
            AiScore = 7.5m,
            FinalizedBy = teacher.ToString(),
            RowVersion = new byte[] { 1 }
        });
        await db.SaveChangesAsync();
        Assert.True(await repo.IsReferencedAsync(teacher));

        db.VivaGradeChangeLogs.Add(new VivaGradeChangeLog
        {
            GradingResultId = gradingId,
            NewScore = 8.0m,
            ChangedBy = auditor.ToString(),
            Reason = "Sửa điểm sau phúc khảo",
            ChangedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Assert.True(await repo.IsReferencedAsync(auditor));

        // Tài khoản không có dữ liệu Viva vẫn xoá cứng được.
        Assert.Equal(DeleteResult.Deleted, await repo.HardDeleteAsync(empty));
    }

    [SqlServerWriteFact]
    public async Task Upgrade_script_rerun_preserves_soft_deleted_account()
    {
        await using var db = database.CreateContext();
        var repo = new SystemAccountRepository(db);
        var id = await Account(db);
        await repo.SoftDeleteAsync(id);
        await database.RunScriptAsync("002-add-system-account-soft-delete.sql");
        Assert.True((await repo.GetByIdAsync(id, includeDeleted: true))!.IsDeleted);
    }
}
