using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AIVES.DAL.Repositories.Implementations;

public sealed class VivaSessionRepository(AIVESDbContext context) : IVivaSessionRepository
{
    private static readonly byte CompletedState = 8; // VivaSessionState.Completed (enum ở BLL, DAL không tham chiếu BLL)

    public Task<VivaSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default) =>
        context.VivaSessions.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);

    public Task<VivaSession?> GetWithDetailsAsync(Guid sessionId, CancellationToken ct = default) =>
        context.VivaSessions.AsNoTracking()
            .Include(x => x.Turns)
            .SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);

    public async Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (await context.VivaSessions.AnyAsync(x => x.SessionId == session.SessionId, ct))
            return session.SessionId;
        context.VivaSessions.Add(session);
        await SaveChangesSafeAsync(ct);
        return session.SessionId;
    }

    public async Task UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default)
    {
        var entity = await context.VivaSessions.SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (entity is null) return;
        entity.State = state;
        await SaveChangesSafeAsync(ct);
    }

    public async Task CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default)
    {
        var entity = await context.VivaSessions.SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        if (entity is null) return;
        entity.State = CompletedState;
        entity.CompletedAtUtc = completedAtUtc;
        await SaveChangesSafeAsync(ct);
    }

    private static readonly Dictionary<string, string[]> EquivalentIdMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SV01"] = new[] { "SV01", "1" },
        ["1"] = new[] { "SV01", "1" },
        ["SV02"] = new[] { "SV02", "2" },
        ["2"] = new[] { "SV02", "2" },
        ["SV03"] = new[] { "SV03", "3" },
        ["3"] = new[] { "SV03", "3" }
    };

    private static string[] GetCandidateStudentIds(string studentId)
    {
        if (EquivalentIdMap.TryGetValue(studentId, out var aliases))
            return aliases;
        return new[] { studentId };
    }

    public Task<List<VivaSession>> GetSessionsByStudentAndExamAsync(string studentId, string examId, CancellationToken ct = default)
    {
        var candidateIds = GetCandidateStudentIds(studentId);
        return context.VivaSessions.AsNoTracking()
            .Include(x => x.Turns)
                .ThenInclude(t => t.Question)
            .Where(x => candidateIds.Contains(x.StudentId) && x.ExamId == examId)
            .OrderBy(x => x.StartedAtUtc)
            .ToListAsync(ct);
    }

    public Task<List<VivaSession>> GetSessionsByExamAsync(string examId, CancellationToken ct = default) =>
        context.VivaSessions.AsNoTracking()
            .Include(x => x.Turns)
                .ThenInclude(t => t.Question)
            .Where(x => x.ExamId == examId)
            .OrderBy(x => x.StartedAtUtc)
            .ToListAsync(ct);

    private async Task SaveChangesSafeAsync(CancellationToken ct)
    {
        try { await context.SaveChangesAsync(ct); }
        catch { context.ChangeTracker.Clear(); throw; }
    }
}
