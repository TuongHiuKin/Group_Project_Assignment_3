using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Interfaces.VivaInterview;

/// <summary>Business boundary for read-only recovery and authoritative countdown transitions.</summary>
public interface IVivaRealtimeSessionService
{
    Task<VivaSessionSnapshotDto> GetSnapshotAsync(Guid sessionId, CancellationToken ct = default);
    Task<VivaSessionSnapshotDto> PrepareJoinAsync(Guid sessionId, string userId, CancellationToken ct = default);
    Task<VivaTimeRemainingDto> BeginAnsweringAsync(Guid sessionId, string userId,
        IVivaRealtimePublisher publisher, CancellationToken ct = default);
    Task<string?> TickAsync(Guid sessionId, IVivaRealtimePublisher publisher, CancellationToken ct = default);

    /// <summary>
    /// Khởi động đồng hồ cho câu đang hỏi nếu chưa có deadline nào (client không gọi được
    /// BeginAnswering, hoặc app vừa restart nên mất deadline trong RAM). Gọi từ worker mỗi giây
    /// để phiên không kẹt vĩnh viễn ở trạng thái chờ trả lời.
    ///
    /// Không cấp giờ khi phiên ĐANG ĐỌC câu hỏi: trạng thái <c>QuestionDelivered</c> nghĩa là TTS
    /// còn đang phát (hoặc sắp phát), nên phải chờ client gọi <c>BeginAnswering</c>. Chỉ khi phiên
    /// đã ở <c>Answering</c> mà mất deadline — hoặc <c>QuestionDelivered</c> quá
    /// <see cref="QuestionDeliveryGraceSeconds"/> giây không ai gọi — mới coi là kẹt và khởi động.
    /// </summary>
    Task<bool> EnsureTimerStartedAsync(Guid sessionId, CancellationToken ct = default);
}
