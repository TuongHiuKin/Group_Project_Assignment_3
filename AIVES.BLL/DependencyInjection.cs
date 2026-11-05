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
using AIVES.BLL.Services.Ai.Unavailable;
using AIVES.BLL.Services.VivaInterview;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Services.ExamReporting;
using AIVES.BLL.Services.Grading;

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
    /// <summary>
    /// Registers BLL services for Group 4 AI-assisted Viva Exam Grading.
    /// </summary>
    public static IServiceCollection AddVivaGradingServices(this IServiceCollection services)
    {
        services.TryAddScoped<IRubricGradingService, RubricGradingService>();
        services.TryAddScoped<IAnswerGradingService, MockAnswerGradingService>();
        services.AddScoped<IGradingOrchestrator, GradingOrchestrator>();
        return services;
    }

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
        services.AddScoped<IAnswerGradingService, MockAnswerGradingService>();
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
        services.AddHttpClient<OpenAiAnswerGradingService>(client => client.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient<GeminiSpeechToTextService>(client => client.Timeout = TimeSpan.FromMinutes(3));
        services.AddHttpClient<GeminiTextToSpeechService>();
        services.AddHttpClient<GeminiFollowUpGeneratorService>(client => client.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient<GeminiAnswerGradingService>(client => client.Timeout = TimeSpan.FromMinutes(2));

        services.AddSingleton<FollowUpPolicyValidator>();
        services.AddSingleton<GradingOutputValidator>();
        services.AddSingleton<ISpeechMetricsAnalyzer, SpeechMetricsAnalyzer>();
        services.AddScoped<MockSpeechToTextService>();
        services.AddScoped<MockTextToSpeechService>();
        services.AddScoped<MockFollowUpGeneratorService>();
        services.AddScoped<MockAnswerGradingService>();

        services.AddScoped<ISpeechToTextService>(sp => ResolveCapability<ISpeechToTextService>(
            sp,
            AiCapability.SpeechToText,
            config => (config.Routing.Stt.ProfileId, config.Routing.Stt.Model),
            (adapter, profile, model, _) => adapter.CreateSpeechToTextService(sp, profile, model),
            () => sp.GetRequiredService<MockSpeechToTextService>(),
            reason => new UnavailableSpeechToTextService(reason)));

        services.AddScoped<ITextToSpeechService>(sp =>
        {
            // Browser speechSynthesis reads the question on the client, so no cloud TTS is resolved.
            if (sp.GetService<IOptions<AiServiceOptions>>()?.Value.UseBrowserSpeechSynthesis == true)
            {
                return new BrowserSpeechSynthesisTextToSpeechService();
            }

            return ResolveCapability<ITextToSpeechService>(
                sp,
                AiCapability.TextToSpeech,
                config => (config.Routing.Tts.ProfileId, config.Routing.Tts.Model),
                (adapter, profile, model, config) =>
                {
                    var cacheDir = sp.GetService<IOptions<AiServiceOptions>>()?.Value.TtsCacheDirectory;
                    var voice = !string.IsNullOrWhiteSpace(config.Routing.Tts.Voice)
                        ? config.Routing.Tts.Voice
                        : (string.Equals(profile.Kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase) ? "Puck" : "alloy");
                    return adapter.CreateTextToSpeechService(sp, profile, model, voice, cacheDir);
                },
                () => sp.GetRequiredService<MockTextToSpeechService>(),
                reason => new UnavailableTextToSpeechService(reason));
        });

        services.AddScoped<IFollowUpGeneratorService>(sp => ResolveCapability<IFollowUpGeneratorService>(
            sp,
            AiCapability.FollowUpLlm,
            config => (config.Routing.Llm.ProfileId, config.Routing.Llm.Model),
            (adapter, profile, model, _) => adapter.CreateFollowUpGeneratorService(sp, profile, model),
            () => sp.GetRequiredService<MockFollowUpGeneratorService>(),
            reason => new UnavailableFollowUpGeneratorService(reason)));

        services.AddScoped<IAnswerGradingService>(sp => ResolveCapability<IAnswerGradingService>(
            sp,
            AiCapability.Grading,
            config => (config.Routing.Grading.ProfileId, config.Routing.Grading.Model),
            (adapter, profile, model, _) => adapter.CreateAnswerGradingService(sp, profile, model),
            () => sp.GetRequiredService<MockAnswerGradingService>(),
            reason => new UnavailableAnswerGradingService(reason)));

        return services;
    }

    /// <summary>
    /// Resolves the real service for one capability from the routing table. When it cannot be resolved,
    /// returns the mock only if mock mode is explicit (<c>UseMock</c>) or <c>AllowMockFallback</c> is on;
    /// otherwise returns an "unavailable" service whose calls fail with a Vietnamese reason instead of fake data.
    /// </summary>
    private static TService ResolveCapability<TService>(
        IServiceProvider sp,
        AiCapability capability,
        Func<AiConfigurationModel, (string? ProfileId, string? Model)> selectTarget,
        Func<IAiProviderAdapter, AiProviderProfile, string, AiConfigurationModel, TService?> create,
        Func<TService> createMock,
        Func<string, TService> createUnavailable)
        where TService : class
    {
        var logger = sp.GetService<ILoggerFactory>()?.CreateLogger(typeof(DependencyInjection).FullName!);
        var options = sp.GetService<IOptions<AiServiceOptions>>()?.Value;
        var mockAllowed = options == null || options.UseMock || options.AllowMockFallback;

        TService Fallback(string reason)
        {
            if (mockAllowed)
            {
                logger?.LogWarning("{Capability}: {Reason}. Using mock service (UseMock/AllowMockFallback enabled).", capability, reason);
                return createMock();
            }

            logger?.LogWarning("{Capability}: {Reason}. Mock fallback is disabled; returning unavailable service.", capability, reason);
            return createUnavailable(reason);
        }

        var store = sp.GetService<IAiCredentialStore>();
        if (store == null) return Fallback(AiUnavailableMessages.RealAiDisabled);

        var config = store.GetConfiguration();
        if (!config.UseRealAi) return Fallback(AiUnavailableMessages.RealAiDisabled);

        var (profileId, targetModel) = selectTarget(config);
        var profile = config.FindProfile(profileId);
        if (profile == null) return Fallback(AiUnavailableMessages.NoProfileSelected);

        var adapter = sp.GetRequiredService<IAiProviderRegistry>().GetAdapter(profile.Kind);
        if (adapter == null) return Fallback(AiUnavailableMessages.ProviderNotSupported);

        try
        {
            var model = !string.IsNullOrWhiteSpace(targetModel) ? targetModel : adapter.GetDefaultModel(capability);
            return create(adapter, profile, model, config) ?? Fallback(AiUnavailableMessages.CapabilityNotEnabled);
        }
        catch (Exception ex)
        {
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            logger?.LogWarning("Failed to create {Capability} service for provider {ProfileName} ({Kind}). Error: {Error}",
                capability, profile.Name, profile.Kind, sanitized);
            return Fallback(AiUnavailableMessages.CreationFailed);
        }
    }
}
