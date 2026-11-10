using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIVES.BLL;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Mocks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class ProtectedAiCredentialStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _storePath;
    private readonly IDataProtectionProvider _dataProtection;

    public ProtectedAiCredentialStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "AivesCredentialTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _storePath = Path.Combine(_tempDirectory, "ai-credentials.json");
        _dataProtection = new EphemeralDataProtectionProvider();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private ProtectedAiCredentialStore CreateStore(string? storePath = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            CredentialStorePath = storePath ?? _storePath
        });

        return new ProtectedAiCredentialStore(
            _dataProtection,
            options,
            NullLogger<ProtectedAiCredentialStore>.Instance);
    }

    [Fact]
    public async Task Store_Saves_And_Reads_Key_Correctly()
    {
        var store = CreateStore();
        const string fakeKey = "test-key-not-real-1234567890";

        await store.SaveAsync(fakeKey, useRealAi: true);

        var status = store.GetStatus();
        Assert.True(status.HasApiKey);
        Assert.True(status.UseRealAi);
        Assert.Equal("…7890", status.KeyHint);
        Assert.NotNull(status.UpdatedAtUtc);
        Assert.Equal(fakeKey, store.GetApiKey());

        // Reload in new store instance to test disk persistence & unprotect
        var reloadedStore = CreateStore();
        var reloadedStatus = reloadedStore.GetStatus();
        Assert.True(reloadedStatus.HasApiKey);
        Assert.True(reloadedStatus.UseRealAi);
        Assert.Equal("…7890", reloadedStatus.KeyHint);
        Assert.Equal(fakeKey, reloadedStore.GetApiKey());
    }

    [Fact]
    public async Task Store_File_Does_Not_Contain_Raw_Key()
    {
        var store = CreateStore();
        const string fakeKey = "test-key-not-real-1234567890";

        await store.SaveAsync(fakeKey, useRealAi: true);

        Assert.True(File.Exists(_storePath));
        var content = await File.ReadAllTextAsync(_storePath);
        Assert.DoesNotContain(fakeKey, content);
        Assert.Contains("protectedApiKey", content);
    }

    [Fact]
    public async Task Store_ClearAsync_Clears_Key_And_Deletes_File()
    {
        var store = CreateStore();
        const string fakeKey = "test-key-not-real-1234567890";

        await store.SaveAsync(fakeKey, useRealAi: true);
        Assert.True(File.Exists(_storePath));

        await store.ClearAsync();

        var status = store.GetStatus();
        Assert.False(status.HasApiKey);
        Assert.False(status.UseRealAi);
        Assert.Null(status.KeyHint);
        Assert.Null(store.GetApiKey());
        Assert.False(File.Exists(_storePath));
    }

    [Fact]
    public async Task Store_Corrupted_File_Does_Not_Throw_And_Treats_As_Unconfigured()
    {
        await File.WriteAllTextAsync(_storePath, "{ invalid json content ]");

        var store = CreateStore();
        var status = store.GetStatus();

        Assert.False(status.HasApiKey);
        Assert.False(status.UseRealAi);
        Assert.Null(store.GetApiKey());
    }

    [Theory]
    [InlineData("too-short")]
    [InlineData("key with whitespace 1234567890")]
    public async Task Store_Rejects_Invalid_Key_Format(string invalidKey)
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(invalidKey, useRealAi: true));
    }

    [Fact]
    public void Resolver_With_Mock_And_Empty_Store_Returns_Mock()
    {
        var configured = new AiServiceOptions { UseMock = true };
        var store = CreateStore();

        var resolved = AiSettingsResolver.Resolve(configured, store);

        Assert.True(resolved.UseMock);
        Assert.False(resolved.UsesRealProviders);
        Assert.Null(resolved.OpenAI.ApiKey);
    }

    [Fact]
    public async Task Resolver_With_Store_Key_And_UseRealAi_Returns_Real_Providers()
    {
        var configured = new AiServiceOptions { UseMock = true };
        var store = CreateStore();
        const string fakeKey = "test-key-not-real-1234567890";

        await store.SaveAsync(fakeKey, useRealAi: true);

        var resolved = AiSettingsResolver.Resolve(configured, store);

        Assert.False(resolved.UseMock);
        Assert.True(resolved.UsesRealProviders);
        Assert.Equal(fakeKey, resolved.OpenAI.ApiKey);
    }

    [Fact]
    public async Task Resolver_With_Store_UseRealAi_False_Returns_Mock_Even_With_Key()
    {
        var configured = new AiServiceOptions { UseMock = false };
        var store = CreateStore();
        const string fakeKey = "test-key-not-real-1234567890";

        await store.SaveAsync(fakeKey, useRealAi: false);

        var resolved = AiSettingsResolver.Resolve(configured, store);

        Assert.True(resolved.UseMock);
        Assert.False(resolved.UsesRealProviders);
    }

    [Fact]
    public async Task AddAiServices_Dynamic_Resolution_Sends_Authorization_Header()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new TestHttpHandler((req, ct) =>
        {
            capturedRequest = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"test transcript"}""", Encoding.UTF8, "application/json")
            });
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDataProtectionProvider>(_dataProtection);
        services.Configure<AiServiceOptions>(opts =>
        {
            opts.UseMock = true;
            opts.CredentialStorePath = _storePath;
        });

        services.ConfigureHttpClientDefaults(builder =>
        {
            builder.ConfigurePrimaryHttpMessageHandler(() => handler);
        });

        services.AddAiServices();

        var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<IAiCredentialStore>();
        const string fakeKey = "test-key-not-real-1234567890";
        await store.SaveAsync(fakeKey, useRealAi: true);

        using (var scope = sp.CreateScope())
        {
            var stt = scope.ServiceProvider.GetRequiredService<ISpeechToTextService>();
            Assert.IsType<WhisperSpeechToTextService>(stt);

            using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
            var result = await stt.TranscribeAsync(audioStream, "audio/webm");

            Assert.True(result.IsSuccess);
            Assert.NotNull(capturedRequest);
            Assert.Equal($"Bearer {fakeKey}", capturedRequest.Headers.Authorization?.ToString());
        }

        // Now set useRealAi = false; next scope should yield mock service
        await store.SaveAsync(null, useRealAi: false);
        using (var scope = sp.CreateScope())
        {
            var stt = scope.ServiceProvider.GetRequiredService<ISpeechToTextService>();
            Assert.IsType<MockSpeechToTextService>(stt);
        }
    }

    [Fact]
    public async Task Tts_With_CacheDirectory_Saves_Audio_File_To_Disk()
    {
        var dummyMp3Bytes = new byte[] { 0xFF, 0xFB, 0x90, 0xC4, 1, 2, 3, 4 };
        var handler = new TestHttpHandler((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(dummyMp3Bytes)
            };
            return Task.FromResult(res);
        });

        var cacheDir = Path.Combine(_tempDirectory, "audio-cache");
        using var client = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            OpenAI = new OpenAiOptions { ApiKey = "test-key-not-real-1234567890" },
            TtsCacheDirectory = cacheDir
        });

        var tts = new OpenAiTextToSpeechService(client, options, cacheDirectory: cacheDir);
        var result = await tts.SynthesizeAsync("Xin chào");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.AudioUrl);
        Assert.StartsWith("/audio/cache/tts-", result.AudioUrl);

        var fileName = Path.GetFileName(result.AudioUrl);
        var savedFile = Path.Combine(cacheDir, fileName);
        Assert.True(File.Exists(savedFile));
        var savedBytes = await File.ReadAllBytesAsync(savedFile);
        Assert.Equal(dummyMp3Bytes, savedBytes);
    }

    [Fact]
    public async Task Legacy_Single_Key_Format_Automatically_Upgrades_To_MultiProfile_Format()
    {
        const string legacyKey = "sk-legacy-test-key-1234567890";
        var protector = _dataProtection.CreateProtector("AIVES.AiCredentials.v1");
        var protectedKey = protector.Protect(legacyKey);

        var legacyJson = "{\"protectedApiKey\": \"" + protectedKey + "\", \"useRealAi\": true, \"updatedAtUtc\": \"2026-10-15T12:00:00Z\"}";

        await File.WriteAllTextAsync(_storePath, legacyJson);

        var store = CreateStore();
        var config = store.GetConfiguration();

        Assert.True(config.UseRealAi);
        Assert.Single(config.Profiles);
        Assert.Equal(legacyKey, config.Profiles[0].ApiKey);
        Assert.Equal(AIVES.BLL.Models.Ai.AiProviderKinds.OpenAi, config.Profiles[0].Kind);

        // Routing automatically initialized to the upgraded profile
        Assert.Equal(config.Profiles[0].Id, config.Routing.Stt.ProfileId);
        Assert.Equal(config.Profiles[0].Id, config.Routing.Tts.ProfileId);
        Assert.Equal(config.Profiles[0].Id, config.Routing.Llm.ProfileId);
    }

    [Fact]
    public async Task Multi_Profile_CRUD_And_Persistence()
    {
        var store = CreateStore();

        var config = new AIVES.BLL.Models.Ai.AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "gemini-profile",
                    Name = "Google Gemini",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.Gemini,
                    BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                    ApiKey = "AIzaSyGeminiKey1234567890"
                },
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "groq-profile",
                    Name = "Groq Llama",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
                    BaseUrl = "https://api.groq.com/openai/v1",
                    ApiKey = "gsk_testGroqKey1234567890",
                    CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.FollowUpLlm
                }
            ],
            Routing = new AIVES.BLL.Models.Ai.AiCapabilityRouting
            {
                Stt = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "gemini-profile", Model = "gemini-2.5-flash" },
                Tts = new AIVES.BLL.Models.Ai.AiTtsCapabilityTarget { ProfileId = "gemini-profile", Model = "gemini-2.5-flash-preview-tts", Voice = "Puck" },
                Llm = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "groq-profile", Model = "llama-3.3-70b" }
            }
        };

        await store.SaveConfigurationAsync(config);

        // Verify disk file does not contain raw keys
        var diskContent = await File.ReadAllTextAsync(_storePath);
        Assert.DoesNotContain("AIzaSyGeminiKey1234567890", diskContent);
        Assert.DoesNotContain("gsk_testGroqKey1234567890", diskContent);

        // Reload from disk in a fresh store instance
        var reloadedStore = CreateStore();
        var reloadedConfig = reloadedStore.GetConfiguration();

        Assert.True(reloadedConfig.UseRealAi);
        Assert.Equal(2, reloadedConfig.Profiles.Count);

        var gemini = reloadedConfig.FindProfile("gemini-profile");
        Assert.NotNull(gemini);
        Assert.Equal("AIzaSyGeminiKey1234567890", gemini.ApiKey);

        var groq = reloadedConfig.FindProfile("groq-profile");
        Assert.NotNull(groq);
        Assert.Equal("gsk_testGroqKey1234567890", groq.ApiKey);
        Assert.Equal(AIVES.BLL.Models.Ai.AiCapability.FollowUpLlm, groq.CustomCapabilities);

        Assert.Equal("gemini-profile", reloadedConfig.Routing.Stt.ProfileId);
        Assert.Equal("groq-profile", reloadedConfig.Routing.Llm.ProfileId);
    }

    [Fact]
    public void Legacy_V1_With_Gemini_Key_Upgrades_To_Gemini_Profile()
    {
        var protector = _dataProtection.CreateProtector("AIVES.AiCredentials.v1");
        var protectedKey = protector.Protect("AIzaSyTestGeminiKey1234567890");

        var legacyJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            protectedApiKey = protectedKey,
            useRealAi = true,
            updatedAtUtc = DateTime.UtcNow
        });

        File.WriteAllText(_storePath, legacyJson);

        var store = CreateStore();
        var config = store.GetConfiguration();

        Assert.True(config.UseRealAi);
        var gemini = config.Profiles.FirstOrDefault(p => p.Kind == AIVES.BLL.Models.Ai.AiProviderKinds.Gemini);
        Assert.NotNull(gemini);
        Assert.Equal("AIzaSyTestGeminiKey1234567890", gemini.ApiKey);
        Assert.Equal("gemini-3.8-flash", config.Routing.Stt.Model);
        Assert.Equal("gemini-3.8-flash-tts", config.Routing.Tts.Model);
        Assert.Equal("Puck", config.Routing.Tts.Voice);
        Assert.Equal("gemini-3.8-flash", config.Routing.Llm.Model);
    }

    [Fact]
    public async Task V2_OpenAi_Profile_With_Misattributed_Gemini_Key_Is_Cleaned_Up()
    {
        var store = CreateStore();

        var config = new AIVES.BLL.Models.Ai.AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "openai-default",
                    Name = "OpenAI",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAi,
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = "AIzaSyMisattributedKey12345" // Accidental Gemini key
                },
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "gemini-real",
                    Name = "Google Gemini Real",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.Gemini,
                    BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                    ApiKey = "AIzaSyMisattributedKey12345"
                }
            ]
        };

        await store.SaveConfigurationAsync(config);

        // Reload store in fresh instance: should clean up the misattributed key on openai-default
        var reloadedStore = CreateStore();
        var reloadedConfig = reloadedStore.GetConfiguration();

        var openai = reloadedConfig.FindProfile("openai-default");
        var gemini = reloadedConfig.FindProfile("gemini-real");

        Assert.NotNull(openai);
        Assert.Null(openai.ApiKey); // Cleaned up!
        Assert.NotNull(gemini);
        Assert.Equal("AIzaSyMisattributedKey12345", gemini.ApiKey); // Kept!
    }

    [Fact]
    public async Task GradingRouting_Survives_Save_And_Reload()
    {
        var store = CreateStore();

        var config = store.GetConfiguration();
        config.UseRealAi = true;
        config.Profiles.Add(new AIVES.BLL.Models.Ai.AiProviderProfile
        {
            Id = "ds-grading",
            Name = "DeepSeek Grading",
            Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "test-key-not-real-1234567890",
            CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.Grading
        });
        config.Routing.Grading = new AIVES.BLL.Models.Ai.AiCapabilityTarget
        {
            ProfileId = "ds-grading",
            Model = "deepseek-v4.1-flash-1017"
        };

        await store.SaveConfigurationAsync(config);

        // Fresh instance reads from disk: Grading routing must not fall back to Mock.
        var reloadedConfig = CreateStore().GetConfiguration();

        Assert.Equal("ds-grading", reloadedConfig.Routing.Grading.ProfileId);
        Assert.Equal("deepseek-v4.1-flash-1017", reloadedConfig.Routing.Grading.Model);
    }

    [Fact]
    public async Task GetConfiguration_Consecutive_Calls_Preserve_Grading_Routing_Via_CloneConfiguration()
    {
        var store = CreateStore();

        var config = store.GetConfiguration();
        config.UseRealAi = true;
        config.Profiles.Add(new AIVES.BLL.Models.Ai.AiProviderProfile
        {
            Id = "grading-profile",
            Name = "Grading Provider",
            Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
            BaseUrl = "https://api.example.com",
            ApiKey = "sk-test-not-real-0000000000",
            CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.Grading
        });
        config.Routing.Grading = new AIVES.BLL.Models.Ai.AiCapabilityTarget
        {
            ProfileId = "grading-profile",
            Model = "deepseek-chat"
        };

        await store.SaveConfigurationAsync(config);

        // Lần đọc thứ nhất.
        var firstRead = store.GetConfiguration();
        Assert.NotNull(firstRead.Routing.Grading);
        Assert.Equal("grading-profile", firstRead.Routing.Grading.ProfileId);
        Assert.Equal("deepseek-chat", firstRead.Routing.Grading.Model);

        // Lần đọc thứ hai liên tiếp: nếu CloneConfiguration bỏ sót Grading thì chỗ này sẽ null.
        var secondRead = store.GetConfiguration();
        Assert.NotNull(secondRead.Routing.Grading);
        Assert.Equal("grading-profile", secondRead.Routing.Grading.ProfileId);
        Assert.Equal("deepseek-chat", secondRead.Routing.Grading.Model);

        // Sửa trên bản đã đọc không được rò sang lần đọc sau (bản chất của Clone).
        firstRead.Routing.Grading.Model = "mutated-model";
        var thirdRead = store.GetConfiguration();
        Assert.Equal("deepseek-chat", thirdRead.Routing.Grading.Model);
    }

    [Fact]
    public async Task Legacy_V2_Without_Grading_Key_Loads_Without_Error_And_Grading_Is_Empty()
    {
        // File cấu hình v2 sinh ra TRƯỚC khi có tính năng Grading routing: không có khóa "grading".
        // Phải đọc được bình thường, giữ nguyên STT/TTS/LLM, và Grading rỗng (=> Mock như hành vi cũ).
        var protector = _dataProtection.CreateProtector("AIVES.AiCredentials.v1");
        var protectedKey = protector.Protect("sk-legacy-not-real-000000");

        var jsonWithoutGrading = $$"""
        {
          "schemaVersion": 2,
          "useRealAi": true,
          "updatedAtUtc": "2026-10-20T10:00:00Z",
          "profiles": [
            {
              "id": "openai-default",
              "name": "OpenAI",
              "kind": "OpenAI",
              "baseUrl": "https://api.openai.com/v1",
              "protectedApiKey": "{{protectedKey}}",
              "customCapabilities": 15
            }
          ],
          "routing": {
            "stt": { "profileId": "openai-default", "model": "whisper-1" },
            "tts": { "profileId": "openai-default", "model": "tts-1", "voice": "alloy" },
            "llm": { "profileId": "openai-default", "model": "gpt-4o-mini" }
          }
        }
        """;

        await File.WriteAllTextAsync(_storePath, jsonWithoutGrading);

        var config = CreateStore().GetConfiguration();

        Assert.True(config.UseRealAi);
        Assert.Single(config.Profiles);

        // Các năng lực cũ giữ nguyên.
        Assert.Equal("openai-default", config.Routing.Stt.ProfileId);
        Assert.Equal("whisper-1", config.Routing.Stt.Model);
        Assert.Equal("openai-default", config.Routing.Tts.ProfileId);
        Assert.Equal("alloy", config.Routing.Tts.Voice);
        Assert.Equal("openai-default", config.Routing.Llm.ProfileId);
        Assert.Equal("gpt-4o-mini", config.Routing.Llm.Model);

        // Grading rỗng -> Mock, không ném lỗi.
        Assert.NotNull(config.Routing.Grading);
        Assert.Null(config.Routing.Grading.ProfileId);
        Assert.Null(config.Routing.Grading.Model);
    }

    [Fact]
    public async Task Saved_Json_Contains_Grading_Key_And_Never_Raw_ApiKey()
    {
        var store = CreateStore();
        const string realLookingKey = "sk-should-never-be-plaintext-1234";

        var config = store.GetConfiguration();
        config.UseRealAi = true;
        config.Profiles.Add(new AIVES.BLL.Models.Ai.AiProviderProfile
        {
            Id = "ds-grading",
            Name = "DeepSeek AI",
            Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
            BaseUrl = "https://api.deepseek.com",
            ApiKey = realLookingKey,
            CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.Grading
        });
        config.Routing.Grading = new AIVES.BLL.Models.Ai.AiCapabilityTarget
        {
            ProfileId = "ds-grading",
            Model = "deepseek-chat"
        };

        await store.SaveConfigurationAsync(config);

        Assert.True(File.Exists(_storePath));
        var json = await File.ReadAllTextAsync(_storePath);

        // Phải có khóa "grading" trong routing...
        Assert.Contains("\"grading\"", json);
        // ...nhưng API key phải được mã hoá, không được nằm dạng thô.
        Assert.DoesNotContain(realLookingKey, json);
        Assert.Contains("protectedApiKey", json);
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
