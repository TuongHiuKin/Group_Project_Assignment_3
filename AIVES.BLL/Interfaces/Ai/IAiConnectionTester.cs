using AIVES.BLL.Models.Ai;

namespace AIVES.BLL.Interfaces.Ai;

public interface IAiConnectionTester
{
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> TestProfileAsync(AiProviderProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListModelsAsync(AiProviderProfile profile, CancellationToken ct = default);

    /// <summary>
    /// Danh sách model thật của nhà cung cấp, hoặc null khi không lấy được hoặc nhà cung cấp không hỗ trợ.
    /// Dùng để kiểm tra tên model khi lưu định tuyến; khác <see cref="ListModelsAsync"/> ở chỗ không trả danh sách dự phòng.
    /// </summary>
    Task<IReadOnlyList<string>?> TryListLiveModelsAsync(AiProviderProfile profile, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
}
