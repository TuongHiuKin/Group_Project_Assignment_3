using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Interfaces.VivaInterview;

/// <summary>Business boundary for read-only recovery and authoritative countdown transitions.</summary>
public interface IVivaRealtimeSessionService
{
    Task<VivaSessionSnapshotDto> GetSnapshotAsync(Guid sessionId, CancellationToken ct = default);
    Task<VivaSessionSnapshotDto> PrepareJoinAsync(Guid sessionId, string userId, CancellationToken ct = default);
    Task<VivaTimeRemainingDto> BeginAnsweringAsync(Guid sessionId, string userId,
        IVivaRealtimePublisher publisher, CancellationToken ct = default);
    Task TickAsync(Guid sessionId, IVivaRealtimePublisher publisher, CancellationToken ct = default);
}
