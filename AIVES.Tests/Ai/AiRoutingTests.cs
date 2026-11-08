using System.Net;
using AIVES.BLL.Services.Ai.Adapters;
using AIVES.BLL;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Gemini;
using AIVES.BLL.Services.Ai.Mocks;
using AIVES.BLL.Services.Ai.Unavailable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class AiRoutingTests
{
    private sealed class MockCredentialStore(AiConfigurationModel config) : IAiCredentialStore
    {
        public AiCredentialStatus GetStatus() => new(true, config.UseRealAi, "...test", DateTime.UtcNow);
        public string? GetApiKey() => config.Profiles.FirstOrDefault()?.ApiKey;
        public Task SaveAsync(string? newApiKey, bool useRealAi, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public AiConfigurationModel GetConfiguration() => config;
        public Task SaveConfigurationAsync(AiConfigurationModel updated, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void Routing_Can_Use_Three_Different_Providers_Simultaneously()
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile { Id = "p-gemini", Name = "Google Gemini", Kind = AiProviderKinds.Gemini, ApiKey = "AIzaSyTest123", BaseUrl = "https://generativelanguage.googleapis.com/v1beta" },
                new AiProviderProfile { Id = "p-openai", Name = "OpenAI", Kind = AiProviderKinds.OpenAi, ApiKey = "sk-test123", BaseUrl = "https://api.openai.com/v1" },
                new AiProviderProfile { Id = "p-groq", Name = "Groq", Kind = AiProviderKinds.OpenAiCompatible, ApiKey = "gsk_test123", BaseUrl = "https://api.groq.com/openai/v1" }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p-gemini", Model = "gemini-2.5-flash" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "p-openai", Model = "tts-1", Voice = "alloy" },
                Llm = new AiCapabilityTarget { ProfileId = "p-groq", Model = "llama-3.3-70b" }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(new AiServiceOptions { UseMock = false }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(config));
        services.AddAiServices();

        var sp = services.BuildServiceProvider();

        var stt = sp.GetRequiredService<ISpeechToTextService>();
        var tts = sp.GetRequiredService<ITextToSpeechService>();
        var llm = sp.GetRequiredService<IFollowUpGeneratorService>();

        // STT should be GeminiSpeechToTextService
        Assert.IsType<GeminiSpeechToTextService>(stt);

        // TTS should be OpenAiTextToSpeechService
        Assert.IsType<OpenAiTextToSpeechService>(tts);

        // LLM should be OpenAiFollowUpGeneratorService (configured for Groq)
        Assert.IsType<OpenAiFollowUpGeneratorService>(llm);
    }

    [Fact]
    public void Routing_Falls_Back_To_Mock_When_Unassigned_Or_RealAi_Disabled()
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = false, // Disabled
            Profiles =
            [
                new AiProviderProfile { Id = "p1", Name = "OpenAI", Kind = AiProviderKinds.OpenAi, ApiKey = "sk-test123" }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p1", Model = "whisper-1" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "p1", Model = "tts-1" },
                Llm = new AiCapabilityTarget { ProfileId = "p1", Model = "gpt-4o-mini" }
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(new AiServiceOptions { UseMock = true }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(config));
        services.AddAiServices();

        var sp = services.BuildServiceProvider();

        Assert.IsType<MockSpeechToTextService>(sp.GetRequiredService<ISpeechToTextService>());
        Assert.IsType<MockTextToSpeechService>(sp.GetRequiredService<ITextToSpeechService>());
        Assert.IsType<MockFollowUpGeneratorService>(sp.GetRequiredService<IFollowUpGeneratorService>());
    }

    [Fact]
    public void Startup_Summary_Formats_Real_And_Mock_Correctly()
    {
        var registry = new AiProviderRegistry([
            new OpenAiProviderAdapter(),
            new GeminiProviderAdapter(),
            new OpenAiCompatibleProviderAdapter()
        ]);

        var mockConfig = new AiConfigurationModel { UseRealAi = false };
        Assert.Equal("Mock", AiSettingsResolver.GetStartupSummary(mockConfig, registry));

        var realConfig = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile { Id = "p-gem", Name = "Gemini", Kind = AiProviderKinds.Gemini },
                new AiProviderProfile { Id = "p-oai", Name = "OpenAI", Kind = AiProviderKinds.OpenAi },
                new AiProviderProfile { Id = "p-groq", Name = "Groq", Kind = AiProviderKinds.OpenAiCompatible }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p-gem", Model = "gemini-2.5-flash" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "p-oai", Model = "tts-1" },
                Llm = new AiCapabilityTarget { ProfileId = "p-groq", Model = "llama-3.3-70b" }
            }
        };

        var summary = AiSettingsResolver.GetStartupSummary(realConfig, registry);
        Assert.Contains("STT=Gemini/gemini-2.5-flash", summary);
        Assert.Contains("TTS=OpenAI/tts-1", summary);
        Assert.Contains("LLM=Groq/llama-3.3-70b", summary);
    }

    [Fact]
    public void Routing_Uses_Default_Model_When_Target_Model_Is_Null_Or_Empty()
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile { Id = "p-gem", Name = "Gemini", Kind = AiProviderKinds.Gemini, ApiKey = "AIzaSyTest" },
                new AiProviderProfile { Id = "p-oai", Name = "OpenAI", Kind = AiProviderKinds.OpenAi, ApiKey = "sk-test" }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p-gem", Model = null }, // empty -> gemini-2.5-flash
                Tts = new AiTtsCapabilityTarget { ProfileId = "p-gem", Model = "", Voice = null }, // empty -> gemini-2.5-flash-preview-tts, Puck
                Llm = new AiCapabilityTarget { ProfileId = "p-oai", Model = " " } // whitespace -> gpt-4o-mini
            }
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(new AiServiceOptions { UseMock = false }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(config));
        services.AddAiServices();

        var sp = services.BuildServiceProvider();

        var stt = sp.GetRequiredService<ISpeechToTextService>();
        var tts = sp.GetRequiredService<ITextToSpeechService>();
        var llm = sp.GetRequiredService<IFollowUpGeneratorService>();

        Assert.IsType<GeminiSpeechToTextService>(stt);
        Assert.IsType<GeminiTextToSpeechService>(tts);
        Assert.IsType<OpenAiFollowUpGeneratorService>(llm);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Routing_When_Adapter_Throws_Uses_Mock_Only_If_Fallback_Allowed(bool allowMockFallback)
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile { Id = "p-faulty", Name = "Faulty Provider", Kind = "faulty-kind", ApiKey = "sk-super-secret" }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p-faulty", Model = "m1" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "p-faulty", Model = "m2" },
                Llm = new AiCapabilityTarget { ProfileId = "p-faulty", Model = "m3" }
            }
        };

        var faultyAdapter = new FaultyThrowingAdapter();
        var registry = new AiProviderRegistry([faultyAdapter]);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(
            new AiServiceOptions { UseMock = false, AllowMockFallback = allowMockFallback }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(config));
        services.AddSingleton<IAiProviderRegistry>(registry);
        services.AddAiServices();

        var sp = services.BuildServiceProvider();

        // Resolving services should NOT throw
        var stt = sp.GetRequiredService<ISpeechToTextService>();
        var tts = sp.GetRequiredService<ITextToSpeechService>();
        var llm = sp.GetRequiredService<IFollowUpGeneratorService>();

        if (allowMockFallback)
        {
            Assert.IsType<MockSpeechToTextService>(stt);
            Assert.IsType<MockTextToSpeechService>(tts);
            Assert.IsType<MockFollowUpGeneratorService>(llm);
        }
        else
        {
            Assert.IsType<UnavailableSpeechToTextService>(stt);
            Assert.IsType<UnavailableTextToSpeechService>(tts);
            Assert.IsType<UnavailableFollowUpGeneratorService>(llm);
            Assert.DoesNotContain("sk-super-secret", ((UnavailableSpeechToTextService)stt).ErrorMessage);
        }
    }

    private static ServiceProvider BuildStrictProvider(AiConfigurationModel config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(
            new AiServiceOptions { UseMock = false, AllowMockFallback = false }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(config));
        services.AddAiServices();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Routing_StrictMode_RealAiDisabled_ReturnsUnavailableServicesWithVietnameseReason()
    {
        using var sp = BuildStrictProvider(new AiConfigurationModel { UseRealAi = false });

        var stt = await sp.GetRequiredService<ISpeechToTextService>().TranscribeAsync(new MemoryStream(new byte[] { 1 }), "audio/webm");
        var tts = await sp.GetRequiredService<ITextToSpeechService>().SynthesizeAsync("Câu hỏi");
        var llm = await sp.GetRequiredService<IFollowUpGeneratorService>().GenerateFollowUpQuestionAsync("Q", "A", "EXAM01");

        Assert.False(stt.IsSuccess);
        Assert.Empty(stt.TranscriptText);
        Assert.Equal("Nhận diện giọng nói (STT) chưa sẵn sàng: chưa bật AI thật trên trang Cấu hình AI. Vui lòng nhờ Admin kiểm tra Cấu hình AI.", stt.ErrorMessage);
        Assert.False(tts.IsSuccess);
        Assert.Null(tts.AudioUrl);
        Assert.Contains("Đọc câu hỏi (TTS) chưa sẵn sàng", tts.ErrorMessage);
        Assert.False(llm.IsSuccess);
        Assert.Contains("Câu hỏi phụ (LLM) chưa sẵn sàng", llm.ErrorMessage);
    }

    /// <summary>
    /// Regression: a routed profile with no API key used to build a real adapter anyway, so the
    /// failure surfaced deep inside the HTTP client as the English
    /// "OpenAI API key is not configured." instead of the intended Vietnamese reason.
    /// </summary>
    [Fact]
    public async Task Routing_StrictMode_ProfileWithoutApiKey_ReturnsUnavailableWithVietnameseReason()
    {
        using var sp = BuildStrictProvider(new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = "openai-nokey",
                    Name = "OpenAI (no key)",
                    Kind = AiProviderKinds.OpenAi,
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = null,
                    CustomCapabilities = AiCapability.All
                }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "openai-nokey", Model = "whisper-1" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "openai-nokey", Model = "tts-1", Voice = "alloy" },
                Llm = new AiCapabilityTarget { ProfileId = "openai-nokey", Model = "gpt-4o-mini" }
            }
        });

        var stt = Assert.IsType<UnavailableSpeechToTextService>(sp.GetRequiredService<ISpeechToTextService>());
        Assert.Contains("Nhận diện giọng nói (STT) chưa sẵn sàng", stt.ErrorMessage);
        Assert.Contains("thiếu API key", stt.ErrorMessage);
        Assert.DoesNotContain("OpenAI API key is not configured", stt.ErrorMessage);

        var tts = Assert.IsType<UnavailableTextToSpeechService>(sp.GetRequiredService<ITextToSpeechService>());
        Assert.Contains("Đọc câu hỏi (TTS) chưa sẵn sàng", tts.ErrorMessage);

        var llm = Assert.IsType<UnavailableFollowUpGeneratorService>(sp.GetRequiredService<IFollowUpGeneratorService>());
        Assert.Contains("Câu hỏi phụ (LLM) chưa sẵn sàng", llm.ErrorMessage);

        var result = await stt.TranscribeAsync(new MemoryStream(new byte[] { 1 }), "audio/webm");
        Assert.False(result.IsSuccess);
        Assert.DoesNotContain("OpenAI API key is not configured", result.ErrorMessage);
    }

    /// <summary>A keyless local/LAN endpoint (Ollama, LM Studio) must still resolve a real service.</summary>
    [Fact]
    public void Routing_KeylessLocalEndpoint_StillResolvesRealService()
    {
        using var sp = BuildStrictProvider(new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = "ollama",
                    Name = "Ollama",
                    Kind = AiProviderKinds.OpenAiCompatible,
                    BaseUrl = "http://localhost:11434/v1",
                    ApiKey = null,
                    CustomCapabilities = AiCapability.FollowUpLlm | AiCapability.Grading
                }
            ],
            Routing = new AiCapabilityRouting
            {
                Llm = new AiCapabilityTarget { ProfileId = "ollama", Model = "llama3" }
            }
        });

        var llm = sp.GetRequiredService<IFollowUpGeneratorService>();
        Assert.IsNotType<UnavailableFollowUpGeneratorService>(llm);
    }

    [Fact]
    public void Routing_StrictMode_MissingProfile_ReturnsUnavailableNotMock()    {
        using var sp = BuildStrictProvider(new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles = [new AiProviderProfile { Id = "p-gem", Name = "Gemini", Kind = AiProviderKinds.Gemini, ApiKey = "AIzaSyTest123" }],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "deleted-profile" },
                Tts = new AiTtsCapabilityTarget { ProfileId = null },
                Llm = new AiCapabilityTarget { ProfileId = "p-gem", Model = "gemini-3.8-flash" }
            }
        });

        var stt = Assert.IsType<UnavailableSpeechToTextService>(sp.GetRequiredService<ISpeechToTextService>());
        Assert.Contains(AiUnavailableMessages.NoProfileSelected, stt.ErrorMessage);
        Assert.IsType<UnavailableTextToSpeechService>(sp.GetRequiredService<ITextToSpeechService>());
        Assert.IsType<GeminiFollowUpGeneratorService>(sp.GetRequiredService<IFollowUpGeneratorService>());
    }

    [Fact]
    public void Routing_ExplicitMockMode_StillUsesMocksWhenRealAiDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(
            new AiServiceOptions { UseMock = true, AllowMockFallback = false }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(new AiConfigurationModel { UseRealAi = false }));
        services.AddAiServices();
        using var sp = services.BuildServiceProvider();

        Assert.IsType<MockSpeechToTextService>(sp.GetRequiredService<ISpeechToTextService>());
        Assert.IsType<MockTextToSpeechService>(sp.GetRequiredService<ITextToSpeechService>());
        Assert.IsType<MockFollowUpGeneratorService>(sp.GetRequiredService<IFollowUpGeneratorService>());
    }

    [Fact]
    public async Task Routing_BrowserSpeechSynthesis_SkipsCloudTtsEvenWhenGeminiTtsIsRouted()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(
            new AiServiceOptions { UseMock = false, AllowMockFallback = false, UseBrowserSpeechSynthesis = true }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles = [new AiProviderProfile { Id = "p-gem", Name = "Gemini", Kind = AiProviderKinds.Gemini, ApiKey = "AIzaSyTest123" }],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p-gem", Model = "gemini-3.8-flash" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "p-gem", Model = "gemini-3.8-flash-tts", Voice = "Puck" },
                Llm = new AiCapabilityTarget { ProfileId = "p-gem", Model = "gemini-3.8-flash" }
            }
        }));
        services.AddAiServices();
        using var sp = services.BuildServiceProvider();

        var tts = Assert.IsType<BrowserSpeechSynthesisTextToSpeechService>(sp.GetRequiredService<ITextToSpeechService>());
        var result = await tts.SynthesizeAsync("Câu hỏi");

        Assert.True(result.IsSuccess);
        Assert.Null(result.AudioUrl);
        Assert.IsType<GeminiSpeechToTextService>(sp.GetRequiredService<ISpeechToTextService>());
        Assert.IsType<GeminiFollowUpGeneratorService>(sp.GetRequiredService<IFollowUpGeneratorService>());
    }

    [Fact]
    public async Task Adapter_WhenAllowMockFallbackIsFalse_DoesNotFallbackToMockAndReturnsVietnameseError()
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile { Id = "p-gem", Name = "Gemini", Kind = AiProviderKinds.Gemini, ApiKey = "AIzaSyTest123" }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p-gem", Model = "gemini-2.5-flash" }
            }
        };

        var failingHttpClient = new HttpClient(new FailingHttpMessageHandler(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"Quota exceeded\"}}"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(failingHttpClient);
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(
            new AiServiceOptions { UseMock = false, AllowMockFallback = false }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(config));
        services.AddAiServices();

        var sp = services.BuildServiceProvider();
        var stt = sp.GetRequiredService<ISpeechToTextService>();

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var result = await stt.TranscribeAsync(audioStream, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Empty(result.TranscriptText);
        Assert.Contains("Lỗi nhận diện giọng nói Gemini", result.ErrorMessage);
    }

    [Fact]
    public async Task Adapter_WhenAllowMockFallbackIsTrue_FallsBackToMockOnApiFailure()
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile { Id = "p-gem", Name = "Gemini", Kind = AiProviderKinds.Gemini, ApiKey = "AIzaSyTest123" }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "p-gem", Model = "gemini-2.5-flash" }
            }
        };

        var failingHttpClient = new HttpClient(new FailingHttpMessageHandler(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"Quota exceeded\"}}"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(failingHttpClient);
        services.AddSingleton<IOptions<AiServiceOptions>>(Microsoft.Extensions.Options.Options.Create(
            new AiServiceOptions { UseMock = false, AllowMockFallback = true }));
        services.AddSingleton<IAiCredentialStore>(new MockCredentialStore(config));
        services.AddAiServices();

        var sp = services.BuildServiceProvider();
        var stt = sp.GetRequiredService<ISpeechToTextService>();

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var result = await stt.TranscribeAsync(audioStream, "audio/webm");

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.TranscriptText);
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:8000", true)]
    [InlineData("http://192.168.1.100:5000", true)]
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("http://172.16.0.1:8000", true)]
    [InlineData("http://172.31.255.255:8000", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://generativelanguage.googleapis.com", false)]
    [InlineData("http://localhost.example.com", false)]
    [InlineData("http://8.8.8.8:8000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NetworkEndpointHelper_Correctly_Identifies_Local_And_Lan_Addresses(string? url, bool expected)
    {
        Assert.Equal(expected, NetworkEndpointHelper.IsLocalOrLan(url));
    }

    private sealed class FailingHttpMessageHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody)
            });
        }
    }

    private sealed class FaultyThrowingAdapter : IAiProviderAdapter
    {
        public string Kind => "faulty-kind";
        public AiCapability SupportedCapabilities => AiCapability.All;
        public string GetDefaultModel(AiCapability capability) => "faulty-default";

        public ISpeechToTextService? CreateSpeechToTextService(IServiceProvider sp, AiProviderProfile profile, string model) =>
            throw new InvalidOperationException($"Crash STT with secret key {profile.ApiKey}");

        public ITextToSpeechService? CreateTextToSpeechService(IServiceProvider sp, AiProviderProfile profile, string model, string? voice, string? cacheDirectory) =>
            throw new InvalidOperationException($"Crash TTS with secret key {profile.ApiKey}");

        public IFollowUpGeneratorService? CreateFollowUpGeneratorService(IServiceProvider sp, AiProviderProfile profile, string model) =>
            throw new InvalidOperationException($"Crash LLM with secret key {profile.ApiKey}");

        public Task<(bool Ok, string Message)> TestConnectionAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default) =>
            Task.FromResult((false, "Crash"));

        public Task<IReadOnlyList<string>> ListModelsAsync(HttpClient httpClient, AiProviderProfile profile, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
