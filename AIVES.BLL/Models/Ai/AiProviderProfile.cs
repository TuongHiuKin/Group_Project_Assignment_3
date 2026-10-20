namespace AIVES.BLL.Models.Ai;

/// <summary>
/// Represents a configured AI provider profile (e.g. OpenAI official, Gemini, local Ollama).
/// </summary>
public sealed class AiProviderProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = AiProviderKinds.OpenAi;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ApiKey { get; set; }
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Explicit capabilities allowed or supported for this profile.
    /// Primarily used for OpenAICompatible profiles which might only provide LLM.
    /// </summary>
    public AiCapability CustomCapabilities { get; set; } = AiCapability.All;

    /// <summary>
    /// Returns a masked display hint for the API key (e.g. "...abcd").
    /// </summary>
    public string? GetKeyHint()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return null;
        return ApiKey.Length >= 4 ? "…" + ApiKey[^4..] : "…" + ApiKey;
    }
}
