using Microsoft.Extensions.DependencyInjection;
using AIVES.BLL.Interfaces.VivaInterview;
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
        services.AddScoped<AIVES.BLL.Interfaces.Ai.ISpeechToTextService, AIVES.BLL.Services.Ai.Mocks.MockSpeechToTextService>();
        services.AddScoped<AIVES.BLL.Interfaces.Ai.ITextToSpeechService, AIVES.BLL.Services.Ai.Mocks.MockTextToSpeechService>();
        services.AddScoped<AIVES.BLL.Interfaces.Ai.IFollowUpGeneratorService, AIVES.BLL.Services.Ai.Mocks.MockFollowUpGeneratorService>();
        return services;
    }

    /// <summary>
    /// Registers AI services according to AiServiceOptions configuration (Whisper STT or Mock fallback).
    /// </summary>
    public static IServiceCollection AddAiServices(this IServiceCollection services)
    {
        services.AddHttpClient<AIVES.BLL.Services.Ai.WhisperSpeechToTextService>();
        services.AddScoped<AIVES.BLL.Services.Ai.Mocks.MockSpeechToTextService>();
        services.AddScoped<AIVES.BLL.Services.Ai.Mocks.MockTextToSpeechService>();
        services.AddScoped<AIVES.BLL.Services.Ai.Mocks.MockFollowUpGeneratorService>();

        services.AddScoped<AIVES.BLL.Interfaces.Ai.ISpeechToTextService>(sp =>
        {
            var options = sp.GetService<Microsoft.Extensions.Options.IOptions<AIVES.BLL.Options.AiServiceOptions>>()?.Value
                ?? new AIVES.BLL.Options.AiServiceOptions();
            var mock = sp.GetRequiredService<AIVES.BLL.Services.Ai.Mocks.MockSpeechToTextService>();

            if (options.UseMock || string.IsNullOrWhiteSpace(options.OpenAI.ApiKey))
            {
                return mock;
            }

            return ActivatorUtilities.CreateInstance<AIVES.BLL.Services.Ai.WhisperSpeechToTextService>(sp, mock);
        });

        services.AddScoped<AIVES.BLL.Interfaces.Ai.ITextToSpeechService, AIVES.BLL.Services.Ai.Mocks.MockTextToSpeechService>();
        services.AddScoped<AIVES.BLL.Interfaces.Ai.IFollowUpGeneratorService, AIVES.BLL.Services.Ai.Mocks.MockFollowUpGeneratorService>();

        return services;
    }
}
