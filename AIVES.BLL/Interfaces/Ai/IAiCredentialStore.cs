namespace AIVES.BLL.Interfaces.Ai;

public interface IAiCredentialStore
{
    AiCredentialStatus GetStatus();
    string? GetApiKey();
    Task SaveAsync(string? newApiKey, bool useRealAi, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}

public sealed record AiCredentialStatus(bool HasApiKey, bool UseRealAi, string? KeyHint, DateTime? UpdatedAtUtc);
