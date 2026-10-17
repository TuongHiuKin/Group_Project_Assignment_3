using AIVES.BLL.Interfaces.Ai;
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
            var status = store.GetStatus();
            if (status.HasApiKey)
            {
                copy.OpenAI.ApiKey = store.GetApiKey();
                copy.UseMock = !status.UseRealAi;
            }
        }

        return copy;
    }
}
