using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaSessionRepository
{
    Task<VivaSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default);
    Task<VivaSession?> GetWithDetailsAsync(Guid sessionId, CancellationToken ct = default);
    Task<List<VivaSession>> GetSessionsWithDetailsAsync(string? examId = null, string? studentId = null, CancellationToken ct = default);
    Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default);
    Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default);
    Task UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default);
    Task CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default);
}
