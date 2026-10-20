using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;

namespace AIVES.BLL.Services.Ai;

public static class AiSettingsResolver
{
    public static AiServiceOptions Resolve(AiServiceOptions configured, IAiCredentialStore? store)
    {
        ArgumentNullException.ThrowIfNull(configured);

        var copy = new AiServiceOptions
        {
            UseMock = configured.UseMock,
            AllowMockFallback = configured.AllowMockFallback,
            SpeechToTextTimeoutSeconds = configured.SpeechToTextTimeoutSeconds,
            TextToSpeechTimeoutSeconds = configured.TextToSpeechTimeoutSeconds,
            FollowUpTimeoutSeconds = configured.FollowUpTimeoutSeconds,
            TtsCacheDirectory = configured.TtsCacheDirectory,
            CredentialStorePath = configured.CredentialStorePath,
            OpenAI = new OpenAiOptions
            {
                ApiKey = configured.OpenAI.ApiKey,
                Model = configured.OpenAI.Model,
                WhisperModel = configured.OpenAI.WhisperModel,
                TtsModel = configured.OpenAI.TtsModel,
                TtsVoice = configured.OpenAI.TtsVoice,
                BaseUrl = configured.OpenAI.BaseUrl,
                OrganizationId = configured.OpenAI.OrganizationId,
                TranscriptionLanguage = configured.OpenAI.TranscriptionLanguage
            },
            AzureSpeech = new AzureSpeechOptions
            {
                SubscriptionKey = configured.AzureSpeech.SubscriptionKey,
                Region = configured.AzureSpeech.Region,
                VoiceName = configured.AzureSpeech.VoiceName
            }
        };

        if (store != null)
        {
            var config = store.GetConfiguration();
            copy.UseMock = !config.UseRealAi;

            var primaryProfile = config.FindProfile(config.Routing.Llm.ProfileId)
                ?? config.Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ApiKey));

            if (primaryProfile != null)
            {
                copy.OpenAI.ApiKey = primaryProfile.ApiKey;
                if (!string.IsNullOrWhiteSpace(primaryProfile.BaseUrl))
                {
                    copy.OpenAI.BaseUrl = primaryProfile.BaseUrl;
                }
            }
        }

        return copy;
    }

    public static string GetStartupSummary(AiConfigurationModel config, IAiProviderRegistry registry)
    {
        if (!config.UseRealAi)
        {
            return "Mock";
        }

        var sttProfile = config.FindProfile(config.Routing.Stt.ProfileId);
        var ttsProfile = config.FindProfile(config.Routing.Tts.ProfileId);
        var llmProfile = config.FindProfile(config.Routing.Llm.ProfileId);

        var sttDesc = FormatDesc(sttProfile, config.Routing.Stt.Model, AiCapability.SpeechToText, registry);
        var ttsDesc = FormatDesc(ttsProfile, config.Routing.Tts.Model, AiCapability.TextToSpeech, registry);
        var llmDesc = FormatDesc(llmProfile, config.Routing.Llm.Model, AiCapability.FollowUpLlm, registry);

        return $"Real [STT={sttDesc}, TTS={ttsDesc}, LLM={llmDesc}]";
    }

    private static string FormatDesc(AiProviderProfile? profile, string? model, AiCapability cap, IAiProviderRegistry registry)
    {
        if (profile == null) return "Mock";
        var adapter = registry.GetAdapter(profile.Kind);
        if (adapter == null || !profile.CustomCapabilities.HasFlag(cap)) return "Mock";

        var name = string.IsNullOrWhiteSpace(profile.Name) ? profile.Kind : profile.Name;
        var modelStr = string.IsNullOrWhiteSpace(model) ? adapter.GetDefaultModel(cap) : model;
        return $"{name}/{modelStr}";
    }
}
