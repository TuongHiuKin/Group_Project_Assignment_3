using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaSessionRepository
{
    Task<VivaSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default);
    Task<VivaSession?> GetWithDetailsAsync(Guid sessionId, CancellationToken ct = default);
    Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default);
    Task<bool> UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default);
    Task<bool> CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default);
}
