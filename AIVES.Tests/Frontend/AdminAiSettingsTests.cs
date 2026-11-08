using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Security;
using AIVES.BLL.Services.Ai;
using Microsoft.AspNetCore.Authentication;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudentNameMVC.Pages.Admin;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class AdminAiSettingsTests : IClassFixture<AdminAiSettingsTests.AdminCompositionFactory>
{
    public sealed class AdminCompositionFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "true",
                ["AiServices:UseMock"] = "true",
                ["MockData:SeedOnStartup"] = "false",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=AdminFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "admin@example.test",
                ["DefaultAdmin:Password"] = "admin-password-test"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }
    }

    private readonly AdminCompositionFactory _factory;

    public AdminAiSettingsTests(AdminCompositionFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_AiSettings_AnonymousUser_IsRedirectedToLogin()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Admin/AiSettings");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Contains("/Account/Login", response.Headers.Location.ToString());
    }

    [Fact]
    public void OnGet_Never_Exposes_ApiKey_In_Model()
    {
        var fakeStore = new InMemoryCredentialStore("test-key-not-real-1234567890", useRealAi: true);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        model.OnGet();

        Assert.Null(model.ApiKey);
        Assert.True(model.Status.HasApiKey);
        Assert.Equal("…7890", model.Status.KeyHint);
        Assert.True(model.UseRealAi);
    }

    [Fact]
    public async Task OnPostSave_WhenEnablingRealAiWithoutKey_AddsModelError()
    {
        var fakeStore = new InMemoryCredentialStore(null, useRealAi: false);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);
        model.ApiKey = "";
        model.UseRealAi = true;

        var result = await model.OnPostSaveAsync();

        Assert.IsType<PageResult>(result);
        Assert.True(model.ModelState.ContainsKey(nameof(model.ApiKey)));
        Assert.False(fakeStore.SaveCalled);
    }

    [Fact]
    public async Task OnPostSave_WithValidKey_CallsStoreAndRedirects()
    {
        var fakeStore = new InMemoryCredentialStore(null, useRealAi: false);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);
        const string fakeKey = "test-key-not-real-1234567890";
        model.ApiKey = fakeKey;
        model.UseRealAi = true;

        var result = await model.OnPostSaveAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(redirect.PageName); // Redirects to same page
        Assert.True(fakeStore.SaveCalled);
        Assert.Equal(fakeKey, fakeStore.SavedKey);
        Assert.True(fakeStore.SavedUseRealAi);
    }

    [Fact]
    public async Task OnPostClear_CallsClearAndRedirects()
    {
        var fakeStore = new InMemoryCredentialStore("test-key-not-real-1234567890", useRealAi: true);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        var result = await model.OnPostClearAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(redirect.PageName);
        Assert.True(fakeStore.ClearCalled);
    }

    [Fact]
    public async Task ConnectionTester_Success_WhenApiReturns200()
    {
        var handler = new TestHttpHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data": []}""", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var store = new InMemoryCredentialStore("test-key-not-real-1234567890", true);
        var monitor = new TestOptionsMonitor<AiServiceOptions>(new AiServiceOptions());
        var tester = new OpenAiConnectionTester(client, store, monitor, NullLogger<OpenAiConnectionTester>.Instance);

        var (ok, message) = await tester.TestConnectionAsync();

        Assert.True(ok);
        Assert.Contains("200 OK", message);
    }

    [Fact]
    public async Task ConnectionTester_Failure_WhenApiReturns401_DoesNotContainKey()
    {
        const string fakeKey = "sk-test-SECRET-4f9a1234567890";
        var handler = new TestHttpHandler((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error": {"message": "Invalid API key."}}""", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var store = new InMemoryCredentialStore(fakeKey, true);
        var monitor = new TestOptionsMonitor<AiServiceOptions>(new AiServiceOptions());
        var tester = new OpenAiConnectionTester(client, store, monitor, NullLogger<OpenAiConnectionTester>.Instance);

        var (ok, message) = await tester.TestConnectionAsync();

        Assert.False(ok);
        Assert.Contains("401", message);
        Assert.DoesNotContain(fakeKey, message);
    }

    [Fact]
    public async Task Get_AiSettings_RendersDeepSeekConnectCard()
    {
        using var factory = new RenderFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/AiSettings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Contains("Kết nối DeepSeek", html);
        Assert.Contains("DeepSeekInput.ApiKey", html);
        Assert.Contains("https://api.deepseek.com/v1", html);
        Assert.Contains("deepseek-chat", html);
        // The dedicated card must not let the admin route audio through DeepSeek.
        Assert.DoesNotContain("DeepSeekInput.EnableStt", html);
        Assert.DoesNotContain("DeepSeekInput.EnableTts", html);
    }

    /// <summary>
    /// Authenticated RenderFactory: the class-level fixture uses the real cookie handler, so a shared
    /// <c>ApiSettings</c> render test needs its own header-driven auth scheme.
    /// </summary>
    private sealed class RenderFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "false",
                ["AiServices:UseMock"] = "true",
                ["MockData:SeedOnStartup"] = "false",
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=AdminRender;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "admin@example.test",
                ["DefaultAdmin:Password"] = "admin-password-test"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(o =>
                {
                    o.DefaultAuthenticateScheme = "AdminRender";
                    o.DefaultChallengeScheme = "AdminRender";
                }).AddScheme<AuthenticationSchemeOptions, AdminRenderHandler>("AdminRender", _ => { });
                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }

        private sealed class AdminRenderHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
        {
            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                var claims = new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, "ADMIN01"),
                    new Claim(ClaimTypes.Name, "Administrator"),
                    new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
                };
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(claims, "AdminRender")), "AdminRender")));
            }
        }
    }

    [Fact]
    public async Task Get_AiSettings_WhenVivaDisabled_Returns200ForAdmin()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "TestAdmin";
                    options.DefaultChallengeScheme = "TestAdmin";
                }).AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>("TestAdmin", _ => { });
            });
        });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Admin/AiSettings");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnPostTest_WhenConnectionTesterFails_SetsTestErrorMessageAndRedirects()
    {
        var fakeStore = new InMemoryCredentialStore(null, useRealAi: false);
        var tester = new DummyConnectionTester(false, "Chưa cấu hình OpenAI API key.");
        var model = CreatePageModel(fakeStore, tester);

        var result = await model.OnPostTestAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(redirect.PageName);
        Assert.Equal("Chưa cấu hình OpenAI API key.", model.TempData["TestErrorMessage"]);
    }

    [Fact]
    public async Task OnPostSaveRouting_WhenProfileDoesNotSupportCapability_AddsModelErrorAndReturnsPage()
    {
        var config = new AIVES.BLL.Models.Ai.AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "tts-only-profile",
                    Name = "ElevenLabs",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
                    ApiKey = "sk-1234567890",
                    BaseUrl = "https://api.elevenlabs.io",
                    CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.TextToSpeech
                }
            ]
        };

        var fakeStore = new InMemoryCredentialStore("sk-1234567890", useRealAi: true, config);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        model.RoutingInput = new AiSettingsModel.RoutingInputModel
        {
            UseRealAi = true,
            SttProfileId = "tts-only-profile",
            SttModel = "whisper-1"
        };

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey("RoutingInput.SttProfileId"));
        Assert.Contains("không hỗ trợ", model.ModelState["RoutingInput.SttProfileId"]!.Errors[0].ErrorMessage);
    }

    [Fact]
    public async Task OnPostSaveProfile_WhenKindChanges_UpdatesRoutedModelsToNewKindDefaults()
    {
        var config = new AIVES.BLL.Models.Ai.AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "p1",
                    Name = "MultiProvider",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAi,
                    ApiKey = "sk-1234567890",
                    BaseUrl = "https://api.openai.com/v1",
                    CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.All
                }
            ],
            Routing = new AIVES.BLL.Models.Ai.AiCapabilityRouting
            {
                Stt = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "p1", Model = "whisper-1" },
                Tts = new AIVES.BLL.Models.Ai.AiTtsCapabilityTarget { ProfileId = "p1", Model = "tts-1", Voice = "alloy" },
                Llm = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "p1", Model = "gpt-4o-mini" }
            }
        };

        var fakeStore = new InMemoryCredentialStore("sk-1234567890", useRealAi: true, config);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        model.ProfileInput = new AiSettingsModel.ProfileInputModel
        {
            Id = "p1",
            Name = "MultiProvider Gemini",
            Kind = AIVES.BLL.Models.Ai.AiProviderKinds.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = "AIzaSyTest1234567890",
            EnableStt = true,
            EnableTts = true,
            EnableLlm = true
        };

        var result = await model.OnPostSaveProfileAsync();

        Assert.IsType<RedirectToPageResult>(result);
        var updated = fakeStore.GetConfiguration();
        Assert.Equal("gemini-3.8-flash", updated.Routing.Stt.Model);
        Assert.Equal("gemini-3.8-flash-tts", updated.Routing.Tts.Model);
        Assert.Equal("Puck", updated.Routing.Tts.Voice);
        Assert.Equal("gemini-3.8-flash", updated.Routing.Llm.Model);
    }

    [Fact]
    public async Task OnPostSaveProfile_LocalOrLanBaseUrl_DoesNotRequireApiKey()
    {
        var fakeStore = new InMemoryCredentialStore(null, useRealAi: false);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        model.ProfileInput = new AiSettingsModel.ProfileInputModel
        {
            Name = "Local Ollama on LAN",
            Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
            BaseUrl = "http://192.168.1.55:11434/v1",
            ApiKey = null,
            EnableLlm = true
        };

        var result = await model.OnPostSaveProfileAsync();

        Assert.IsType<RedirectToPageResult>(result);
        Assert.True(model.ModelState.IsValid);
        Assert.True(fakeStore.SaveCalled);
    }

    [Fact]
    public async Task OnPostSaveRouting_WhenModelIsEmpty_AssignsDefaultModelForProfile()
    {
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
                    ApiKey = "AIzaSyTest1234567890",
                    BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                    CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.All
                }
            ]
        };

        var fakeStore = new InMemoryCredentialStore("AIzaSyTest1234567890", useRealAi: true, config);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        model.RoutingInput = new AiSettingsModel.RoutingInputModel
        {
            UseRealAi = true,
            SttProfileId = "gemini-profile",
            SttModel = "", // Empty
            TtsProfileId = "gemini-profile",
            TtsModel = null, // Null
            TtsVoice = "", // Empty
            LlmProfileId = "gemini-profile",
            LlmModel = "  " // Whitespace
        };

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<RedirectToPageResult>(result);
        Assert.True(model.ModelState.IsValid);

        var saved = fakeStore.GetConfiguration();
        Assert.Equal("gemini-3.8-flash", saved.Routing.Stt.Model);
        Assert.Equal("gemini-3.8-flash-tts", saved.Routing.Tts.Model);
        Assert.Equal("Puck", saved.Routing.Tts.Voice);
        Assert.Equal("gemini-3.8-flash", saved.Routing.Llm.Model);
    }

    [Fact]
    public async Task OnPostSaveRouting_WhenModelIsFromAnotherVendor_AddsModelError()
    {
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
                    ApiKey = "AIzaSyTest1234567890",
                    BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                    CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.All
                }
            ]
        };

        var fakeStore = new InMemoryCredentialStore("AIzaSyTest1234567890", useRealAi: true, config);
        var tester = new DummyConnectionTester(true, "OK");
        var model = CreatePageModel(fakeStore, tester);

        // Assign OpenAI model names to a Gemini profile
        model.RoutingInput = new AiSettingsModel.RoutingInputModel
        {
            UseRealAi = true,
            SttProfileId = "gemini-profile",
            SttModel = "whisper-1", // OpenAI model!
            TtsProfileId = "gemini-profile",
            TtsModel = "tts-1", // OpenAI model!
            LlmProfileId = "gemini-profile",
            LlmModel = "gpt-4o-mini" // OpenAI model!
        };

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey("RoutingInput.SttModel"));
        Assert.Contains("OpenAI", model.ModelState["RoutingInput.SttModel"]!.Errors[0].ErrorMessage);
        Assert.True(model.ModelState.ContainsKey("RoutingInput.TtsModel"));
        Assert.Contains("OpenAI", model.ModelState["RoutingInput.TtsModel"]!.Errors[0].ErrorMessage);
        Assert.True(model.ModelState.ContainsKey("RoutingInput.LlmModel"));
        Assert.Contains("OpenAI", model.ModelState["RoutingInput.LlmModel"]!.Errors[0].ErrorMessage);
    }

    [Fact]
    public async Task OnPostSaveRouting_DeepSeekProfile_DefaultsToDeepSeekChatNotOpenAi()
    {
        var config = new AIVES.BLL.Models.Ai.AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "deepseek",
                    Name = "DeepSeek",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
                    ApiKey = "sk-deepseek-1234567890",
                    BaseUrl = "https://api.deepseek.com/v1",
                    CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.Grading | AIVES.BLL.Models.Ai.AiCapability.FollowUpLlm
                }
            ]
        };

        var fakeStore = new InMemoryCredentialStore("sk-deepseek-1234567890", useRealAi: true, config);
        var model = CreatePageModel(fakeStore, new DummyConnectionTester(true, "OK"));

        model.RoutingInput = new AiSettingsModel.RoutingInputModel
        {
            UseRealAi = true,
            LlmProfileId = "deepseek",
            LlmModel = "",       // must resolve to deepseek-chat, not gpt-4o-mini
            GradingProfileId = "deepseek",
            GradingModel = null  // must resolve to deepseek-chat
        };

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<RedirectToPageResult>(result);
        Assert.True(model.ModelState.IsValid);

        var saved = fakeStore.GetConfiguration();
        Assert.Equal("deepseek-chat", saved.Routing.Llm.Model);
        Assert.Equal("deepseek-chat", saved.Routing.Grading.Model);
    }

    [Fact]
    public async Task OnPostSaveRouting_DeepSeekProfile_RejectsWhisperSttModel()
    {
        var config = new AIVES.BLL.Models.Ai.AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "deepseek",
                    Name = "DeepSeek",
                    Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
                    ApiKey = "sk-deepseek-1234567890",
                    BaseUrl = "https://api.deepseek.com/v1",
                    CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.Grading
                }
            ]
        };

        var fakeStore = new InMemoryCredentialStore("sk-deepseek-1234567890", useRealAi: true, config);
        var model = CreatePageModel(fakeStore, new DummyConnectionTester(true, "OK"));

        model.RoutingInput = new AiSettingsModel.RoutingInputModel
        {
            UseRealAi = true,
            SttProfileId = "deepseek",
            SttModel = "whisper-1"
        };

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey("RoutingInput.SttProfileId"));
    }

    private static AiSettingsModel CreatePageModel(IAiCredentialStore store, IAiConnectionTester tester)
    {
        var registry = new AIVES.BLL.Services.Ai.AiProviderRegistry([
            new AIVES.BLL.Services.Ai.Adapters.OpenAiProviderAdapter(),
            new AIVES.BLL.Services.Ai.Adapters.GeminiProviderAdapter(),
            new AIVES.BLL.Services.Ai.Adapters.OpenAiCompatibleProviderAdapter()
        ]);
        var model = new AiSettingsModel(store, tester, registry, NullLogger<AiSettingsModel>.Instance);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "ADMIN01"),
            new(ClaimTypes.Name, "Administrator"),
            new(ClaimTypes.Role, ApplicationRoles.Admin)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };
        var pageContext = new PageContext { HttpContext = httpContext };
        model.PageContext = pageContext;
        model.TempData = new TempDataDictionary(httpContext, new TempDataProviderStub());
        return model;
    }

    private sealed class InMemoryCredentialStore : IAiCredentialStore
    {
        private string? _key;
        private bool _useRealAi;
        private AIVES.BLL.Models.Ai.AiConfigurationModel? _customConfig;

        public bool SaveCalled { get; private set; }
        public string? SavedKey { get; private set; }
        public bool SavedUseRealAi { get; private set; }
        public bool ClearCalled { get; private set; }

        public InMemoryCredentialStore(string? initialKey, bool useRealAi, AIVES.BLL.Models.Ai.AiConfigurationModel? customConfig = null)
        {
            _key = initialKey;
            _useRealAi = useRealAi;
            _customConfig = customConfig;
        }

        public AiCredentialStatus GetStatus() =>
            new(!string.IsNullOrWhiteSpace(_key), _useRealAi, _key?.Length >= 4 ? "…" + _key[^4..] : null, DateTime.UtcNow);

        public string? GetApiKey() => _key;

        public Task SaveAsync(string? newApiKey, bool useRealAi, CancellationToken ct = default)
        {
            SaveCalled = true;
            if (!string.IsNullOrWhiteSpace(newApiKey)) _key = newApiKey;
            _useRealAi = useRealAi;
            SavedKey = _key;
            SavedUseRealAi = _useRealAi;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            ClearCalled = true;
            _key = null;
            _useRealAi = false;
            return Task.CompletedTask;
        }

        public AIVES.BLL.Models.Ai.AiConfigurationModel GetConfiguration()
        {
            if (_customConfig != null) return _customConfig;

            return new()
            {
                UseRealAi = _useRealAi,
                Profiles = string.IsNullOrWhiteSpace(_key) ? [] : [new AIVES.BLL.Models.Ai.AiProviderProfile { Id = "p1", Name = "OpenAI", Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAi, ApiKey = _key, BaseUrl = "https://api.openai.com/v1" }],
                Routing = new AIVES.BLL.Models.Ai.AiCapabilityRouting
                {
                    Stt = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "p1", Model = "whisper-1" },
                    Tts = new AIVES.BLL.Models.Ai.AiTtsCapabilityTarget { ProfileId = "p1", Model = "tts-1", Voice = "alloy" },
                    Llm = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "p1", Model = "gpt-4o-mini" }
                }
            };
        }

        public Task SaveConfigurationAsync(AIVES.BLL.Models.Ai.AiConfigurationModel config, CancellationToken ct = default)
        {
            SaveCalled = true;
            _customConfig = config;
            _useRealAi = config.UseRealAi;
            _key = config.Profiles.FirstOrDefault()?.ApiKey;
            SavedKey = _key;
            SavedUseRealAi = _useRealAi;
            return Task.CompletedTask;
        }
    }

    private sealed class DummyConnectionTester : IAiConnectionTester
    {
        private readonly bool _ok;
        private readonly string _msg;

        public DummyConnectionTester(bool ok, string msg)
        {
            _ok = ok;
            _msg = msg;
        }

        public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) =>
            Task.FromResult((_ok, _msg));

        public Task<(bool Ok, string Message)> TestProfileAsync(AIVES.BLL.Models.Ai.AiProviderProfile profile, CancellationToken ct = default) =>
            Task.FromResult((_ok, _msg));

        public Task<IReadOnlyList<string>> ListModelsAsync(AIVES.BLL.Models.Ai.AiProviderProfile profile, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["gpt-4o-mini", "gemini-2.5-flash"]);
    }

    /// <summary>Returns a fixed "live" ListModels result for Gemini profiles (no network).</summary>
    private sealed class LiveModelsConnectionTester(IReadOnlyList<string>? liveModels) : IAiConnectionTester
    {
        public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((true, "OK"));
        public Task<(bool Ok, string Message)> TestProfileAsync(AIVES.BLL.Models.Ai.AiProviderProfile profile, CancellationToken ct = default) => Task.FromResult((true, "OK"));
        public Task<IReadOnlyList<string>> ListModelsAsync(AIVES.BLL.Models.Ai.AiProviderProfile profile, CancellationToken ct = default) =>
            Task.FromResult(liveModels ?? (IReadOnlyList<string>)Array.Empty<string>());
        public Task<IReadOnlyList<string>?> TryListLiveModelsAsync(AIVES.BLL.Models.Ai.AiProviderProfile profile, CancellationToken ct = default) =>
            Task.FromResult(liveModels);
    }

    private static AIVES.BLL.Models.Ai.AiConfigurationModel GeminiOnlyConfig() => new()
    {
        UseRealAi = true,
        Profiles =
        [
            new AIVES.BLL.Models.Ai.AiProviderProfile
            {
                Id = "gemini-profile",
                Name = "Google Gemini",
                Kind = AIVES.BLL.Models.Ai.AiProviderKinds.Gemini,
                ApiKey = "AIzaSyTest1234567890",
                BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.All
            }
        ],
        Routing = new AIVES.BLL.Models.Ai.AiCapabilityRouting
        {
            Stt = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "gemini-profile", Model = "gemini-3.8-flash" },
            Tts = new AIVES.BLL.Models.Ai.AiTtsCapabilityTarget { ProfileId = "gemini-profile", Model = "gemini-3.8-flash-tts", Voice = "Kore" },
            Llm = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "gemini-profile", Model = "gemini-3.8-flash" }
        }
    };

    private static readonly string[] KienLiveModels = ["gemini-3.8-flash", "gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts", "gemini-3.5-flash", "gemini-3.5-flash-lite"];

    private static AiSettingsModel.RoutingInputModel GeminiRouting(string stt, string tts, string llm) => new()
    {
        UseRealAi = true,
        SttProfileId = "gemini-profile", SttModel = stt,
        TtsProfileId = "gemini-profile", TtsModel = tts, TtsVoice = "Puck",
        LlmProfileId = "gemini-profile", LlmModel = llm
    };

    [Fact]
    public async Task OnPostSaveRouting_GeminiModelNotInLiveList_AddsErrorWithSuggestionAndDoesNotSave()
    {
        var fakeStore = new InMemoryCredentialStore("AIzaSyTest1234567890", useRealAi: true, GeminiOnlyConfig());
        var model = CreatePageModel(fakeStore, new LiveModelsConnectionTester(KienLiveModels));
        model.RoutingInput = GeminiRouting("gemini-3.8-flash", "gemini-3.8-flash-preview-tts", "gemini-1.5-flash");

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(fakeStore.SaveCalled);
        var ttsError = Assert.Single(model.ModelState["RoutingInput.TtsModel"]!.Errors).ErrorMessage;
        Assert.Contains("'gemini-3.8-flash-preview-tts' không có trong danh sách model", ttsError);
        Assert.Contains("Gợi ý: 'gemini-3.8-flash-tts'", ttsError);
        var llmError = Assert.Single(model.ModelState["RoutingInput.LlmModel"]!.Errors).ErrorMessage;
        Assert.Contains("Gợi ý: 'gemini-", llmError);
        Assert.False(model.ModelState.ContainsKey("RoutingInput.SttModel"));
        Assert.Equal("gemini-3.8-flash-preview-tts", model.RoutingInput.TtsModel); // user input kept on the page
    }

    [Theory]
    [InlineData("gemini-2.0-flash", "RoutingInput.TtsModel", "không phải model đọc giọng nói")]
    [InlineData("gemini-3.8-flash-tts", "RoutingInput.SttModel", "là model đọc giọng nói (TTS)")]
    public async Task OnPostSaveRouting_GeminiModelForWrongCapability_AddsError(string wrongModel, string field, string expected)
    {
        var fakeStore = new InMemoryCredentialStore("AIzaSyTest1234567890", useRealAi: true, GeminiOnlyConfig());
        var model = CreatePageModel(fakeStore, new DummyConnectionTester(true, "OK")); // no live list available
        model.RoutingInput = field == "RoutingInput.TtsModel"
            ? GeminiRouting("gemini-3.8-flash", wrongModel, "gemini-3.8-flash")
            : GeminiRouting(wrongModel, "gemini-3.8-flash-tts", "gemini-3.8-flash");

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<PageResult>(result);
        Assert.Contains(expected, Assert.Single(model.ModelState[field]!.Errors).ErrorMessage);
        Assert.False(fakeStore.SaveCalled);
    }

    [Fact]
    public async Task OnPostSaveRouting_GeminiModelsInLiveList_Saves()
    {
        var fakeStore = new InMemoryCredentialStore("AIzaSyTest1234567890", useRealAi: true, GeminiOnlyConfig());
        var model = CreatePageModel(fakeStore, new LiveModelsConnectionTester(KienLiveModels));
        model.RoutingInput = GeminiRouting("gemini-3.8-flash", "gemini-3.8-flash-lite-tts", "gemini-3.5-flash");

        var result = await model.OnPostSaveRoutingAsync();

        Assert.IsType<RedirectToPageResult>(result);
        var saved = fakeStore.GetConfiguration();
        Assert.Equal("gemini-3.8-flash-lite-tts", saved.Routing.Tts.Model);
        Assert.Equal("gemini-3.5-flash", saved.Routing.Llm.Model);
    }

    [Fact]
    public async Task OnPostSaveProfile_SameKind_KeepsValidRoutedModels()
    {
        var fakeStore = new InMemoryCredentialStore("AIzaSyTest1234567890", useRealAi: true, GeminiOnlyConfig());
        var model = CreatePageModel(fakeStore, new DummyConnectionTester(true, "OK"));
        model.ProfileInput = new AiSettingsModel.ProfileInputModel
        {
            Id = "gemini-profile",
            Name = "Google Gemini (renamed)",
            Kind = AIVES.BLL.Models.Ai.AiProviderKinds.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta"
        };

        var result = await model.OnPostSaveProfileAsync();

        Assert.IsType<RedirectToPageResult>(result);
        var saved = fakeStore.GetConfiguration();
        Assert.Equal("gemini-3.8-flash", saved.Routing.Stt.Model);
        Assert.Equal("gemini-3.8-flash-tts", saved.Routing.Tts.Model);
        Assert.Equal("Kore", saved.Routing.Tts.Voice);
        Assert.Equal("gemini-3.8-flash", saved.Routing.Llm.Model);
    }

    [Fact]
    public async Task OnPostSaveProfile_KindChange_KeepsModelsStillCompatibleWithNewKind()
    {
        var config = new AIVES.BLL.Models.Ai.AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AIVES.BLL.Models.Ai.AiProviderProfile
                {
                    Id = "p1", Name = "Proxy", Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAiCompatible,
                    ApiKey = "sk-1234567890", BaseUrl = "https://proxy.example/v1", CustomCapabilities = AIVES.BLL.Models.Ai.AiCapability.All
                }
            ],
            Routing = new AIVES.BLL.Models.Ai.AiCapabilityRouting
            {
                Llm = new AIVES.BLL.Models.Ai.AiCapabilityTarget { ProfileId = "p1", Model = "gpt-4o" }
            }
        };
        var fakeStore = new InMemoryCredentialStore("sk-1234567890", useRealAi: true, config);
        var model = CreatePageModel(fakeStore, new DummyConnectionTester(true, "OK"));
        model.ProfileInput = new AiSettingsModel.ProfileInputModel
        {
            Id = "p1", Name = "OpenAI", Kind = AIVES.BLL.Models.Ai.AiProviderKinds.OpenAi, BaseUrl = "https://api.openai.com/v1"
        };

        await model.OnPostSaveProfileAsync();

        Assert.Equal("gpt-4o", fakeStore.GetConfiguration().Routing.Llm.Model);
    }

    private sealed class TempDataProviderStub : ITempDataProvider
    {
        private readonly Dictionary<string, object?> _values = new();
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public T CurrentValue { get; }
        public TestOptionsMonitor(T value) => CurrentValue = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class TestHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) => _handler(req, ct);
    }

    private sealed class TestAdminAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAdminAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "ADMIN01"),
                new Claim(ClaimTypes.Name, "Administrator"),
                new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
            };
            var identity = new ClaimsIdentity(claims, "TestAdmin");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "TestAdmin");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
