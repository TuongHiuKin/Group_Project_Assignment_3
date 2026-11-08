using AIVES.BLL.Models.Ai;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Resolves the default model name for a provider. <see cref="AiProviderKinds.OpenAiCompatible"/> covers
/// many vendors (DeepSeek, Groq, Together, Ollama, LM Studio) whose model names differ, so the base URL
/// is used to pick a vendor-appropriate default instead of always returning an OpenAI model name.
/// </summary>
public static class AiProviderModelDefaults
{
    public const string DeepSeekChat = "deepseek-chat";
    public const string DeepSeekReasoner = "deepseek-reasoner";

    /// <summary>Fixed profile id so the dedicated DeepSeek card can find/reuse it across sessions.</summary>
    public const string DeepSeekProfileId = "deepseek";

    public const string DeepSeekBaseUrl = "https://api.deepseek.com/v1";

    /// <summary>DeepSeek exposes no STT or TTS endpoint; only chat/completions.</summary>
    public static bool IsDeepSeek(string? kind, string? baseUrl) =>
        Contains(baseUrl, "deepseek") ||
        (string.Equals(kind, AiProviderKinds.OpenAiCompatible, StringComparison.OrdinalIgnoreCase) &&
         Contains(baseUrl, "deepseek"));

    /// <summary>Groq does expose Whisper STT but no TTS.</summary>
    public static bool IsGroq(string? baseUrl) => Contains(baseUrl, "groq");

    /// <summary>Local runtimes (Ollama, LM Studio, vLLM) usually have no cloud routing by default.</summary>
    public static bool IsLocalRuntime(string? baseUrl) => NetworkEndpointHelper.IsLocalOrLan(baseUrl);

    /// <summary>
    /// Capabilities a provider can actually serve. Used to pre-tick the right checkboxes in /Admin/AiSettings
    /// so a DeepSeek profile never gets routed for STT/TTS and fails at runtime.
    /// </summary>
    public static AiCapability SupportedCapabilities(string? kind, string? baseUrl)
    {
        if (IsDeepSeek(kind, baseUrl))
        {
            return AiCapability.FollowUpLlm | AiCapability.Grading;
        }

        if (IsGroq(baseUrl))
        {
            return AiCapability.SpeechToText | AiCapability.FollowUpLlm | AiCapability.Grading;
        }

        if (string.Equals(kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return AiCapability.All;
        }

        if (string.Equals(kind, AiProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase))
        {
            return AiCapability.All;
        }

        // Generic OpenAI-compatible endpoint: assume LLM + grading (the common case) and let the admin opt in.
        return AiCapability.FollowUpLlm | AiCapability.Grading;
    }

    /// <summary>
    /// Default model for a capability. Returns <paramref name="fallback"/> (normally the adapter default)
    /// when the base URL does not identify a known vendor.
    /// </summary>
    public static string Resolve(string? kind, string? baseUrl, AiCapability capability, string fallback)
    {
        if (IsDeepSeek(kind, baseUrl))
        {
            return capability is AiCapability.FollowUpLlm or AiCapability.Grading
                ? DeepSeekChat
                : fallback;
        }

        if (IsGroq(baseUrl))
        {
            return capability switch
            {
                AiCapability.SpeechToText => "whisper-large-v3",
                AiCapability.FollowUpLlm => "llama-3.3-70b-versatile",
                AiCapability.Grading => "llama-3.3-70b-versatile",
                _ => fallback
            };
        }

        return fallback;
    }

    private static bool Contains(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(token, StringComparison.OrdinalIgnoreCase);
}
