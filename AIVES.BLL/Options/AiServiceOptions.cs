namespace AIVES.BLL.Options;

/// <summary>
/// Configuration options for AI services (STT, TTS, LLM Follow-up).
/// Configured under section "AiServices" in appsettings.json.
/// </summary>
public class AiServiceOptions
{
    public const string SectionName = "AiServices";

    /// <summary>
    /// When true, deterministic offline mock services are used instead of real cloud APIs.
    /// Default is true for local development and CI/CD testing.
    /// </summary>
    public bool UseMock { get; set; } = true;

    /// <summary>
    /// When true, real AI adapters may fall back to deterministic mock services if a cloud API call fails.
    /// Default is false in production/real AI mode so errors are surfaced clearly instead of returning fake transcripts.
    /// </summary>
    public bool AllowMockFallback { get; set; } = false;

    /// <summary>
    /// STT operation timeout in seconds (default: 15s).
    /// </summary>
    public int SpeechToTextTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// TTS operation timeout in seconds (default: 10s).
    /// </summary>
    public int TextToSpeechTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// LLM follow-up generation timeout in seconds (default: 15s).
    /// </summary>
    public int FollowUpTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Settings for OpenAI / Whisper / GPT-4o.
    /// </summary>
    public OpenAiOptions OpenAI { get; set; } = new();

    /// <summary>
    /// Settings for Azure Cognitive Speech (STT/TTS).
    /// </summary>
    public AzureSpeechOptions AzureSpeech { get; set; } = new();

    /// <summary>Thư mục lưu mp3 TTS (wwwroot/audio/cache). Presentation layer gán khi khởi động.</summary>
    public string? TtsCacheDirectory { get; set; }

    /// <summary>Đường dẫn file lưu API key đã mã hoá (App_Data/ai-credentials.json). Presentation layer gán khi khởi động.</summary>
    public string? CredentialStorePath { get; set; }

    /// <summary>True khi dùng provider thật: UseMock = false và đã có OpenAI API key.</summary>
    public bool UsesRealProviders => !UseMock && !string.IsNullOrWhiteSpace(OpenAI.ApiKey);
}

public class OpenAiOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "gpt-4o-mini";
    public string WhisperModel { get; set; } = "whisper-1";
    public string TtsModel { get; set; } = "tts-1";
    public string TtsVoice { get; set; } = "alloy";
    public string? BaseUrl { get; set; }
    public string? OrganizationId { get; set; }

    /// <summary>Mã ngôn ngữ ISO-639-1 gửi cho Whisper. Để trống thì Whisper tự nhận diện.</summary>
    public string? TranscriptionLanguage { get; set; } = "vi";
}

public class AzureSpeechOptions
{
    public string? SubscriptionKey { get; set; }
    public string Region { get; set; } = "southeastasia";
    public string VoiceName { get; set; } = "vi-VN-HoaiMyNeural";
}
