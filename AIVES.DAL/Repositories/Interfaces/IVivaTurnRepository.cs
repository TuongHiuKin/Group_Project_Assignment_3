using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaTurnRepository
{
    Task<Guid> CreateTurnAsync(VivaSessionTurn turn, CancellationToken ct = default);
    Task<VivaSessionTurn?> GetByIdAsync(Guid turnId, CancellationToken ct = default);
    Task SaveTranscriptAsync(Guid turnId, string transcript, decimal confidence, CancellationToken ct = default);
    Task SaveFollowUpAsync(Guid turnId, string followUpQuestion, string? audioPath, CancellationToken ct = default);
    Task<int> GetTurnCountForSessionAsync(Guid sessionId, CancellationToken ct = default);
}
