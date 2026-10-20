using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Adapters;
using AIVES.BLL.Services.Ai.Gemini;
using AIVES.BLL.Services.Ai.Mocks;
using AIVES.BLL.Services.VivaInterview;

namespace AIVES.BLL;

public static class DependencyInjection
{
    /// <summary>
    /// Registers credential storage, provider adapters, registry, and connection testing for AI settings management.
    /// Available regardless of whether the viva interview engine is actively enabled.
    /// </summary>
    public static IServiceCollection AddAiCredentialManagement(this IServiceCollection services)
    {
        services.TryAddSingleton<IAiProviderAdapter, OpenAiProviderAdapter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAiProviderAdapter, OpenAiCompatibleProviderAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAiProviderAdapter, GeminiProviderAdapter>());

        services.TryAddSingleton<IAiProviderRegistry, AiProviderRegistry>();
        services.TryAddSingleton<IAiCredentialStore, ProtectedAiCredentialStore>();
        services.AddHttpClient<IAiConnectionTester, OpenAiConnectionTester>();
        return services;
    }

    /// <summary>
    /// Registers BLL services for Group 3 AI Viva Interview Core.
    /// </summary>
    public static IServiceCollection AddVivaInterviewServices(this IServiceCollection services)
    {
        services.AddScoped<IVivaSessionOrchestrator, VivaSessionOrchestrator>();
        return services;
    }

    /// <summary>
    /// Registers deterministic mock AI services for offline development, CI/CD, and testing.
    /// </summary>
    public static IServiceCollection AddMockAiServices(this IServiceCollection services)
    {
        services.AddScoped<ISpeechToTextService, MockSpeechToTextService>();
        services.AddScoped<ITextToSpeechService, MockTextToSpeechService>();
        services.AddScoped<IFollowUpGeneratorService, MockFollowUpGeneratorService>();
        return services;
    }

    /// <summary>
    /// Registers AI services according to pluggable capability routing (OpenAI, Gemini, OpenAI-compatible, or Mock fallback).
    /// </summary>
    public static IServiceCollection AddAiServices(this IServiceCollection services)
    {
        services.AddAiCredentialManagement();

        services.AddHttpClient<WhisperSpeechToTextService>();
        services.AddHttpClient<OpenAiTextToSpeechService>();
        services.AddHttpClient<OpenAiFollowUpGeneratorService>();
        services.AddHttpClient<GeminiSpeechToTextService>();
        services.AddHttpClient<GeminiTextToSpeechService>();
        services.AddHttpClient<GeminiFollowUpGeneratorService>();

        services.AddSingleton<FollowUpPolicyValidator>();
        services.AddScoped<MockSpeechToTextService>();
        services.AddScoped<MockTextToSpeechService>();
        services.AddScoped<MockFollowUpGeneratorService>();

        services.AddScoped<ISpeechToTextService>(sp =>
        {
            var mock = sp.GetRequiredService<MockSpeechToTextService>();
            var logger = sp.GetService<ILogger<ISpeechToTextService>>();
            var store = sp.GetService<IAiCredentialStore>();
            if (store == null) return mock;

            var config = store.GetConfiguration();
            if (!config.UseRealAi) return mock;

            var target = config.Routing.Stt;
            var profile = config.FindProfile(target.ProfileId);
            if (profile == null) return mock;

            var registry = sp.GetRequiredService<IAiProviderRegistry>();
            var adapter = registry.GetAdapter(profile.Kind);
            if (adapter == null) return mock;

            try
            {
                var model = !string.IsNullOrWhiteSpace(target.Model)
                    ? target.Model
                    : adapter.GetDefaultModel(AiCapability.SpeechToText);
                var service = adapter.CreateSpeechToTextService(sp, profile, model);
                return service ?? mock;
            }
            catch (Exception ex)
            {
                var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
                logger?.LogWarning("Failed to create SpeechToTextService for provider {ProfileName} ({Kind}). Falling back to mock. Error: {Error}",
                    profile.Name, profile.Kind, sanitized);
                return mock;
            }
        });

        services.AddScoped<ITextToSpeechService>(sp =>
        {
            var mock = sp.GetRequiredService<MockTextToSpeechService>();
            var logger = sp.GetService<ILogger<ITextToSpeechService>>();
            var store = sp.GetService<IAiCredentialStore>();
            if (store == null) return mock;

            var config = store.GetConfiguration();
            if (!config.UseRealAi) return mock;

            var target = config.Routing.Tts;
            var profile = config.FindProfile(target.ProfileId);
            if (profile == null) return mock;

            var registry = sp.GetRequiredService<IAiProviderRegistry>();
            var adapter = registry.GetAdapter(profile.Kind);
            if (adapter == null) return mock;

            var options = sp.GetService<IOptions<AiServiceOptions>>()?.Value;
            var cacheDir = options?.TtsCacheDirectory;

            try
            {
                var model = !string.IsNullOrWhiteSpace(target.Model)
                    ? target.Model
                    : adapter.GetDefaultModel(AiCapability.TextToSpeech);
                var voice = !string.IsNullOrWhiteSpace(target.Voice)
                    ? target.Voice
                    : (string.Equals(profile.Kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase) ? "Puck" : "alloy");

                var service = adapter.CreateTextToSpeechService(sp, profile, model, voice, cacheDir);
                return service ?? mock;
            }
            catch (Exception ex)
            {
                var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
                logger?.LogWarning("Failed to create TextToSpeechService for provider {ProfileName} ({Kind}). Falling back to mock. Error: {Error}",
                    profile.Name, profile.Kind, sanitized);
                return mock;
            }
        });

        services.AddScoped<IFollowUpGeneratorService>(sp =>
        {
            var mock = sp.GetRequiredService<MockFollowUpGeneratorService>();
            var logger = sp.GetService<ILogger<IFollowUpGeneratorService>>();
            var store = sp.GetService<IAiCredentialStore>();
            if (store == null) return mock;

            var config = store.GetConfiguration();
            if (!config.UseRealAi) return mock;

            var target = config.Routing.Llm;
            var profile = config.FindProfile(target.ProfileId);
            if (profile == null) return mock;

            var registry = sp.GetRequiredService<IAiProviderRegistry>();
            var adapter = registry.GetAdapter(profile.Kind);
            if (adapter == null) return mock;

            try
            {
                var model = !string.IsNullOrWhiteSpace(target.Model)
                    ? target.Model
                    : adapter.GetDefaultModel(AiCapability.FollowUpLlm);
                var service = adapter.CreateFollowUpGeneratorService(sp, profile, model);
                return service ?? mock;
            }
            catch (Exception ex)
            {
                var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
                logger?.LogWarning("Failed to create FollowUpGeneratorService for provider {ProfileName} ({Kind}). Falling back to mock. Error: {Error}",
                    profile.Name, profile.Kind, sanitized);
                return mock;
            }
        });

        return services;
    }
}
