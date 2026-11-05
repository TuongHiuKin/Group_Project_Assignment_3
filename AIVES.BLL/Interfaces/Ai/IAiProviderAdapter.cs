using AIVES.BLL.Models.Ai;

namespace AIVES.BLL.Interfaces.Ai;

/// <summary>
/// Pluggable adapter interface for an AI service provider (OpenAI, Gemini, OpenAI-compatible, etc.).
/// </summary>
public interface IAiProviderAdapter
{
    /// <summary>
    /// Identifier kind of this adapter (matches AiProviderKinds constant).
    /// </summary>
    string Kind { get; }

    /// <summary>
    /// Capabilities natively supported by this adapter.
    /// </summary>
    AiCapability SupportedCapabilities { get; }

    /// <summary>
    /// Gets the default model name for a specific capability.
    /// </summary>
    string GetDefaultModel(AiCapability capability);

    /// <summary>
    /// Creates a configured Speech-to-Text service for this provider.
    /// </summary>
    ISpeechToTextService? CreateSpeechToTextService(
        IServiceProvider sp,
        AiProviderProfile profile,
        string model);

    /// <summary>
    /// Creates a configured Text-to-Speech service for this provider.
    /// </summary>
    ITextToSpeechService? CreateTextToSpeechService(
        IServiceProvider sp,
        AiProviderProfile profile,
        string model,
        string? voice,
        string? cacheDirectory);

    /// <summary>
    /// Creates a configured Follow-up LLM generator service for this provider.
    /// </summary>
    IFollowUpGeneratorService? CreateFollowUpGeneratorService(
        IServiceProvider sp,
        AiProviderProfile profile,
        string model);

    /// <summary>
    /// Creates a configured Answer Grading service for this provider.
    /// </summary>
    IAnswerGradingService? CreateAnswerGradingService(
        IServiceProvider sp,
        AiProviderProfile profile,
        string model) => null;

    /// <summary>
    /// Tests network connectivity and API key validity for the given profile.
    /// </summary>
    Task<(bool Ok, string Message)> TestConnectionAsync(
        HttpClient httpClient,
        AiProviderProfile profile,
        CancellationToken ct = default);

    /// <summary>
    /// Queries the provider for a list of available models.
    /// </summary>
    Task<IReadOnlyList<string>> ListModelsAsync(
        HttpClient httpClient,
        AiProviderProfile profile,
        CancellationToken ct = default);
}
