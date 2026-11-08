using AIVES.BLL.Models.Ai;
using AIVES.BLL.Services.Ai;
using Xunit;

namespace AIVES.Tests.Ai;

/// <summary>
/// The OpenAI-compatible adapter serves DeepSeek, Groq and local runtimes, whose model names and
/// capability sets differ from OpenAI. These pin the vendor-aware defaults used by /Admin/AiSettings.
/// </summary>
public sealed class AiProviderModelDefaultsTests
{
    private const string DeepSeekUrl = "https://api.deepseek.com/v1";
    private const string GroqUrl = "https://api.groq.com/openai/v1";

    [Fact]
    public void DeepSeek_is_detected_from_its_base_url()
    {
        Assert.True(AiProviderModelDefaults.IsDeepSeek(AiProviderKinds.OpenAiCompatible, DeepSeekUrl));
        Assert.True(AiProviderModelDefaults.IsDeepSeek(AiProviderKinds.OpenAiCompatible, "https://api.deepseek.com"));
        Assert.False(AiProviderModelDefaults.IsDeepSeek(AiProviderKinds.OpenAiCompatible, GroqUrl));
        Assert.False(AiProviderModelDefaults.IsDeepSeek(AiProviderKinds.OpenAiCompatible, null));
    }

    [Fact]
    public void DeepSeek_defaults_to_deepseek_chat_for_llm_and_grading()
    {
        Assert.Equal("deepseek-chat",
            AiProviderModelDefaults.Resolve(AiProviderKinds.OpenAiCompatible, DeepSeekUrl, AiCapability.FollowUpLlm, "gpt-4o-mini"));
        Assert.Equal("deepseek-chat",
            AiProviderModelDefaults.Resolve(AiProviderKinds.OpenAiCompatible, DeepSeekUrl, AiCapability.Grading, "gpt-4o-mini"));
    }

    [Fact]
    public void DeepSeek_declares_no_audio_capabilities()
    {
        var capabilities = AiProviderModelDefaults.SupportedCapabilities(AiProviderKinds.OpenAiCompatible, DeepSeekUrl);

        Assert.True(capabilities.HasFlag(AiCapability.FollowUpLlm));
        Assert.True(capabilities.HasFlag(AiCapability.Grading));
        Assert.False(capabilities.HasFlag(AiCapability.SpeechToText));
        Assert.False(capabilities.HasFlag(AiCapability.TextToSpeech));
    }

    [Fact]
    public void Groq_keeps_stt_but_not_tts()
    {
        var capabilities = AiProviderModelDefaults.SupportedCapabilities(AiProviderKinds.OpenAiCompatible, GroqUrl);

        Assert.True(capabilities.HasFlag(AiCapability.SpeechToText));
        Assert.False(capabilities.HasFlag(AiCapability.TextToSpeech));
        Assert.Equal("whisper-large-v3",
            AiProviderModelDefaults.Resolve(AiProviderKinds.OpenAiCompatible, GroqUrl, AiCapability.SpeechToText, "whisper-1"));
    }

    [Fact]
    public void Unknown_compatible_endpoint_falls_back_to_the_adapter_default()
    {
        Assert.Equal("gpt-4o-mini",
            AiProviderModelDefaults.Resolve(AiProviderKinds.OpenAiCompatible, "https://unknown.example/v1", AiCapability.Grading, "gpt-4o-mini"));
        Assert.Equal("whisper-1",
            AiProviderModelDefaults.Resolve(AiProviderKinds.OpenAiCompatible, "https://unknown.example/v1", AiCapability.SpeechToText, "whisper-1"));
    }

    [Fact]
    public void OpenAI_and_Gemini_keep_all_capabilities()
    {
        Assert.Equal(AiCapability.All, AiProviderModelDefaults.SupportedCapabilities(AiProviderKinds.OpenAi, "https://api.openai.com/v1"));
        Assert.Equal(AiCapability.All, AiProviderModelDefaults.SupportedCapabilities(AiProviderKinds.Gemini, "https://generativelanguage.googleapis.com/v1beta"));
    }

    [Fact]
    public void Local_runtime_is_recognised()
    {
        Assert.True(AiProviderModelDefaults.IsLocalRuntime("http://localhost:11434/v1"));
        Assert.True(AiProviderModelDefaults.IsLocalRuntime("http://192.168.1.10:8000/v1"));
        Assert.False(AiProviderModelDefaults.IsLocalRuntime(DeepSeekUrl));
    }
}
