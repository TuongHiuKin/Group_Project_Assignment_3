using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Mocks;
using AIVES.BLL.Services.VivaInterview;

namespace AIVES.BLL;

public static class DependencyInjection
{
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
    /// Registers AI services according to AiServiceOptions configuration (Whisper STT, OpenAI TTS, or Mock fallback).
    /// </summary>
    public static IServiceCollection AddAiServices(this IServiceCollection services)
    {
        services.AddSingleton<IAiCredentialStore, ProtectedAiCredentialStore>();
        services.AddHttpClient<IAiConnectionTester, OpenAiConnectionTester>();

        services.AddHttpClient<WhisperSpeechToTextService>();
        services.AddHttpClient<OpenAiTextToSpeechService>();
        services.AddHttpClient<OpenAiFollowUpGeneratorService>();
        services.AddSingleton<FollowUpPolicyValidator>();
        services.AddScoped<MockSpeechToTextService>();
        services.AddScoped<MockTextToSpeechService>();
        services.AddScoped<MockFollowUpGeneratorService>();

        services.AddScoped<ISpeechToTextService>(sp =>
        {
            var configured = sp.GetService<IOptionsMonitor<AiServiceOptions>>()?.CurrentValue
                ?? sp.GetService<IOptions<AiServiceOptions>>()?.Value
                ?? new AiServiceOptions();
            var options = AiSettingsResolver.Resolve(configured, sp.GetService<IAiCredentialStore>());
            var mock = sp.GetRequiredService<MockSpeechToTextService>();

            if (!options.UsesRealProviders)
            {
                return mock;
            }

            var resolved = Microsoft.Extensions.Options.Options.Create(options);
            return ActivatorUtilities.CreateInstance<WhisperSpeechToTextService>(sp, resolved, mock);
        });

        services.AddScoped<ITextToSpeechService>(sp =>
        {
            var configured = sp.GetService<IOptionsMonitor<AiServiceOptions>>()?.CurrentValue
                ?? sp.GetService<IOptions<AiServiceOptions>>()?.Value
                ?? new AiServiceOptions();
            var options = AiSettingsResolver.Resolve(configured, sp.GetService<IAiCredentialStore>());
            var mock = sp.GetRequiredService<MockTextToSpeechService>();

            if (!options.UsesRealProviders)
            {
                return mock;
            }

            var resolved = Microsoft.Extensions.Options.Options.Create(options);
            return string.IsNullOrWhiteSpace(options.TtsCacheDirectory)
                ? ActivatorUtilities.CreateInstance<OpenAiTextToSpeechService>(sp, resolved, mock)
                : ActivatorUtilities.CreateInstance<OpenAiTextToSpeechService>(sp, resolved, mock, options.TtsCacheDirectory);
        });

        services.AddScoped<IFollowUpGeneratorService>(sp =>
        {
            var configured = sp.GetService<IOptionsMonitor<AiServiceOptions>>()?.CurrentValue
                ?? sp.GetService<IOptions<AiServiceOptions>>()?.Value
                ?? new AiServiceOptions();
            var options = AiSettingsResolver.Resolve(configured, sp.GetService<IAiCredentialStore>());
            var mock = sp.GetRequiredService<MockFollowUpGeneratorService>();

            if (!options.UsesRealProviders)
            {
                return mock;
            }

            var resolved = Microsoft.Extensions.Options.Options.Create(options);
            return ActivatorUtilities.CreateInstance<OpenAiFollowUpGeneratorService>(sp, resolved, mock);
        });

        return services;
    }
}
