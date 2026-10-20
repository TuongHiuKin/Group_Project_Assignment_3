using AIVES.BLL.Models.Ai;

namespace AIVES.BLL.Interfaces.Ai;

public interface IAiConnectionTester
{
    Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default);
    Task<(bool Ok, string Message)> TestProfileAsync(AiProviderProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListModelsAsync(AiProviderProfile profile, CancellationToken ct = default);
}
