using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Security;
using AIVES.BLL.Services.Ai;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using StudentNameMVC.Pages.Admin;
using Xunit;

namespace AIVES.Tests.Frontend;

/// <summary>
/// The dedicated "Kết nối DeepSeek" card: paste a key, get a working LLM+Grading route.
/// These tests pin the server-side handler behaviour, not the HTML.
/// </summary>
public sealed class DeepSeekSettingsPageTests
{
    private sealed class FakeStore : IAiCredentialStore
    {
        private AiConfigurationModel _config;

        public FakeStore(AiConfigurationModel? config = null)
        {
            _config = config ?? new AiConfigurationModel();
        }

        public AiConfigurationModel GetConfiguration() => _config;
        public AiCredentialStatus GetStatus() => new(true, _config.UseRealAi, "…1234", null);
        public string? GetApiKey() => _config.Profiles.FirstOrDefault()?.ApiKey;
        public Task SaveConfigurationAsync(AiConfigurationModel config, CancellationToken ct = default)
        {
            _config = config;
            return Task.CompletedTask;
        }

        public Task SaveAsync(string? apiKey, bool useRealAi, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeTester(bool ok, string message, IReadOnlyList<string>? models = null) : IAiConnectionTester
    {
        public AiProviderProfile? LastProfile { get; private set; }

        public Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult((ok, message));

        public Task<(bool Ok, string Message)> TestProfileAsync(AiProviderProfile profile, CancellationToken ct = default)
        {
            LastProfile = profile;
            return Task.FromResult((ok, message));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(AiProviderProfile profile, CancellationToken ct = default) =>
            Task.FromResult(models ?? (IReadOnlyList<string>)[]);
    }

    private static AiSettingsModel CreateModel(IAiCredentialStore store, IAiConnectionTester tester)
    {
        var registry = new AiProviderRegistry([
            new AIVES.BLL.Services.Ai.Adapters.OpenAiProviderAdapter(),
            new AIVES.BLL.Services.Ai.Adapters.GeminiProviderAdapter(),
            new AIVES.BLL.Services.Ai.Adapters.OpenAiCompatibleProviderAdapter()
        ]);
        var model = new AiSettingsModel(store, tester, registry, NullLogger<AiSettingsModel>.Instance);

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "ADMIN01"),
            new Claim(ClaimTypes.Name, "Administrator"),
            new Claim(ClaimTypes.Role, ApplicationRoles.Admin)
        ], "TestAuth");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("localhost");

        model.PageContext = new PageContext { HttpContext = httpContext };
        model.TempData = new TempDataDictionary(httpContext, new EmptyTempDataProvider());
        return model;
    }

    private sealed class EmptyTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    [Fact]
    public void DeepSeekProfile_is_null_when_nothing_is_configured()
    {
        Assert.Null(AiSettingsModel.FindDeepSeekProfile(new AiConfigurationModel()));
    }

    [Fact]
    public void DeepSeekProfile_is_found_by_base_url_even_with_a_random_id()
    {
        var config = new AiConfigurationModel
        {
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = "a1b2c3d4",
                    Name = "My DeepSeek",
                    Kind = AiProviderKinds.OpenAiCompatible,
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-deepseek-1234567890"
                }
            ]
        };

        var found = AiSettingsModel.FindDeepSeekProfile(config);

        Assert.NotNull(found);
        Assert.Equal("a1b2c3d4", found!.Id);
    }

    [Fact]
    public async Task SaveDeepSeek_creates_the_profile_and_routes_llm_and_grading()
    {
        var store = new FakeStore();
        var model = CreateModel(store, new FakeTester(true, "OK"));
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel
        {
            ApiKey = "sk-deepseek-1234567890",
            Model = AiProviderModelDefaults.DeepSeekChat,
            EnableLlm = true,
            EnableGrading = true
        };

        var result = await model.OnPostSaveDeepSeekAsync();

        Assert.IsType<RedirectToPageResult>(result);

        var saved = store.GetConfiguration();
        var profile = Assert.Single(saved.Profiles);
        Assert.Equal(AiProviderModelDefaults.DeepSeekProfileId, profile.Id);
        Assert.Equal(AiProviderKinds.OpenAiCompatible, profile.Kind);
        Assert.Equal("https://api.deepseek.com/v1", profile.BaseUrl);
        Assert.Equal("sk-deepseek-1234567890", profile.ApiKey);
        Assert.True(profile.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm));
        Assert.True(profile.CustomCapabilities.HasFlag(AiCapability.Grading));
        Assert.False(profile.CustomCapabilities.HasFlag(AiCapability.SpeechToText));
        Assert.False(profile.CustomCapabilities.HasFlag(AiCapability.TextToSpeech));

        Assert.Equal(profile.Id, saved.Routing.Llm.ProfileId);
        Assert.Equal("deepseek-chat", saved.Routing.Llm.Model);
        Assert.Equal(profile.Id, saved.Routing.Grading.ProfileId);
        Assert.Equal("deepseek-chat", saved.Routing.Grading.Model);
        Assert.True(saved.UseRealAi);
    }

    [Fact]
    public async Task SaveDeepSeek_never_touches_stt_or_tts_routing()
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = "gemini",
                    Name = "Gemini",
                    Kind = AiProviderKinds.Gemini,
                    BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                    ApiKey = "AIzaSyTest1234567890",
                    CustomCapabilities = AiCapability.All
                }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "gemini", Model = "gemini-3.8-flash" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "gemini", Model = "gemini-3.8-flash-tts", Voice = "Puck" }
            }
        };
        var store = new FakeStore(config);
        var model = CreateModel(store, new FakeTester(true, "OK"));
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel
        {
            ApiKey = "sk-deepseek-1234567890",
            Model = AiProviderModelDefaults.DeepSeekChat,
            EnableLlm = true,
            EnableGrading = true
        };

        await model.OnPostSaveDeepSeekAsync();

        var saved = store.GetConfiguration();
        Assert.Equal("gemini", saved.Routing.Stt.ProfileId);
        Assert.Equal("gemini-3.8-flash", saved.Routing.Stt.Model);
        Assert.Equal("gemini", saved.Routing.Tts.ProfileId);
        Assert.Equal("gemini-3.8-flash-tts", saved.Routing.Tts.Model);
        Assert.Equal("Puck", saved.Routing.Tts.Voice);
    }

    [Fact]
    public async Task SaveDeepSeek_requires_a_key_for_a_new_profile()
    {
        var store = new FakeStore();
        var model = CreateModel(store, new FakeTester(true, "OK"));
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel
        {
            ApiKey = "",
            Model = AiProviderModelDefaults.DeepSeekChat
        };

        var result = await model.OnPostSaveDeepSeekAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey("DeepSeekInput.ApiKey"));
        Assert.Empty(store.GetConfiguration().Profiles);
    }

    [Fact]
    public async Task SaveDeepSeek_rejects_reasoner_model_for_grading()
    {
        var store = new FakeStore();
        var model = CreateModel(store, new FakeTester(true, "OK"));
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel
        {
            ApiKey = "sk-deepseek-1234567890",
            Model = AiProviderModelDefaults.DeepSeekReasoner
        };

        var result = await model.OnPostSaveDeepSeekAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
        Assert.True(model.ModelState.ContainsKey("DeepSeekInput.Model"));
        Assert.Contains("deepseek-chat", model.ModelState["DeepSeekInput.Model"]!.Errors[0].ErrorMessage);
    }

    [Fact]
    public async Task SaveDeepSeek_requires_at_least_one_capability()
    {
        var store = new FakeStore();
        var model = CreateModel(store, new FakeTester(true, "OK"));
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel
        {
            ApiKey = "sk-deepseek-1234567890",
            Model = AiProviderModelDefaults.DeepSeekChat,
            EnableLlm = false,
            EnableGrading = false
        };

        var result = await model.OnPostSaveDeepSeekAsync();

        Assert.IsType<PageResult>(result);
        Assert.False(model.ModelState.IsValid);
    }

    [Fact]
    public async Task SaveDeepSeek_updates_an_existing_profile_without_duplicating_it()
    {
        var config = new AiConfigurationModel
        {
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = AiProviderModelDefaults.DeepSeekProfileId,
                    Name = "DeepSeek",
                    Kind = AiProviderKinds.OpenAiCompatible,
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-old-key-1234567890",
                    CustomCapabilities = AiCapability.Grading
                }
            ]
        };
        var store = new FakeStore(config);
        var model = CreateModel(store, new FakeTester(true, "OK"));
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel
        {
            ApiKey = "",                        // keep the stored key
            Model = AiProviderModelDefaults.DeepSeekChat,
            EnableLlm = true,
            EnableGrading = true
        };

        await model.OnPostSaveDeepSeekAsync();

        var saved = store.GetConfiguration();
        var profile = Assert.Single(saved.Profiles);
        Assert.Equal("sk-old-key-1234567890", profile.ApiKey);
        Assert.True(profile.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm));
    }

    [Fact]
    public async Task TestDeepSeek_uses_the_typed_key_and_reports_models()
    {
        var store = new FakeStore();
        var tester = new FakeTester(true, "Kết nối OpenAI-compatible thành công (HTTP 200 OK).",
            ["deepseek-chat", "deepseek-reasoner"]);
        var model = CreateModel(store, tester);
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel { ApiKey = "sk-typed-key-1234567890" };

        var result = await model.OnPostTestDeepSeekAsync();

        Assert.IsType<RedirectToPageResult>(result);
        Assert.NotNull(tester.LastProfile);
        Assert.Equal("sk-typed-key-1234567890", tester.LastProfile!.ApiKey);
        Assert.Equal("https://api.deepseek.com/v1", tester.LastProfile.BaseUrl);
        Assert.Contains("deepseek-chat", model.TempData["TestSuccessMessage"]?.ToString());
    }

    [Fact]
    public async Task TestDeepSeek_fails_without_a_key()
    {
        var store = new FakeStore();
        var model = CreateModel(store, new FakeTester(true, "OK"));
        model.DeepSeekInput = new AiSettingsModel.DeepSeekInputModel { ApiKey = "" };

        await model.OnPostTestDeepSeekAsync();

        Assert.Contains("Vui lòng nhập API key", model.TempData["TestErrorMessage"]?.ToString());
    }

    [Fact]
    public async Task RemoveDeepSeek_deletes_the_profile_and_clears_routing()
    {
        var config = new AiConfigurationModel
        {
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = AiProviderModelDefaults.DeepSeekProfileId,
                    Name = "DeepSeek",
                    Kind = AiProviderKinds.OpenAiCompatible,
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-deepseek-1234567890",
                    CustomCapabilities = AiCapability.Grading | AiCapability.FollowUpLlm
                }
            ],
            Routing = new AiCapabilityRouting
            {
                Llm = new AiCapabilityTarget { ProfileId = AiProviderModelDefaults.DeepSeekProfileId, Model = "deepseek-chat" },
                Grading = new AiCapabilityTarget { ProfileId = AiProviderModelDefaults.DeepSeekProfileId, Model = "deepseek-chat" }
            }
        };
        var store = new FakeStore(config);
        var model = CreateModel(store, new FakeTester(true, "OK"));

        var result = await model.OnPostRemoveDeepSeekAsync();

        Assert.IsType<RedirectToPageResult>(result);
        var saved = store.GetConfiguration();
        Assert.Empty(saved.Profiles);
        Assert.Null(saved.Routing.Llm.ProfileId);
        Assert.Null(saved.Routing.Grading.ProfileId);
    }

    [Fact]
    public void LoadViewModelData_populates_the_deepseek_card_state()
    {
        var config = new AiConfigurationModel
        {
            UseRealAi = true,
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = AiProviderModelDefaults.DeepSeekProfileId,
                    Name = "DeepSeek",
                    Kind = AiProviderKinds.OpenAiCompatible,
                    BaseUrl = "https://api.deepseek.com/v1",
                    ApiKey = "sk-deepseek-1234567890",
                    CustomCapabilities = AiCapability.Grading | AiCapability.FollowUpLlm
                }
            ],
            Routing = new AiCapabilityRouting
            {
                Grading = new AiCapabilityTarget { ProfileId = AiProviderModelDefaults.DeepSeekProfileId, Model = "deepseek-chat" },
                Llm = new AiCapabilityTarget { ProfileId = AiProviderModelDefaults.DeepSeekProfileId, Model = "deepseek-chat" }
            }
        };
        var store = new FakeStore(config);
        var model = CreateModel(store, new FakeTester(true, "OK"));

        model.OnGet();

        Assert.NotNull(model.DeepSeekProfile);
        Assert.True(model.DeepSeekGradingEnabled);
        Assert.True(model.DeepSeekLlmEnabled);
        Assert.Equal("deepseek-chat", model.DeepSeekInput.Model);
        Assert.Null(model.DeepSeekInput.ApiKey); // never echo the key back into the form
    }
}
