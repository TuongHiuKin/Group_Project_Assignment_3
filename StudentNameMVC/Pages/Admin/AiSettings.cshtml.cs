using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Gemini;
using StudentNameMVC.Security;

namespace StudentNameMVC.Pages.Admin;

[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
public class AiSettingsModel : PageModel
{
    private readonly IAiCredentialStore _credentialStore;
    private readonly IAiConnectionTester _connectionTester;
    private readonly IAiProviderRegistry _providerRegistry;
    private readonly ILogger<AiSettingsModel> _logger;

    public AiSettingsModel(
        IAiCredentialStore credentialStore,
        IAiConnectionTester connectionTester,
        IAiProviderRegistry providerRegistry,
        ILogger<AiSettingsModel> logger)
    {
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _connectionTester = connectionTester ?? throw new ArgumentNullException(nameof(connectionTester));
        _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AiCredentialStatus Status { get; set; } = new(false, false, null, null);
    public AiConfigurationModel Configuration { get; set; } = new();

    /// <summary>Hồ sơ DeepSeek hiện có (nếu đã kết nối), dùng cho khối "Kết nối DeepSeek".</summary>
    public AiProviderProfile? DeepSeekProfile { get; set; }

    public bool DeepSeekGradingEnabled { get; set; }
    public bool DeepSeekLlmEnabled { get; set; }
    public string DeepSeekModel => DeepSeekProfile == null
        ? AiProviderModelDefaults.DeepSeekChat
        : Configuration.Routing.Grading.ProfileId == DeepSeekProfile.Id && !string.IsNullOrWhiteSpace(Configuration.Routing.Grading.Model)
            ? Configuration.Routing.Grading.Model!
            : AiProviderModelDefaults.DeepSeekChat;

    [BindProperty]
    public DeepSeekInputModel DeepSeekInput { get; set; } = new();

    [BindProperty]
    public string? ApiKey { get; set; }

    [BindProperty]
    public bool UseRealAi { get; set; }

    [BindProperty]
    public ProfileInputModel ProfileInput { get; set; } = new();

    [BindProperty]
    public RoutingInputModel RoutingInput { get; set; } = new();

    public void OnGet()
    {
        LoadViewModelData();
    }

    public async Task<IActionResult> OnPostSaveProfileAsync(CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();

        if (string.IsNullOrWhiteSpace(ProfileInput.Name))
        {
            ModelState.AddModelError("ProfileInput.Name", "Tên nhà cung cấp không được để trống.");
        }

        if (string.IsNullOrWhiteSpace(ProfileInput.BaseUrl))
        {
            if (string.Equals(ProfileInput.Kind, AiProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase))
            {
                ProfileInput.BaseUrl = "https://api.openai.com/v1";
            }
            else if (string.Equals(ProfileInput.Kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
            {
                ProfileInput.BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
            }
            else
            {
                ModelState.AddModelError("ProfileInput.BaseUrl", "Base URL không được để trống.");
            }
        }

        var isLocal = NetworkEndpointHelper.IsLocalOrLan(ProfileInput.BaseUrl);

        var isNew = string.IsNullOrWhiteSpace(ProfileInput.Id);
        var existing = isNew ? null : config.FindProfile(ProfileInput.Id);

        if (!string.IsNullOrWhiteSpace(ProfileInput.ApiKey))
        {
            var trimmed = ProfileInput.ApiKey.Trim();
            if ((trimmed.Length is < 10 or > 400 || trimmed.Any(char.IsWhiteSpace)) && !isLocal)
            {
                ModelState.AddModelError("ProfileInput.ApiKey", "API key không hợp lệ (phải từ 10 đến 400 ký tự và không chứa khoảng trắng).");
            }
        }
        else if (isNew && !isLocal)
        {
            ModelState.AddModelError("ProfileInput.ApiKey", "Vui lòng nhập API key cho nhà cung cấp mới.");
        }

        if (!ModelState.IsValid)
        {
            LoadViewModelData();
            return Page();
        }

        var capabilities = AiCapability.None;
        if (ProfileInput.EnableStt) capabilities |= AiCapability.SpeechToText;
        if (ProfileInput.EnableTts) capabilities |= AiCapability.TextToSpeech;
        if (ProfileInput.EnableLlm) capabilities |= AiCapability.FollowUpLlm;
        if (ProfileInput.EnableGrading) capabilities |= AiCapability.Grading;
        if (capabilities == AiCapability.None) capabilities = AiCapability.All;

        if (existing != null)
        {
            var oldKind = existing.Kind;
            existing.Name = ProfileInput.Name.Trim();
            existing.Kind = ProfileInput.Kind;
            existing.BaseUrl = ProfileInput.BaseUrl.TrimEnd('/');
            existing.OrganizationId = string.IsNullOrWhiteSpace(ProfileInput.OrganizationId) ? null : ProfileInput.OrganizationId.Trim();
            existing.CustomCapabilities = capabilities;
            if (!string.IsNullOrWhiteSpace(ProfileInput.ApiKey))
            {
                existing.ApiKey = ProfileInput.ApiKey.Trim();
            }

            // If the provider kind changed, reset only routed models that are no longer valid for the new kind
            if (!string.Equals(oldKind, ProfileInput.Kind, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(config.Routing.Stt.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase) &&
                    !IsModelCompatible(existing.Kind, AiCapability.SpeechToText, config.Routing.Stt.Model))
                {
                    config.Routing.Stt.Model = DefaultModelFor(existing, AiCapability.SpeechToText);
                }
                if (string.Equals(config.Routing.Tts.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase) &&
                    !IsModelCompatible(existing.Kind, AiCapability.TextToSpeech, config.Routing.Tts.Model))
                {
                    config.Routing.Tts.Model = DefaultModelFor(existing, AiCapability.TextToSpeech);
                    config.Routing.Tts.Voice = DefaultVoiceFor(existing.Kind);
                }
                if (string.Equals(config.Routing.Llm.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase) &&
                    !IsModelCompatible(existing.Kind, AiCapability.FollowUpLlm, config.Routing.Llm.Model))
                {
                    config.Routing.Llm.Model = DefaultModelFor(existing, AiCapability.FollowUpLlm);
                }
                if (string.Equals(config.Routing.Grading.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase) &&
                    !IsModelCompatible(existing.Kind, AiCapability.Grading, config.Routing.Grading.Model))
                {
                    config.Routing.Grading.Model = DefaultModelFor(existing, AiCapability.Grading);
                }
            }

            // Clear routing if capabilities were removed from this profile
            if (!capabilities.HasFlag(AiCapability.SpeechToText) && string.Equals(config.Routing.Stt.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Stt.ProfileId = null;
            }
            if (!capabilities.HasFlag(AiCapability.TextToSpeech) && string.Equals(config.Routing.Tts.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Tts.ProfileId = null;
            }
            if (!capabilities.HasFlag(AiCapability.FollowUpLlm) && string.Equals(config.Routing.Llm.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Llm.ProfileId = null;
            }
            if (!capabilities.HasFlag(AiCapability.Grading) && string.Equals(config.Routing.Grading.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Grading.ProfileId = null;
            }
        }
        else
        {
            var newProfile = new AiProviderProfile
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = ProfileInput.Name.Trim(),
                Kind = ProfileInput.Kind,
                BaseUrl = ProfileInput.BaseUrl.TrimEnd('/'),
                ApiKey = ProfileInput.ApiKey?.Trim(),
                OrganizationId = string.IsNullOrWhiteSpace(ProfileInput.OrganizationId) ? null : ProfileInput.OrganizationId.Trim(),
                CustomCapabilities = capabilities
            };
            config.Profiles.Add(newProfile);

            // Auto-assign routing if currently unassigned
            if (string.IsNullOrWhiteSpace(config.Routing.Stt.ProfileId) && capabilities.HasFlag(AiCapability.SpeechToText))
            {
                config.Routing.Stt.ProfileId = newProfile.Id;
                config.Routing.Stt.Model = DefaultModelFor(newProfile, AiCapability.SpeechToText);
            }
            if (string.IsNullOrWhiteSpace(config.Routing.Tts.ProfileId) && capabilities.HasFlag(AiCapability.TextToSpeech))
            {
                config.Routing.Tts.ProfileId = newProfile.Id;
                config.Routing.Tts.Model = DefaultModelFor(newProfile, AiCapability.TextToSpeech);
                config.Routing.Tts.Voice = DefaultVoiceFor(newProfile.Kind);
            }
            if (string.IsNullOrWhiteSpace(config.Routing.Llm.ProfileId) && capabilities.HasFlag(AiCapability.FollowUpLlm))
            {
                config.Routing.Llm.ProfileId = newProfile.Id;
                config.Routing.Llm.Model = DefaultModelFor(newProfile, AiCapability.FollowUpLlm);
            }
            if (string.IsNullOrWhiteSpace(config.Routing.Grading.ProfileId) && capabilities.HasFlag(AiCapability.Grading))
            {
                config.Routing.Grading.ProfileId = newProfile.Id;
                config.Routing.Grading.Model = DefaultModelFor(newProfile, AiCapability.Grading);
            }
        }

        await _credentialStore.SaveConfigurationAsync(config, ct);
        TempData["SuccessMessage"] = isNew
            ? $"Đã thêm nhà cung cấp '{ProfileInput.Name}' thành công."
            : $"Đã cập nhật hồ sơ '{ProfileInput.Name}' thành công.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteProfileAsync(string profileId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return RedirectToPage();
        }

        var config = _credentialStore.GetConfiguration();
        var profile = config.FindProfile(profileId);
        if (profile != null)
        {
            config.Profiles.Remove(profile);

            // Reset routings pointing to deleted profile
            if (string.Equals(config.Routing.Stt.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Stt.ProfileId = config.Profiles.FirstOrDefault(p => p.CustomCapabilities.HasFlag(AiCapability.SpeechToText))?.Id;
            }
            if (string.Equals(config.Routing.Tts.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Tts.ProfileId = config.Profiles.FirstOrDefault(p => p.CustomCapabilities.HasFlag(AiCapability.TextToSpeech))?.Id;
            }
            if (string.Equals(config.Routing.Llm.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Llm.ProfileId = config.Profiles.FirstOrDefault(p => p.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm))?.Id;
            }
            if (string.Equals(config.Routing.Grading.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                config.Routing.Grading.ProfileId = config.Profiles.FirstOrDefault(p => p.CustomCapabilities.HasFlag(AiCapability.Grading))?.Id;
            }

            await _credentialStore.SaveConfigurationAsync(config, ct);
            TempData["SuccessMessage"] = $"Đã xoá hồ sơ '{profile.Name}'.";
        }

        return RedirectToPage();
    }

    // ============================================================
    // DeepSeek quick-connect block (single API-key field)
    // ============================================================

    /// <summary>
    /// Creates or updates the DeepSeek profile from the dedicated "Kết nối DeepSeek" card and routes
    /// LLM/Grading to it. DeepSeek has no audio endpoints, so STT/TTS routing is left untouched.
    /// </summary>
    public async Task<IActionResult> OnPostSaveDeepSeekAsync(CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();
        var existing = FindDeepSeekProfile(config);
        var isNew = existing == null;

        var apiKey = (DeepSeekInput.ApiKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(apiKey) && isNew)
        {
            ModelState.AddModelError("DeepSeekInput.ApiKey", "Vui lòng nhập API key DeepSeek.");
        }
        else if (!string.IsNullOrWhiteSpace(apiKey) && (apiKey.Length is < 10 or > 400 || apiKey.Any(char.IsWhiteSpace)))
        {
            ModelState.AddModelError("DeepSeekInput.ApiKey", "API key DeepSeek không hợp lệ (10–400 ký tự, không chứa khoảng trắng).");
        }

        var wantedCaps = AiCapability.None;
        if (DeepSeekInput.EnableLlm) wantedCaps |= AiCapability.FollowUpLlm;
        if (DeepSeekInput.EnableGrading) wantedCaps |= AiCapability.Grading;
        if (wantedCaps == AiCapability.None)
        {
            ModelState.AddModelError(string.Empty, "Chọn ít nhất một năng lực (LLM hoặc Grading) cho DeepSeek.");
        }

        var model = string.IsNullOrWhiteSpace(DeepSeekInput.Model)
            ? AiProviderModelDefaults.DeepSeekChat
            : DeepSeekInput.Model.Trim();

        if (!string.Equals(model, AiProviderModelDefaults.DeepSeekChat, StringComparison.OrdinalIgnoreCase))
        {
            // deepseek-reasoner streams long reasoning text: it blows the 30s grading timeout and
            // returns JSON wrapped in prose. Only deepseek-chat is safe for structured grading.
            ModelState.AddModelError("DeepSeekInput.Model",
                $"Chỉ hỗ trợ '{AiProviderModelDefaults.DeepSeekChat}' cho chấm điểm. " +
                $"'{AiProviderModelDefaults.DeepSeekReasoner}' sinh chuỗi suy luận dài, dễ vượt timeout 30 giây và hỏng định dạng JSON.");
        }

        if (!ModelState.IsValid)
        {
            LoadViewModelData();
            return Page();
        }

        if (isNew)
        {
            existing = new AiProviderProfile
            {
                Id = AiProviderModelDefaults.DeepSeekProfileId,
                Name = "DeepSeek",
                Kind = AiProviderKinds.OpenAiCompatible,
                BaseUrl = AiProviderModelDefaults.DeepSeekBaseUrl
            };
            config.Profiles.Add(existing);
        }

        var profile = existing!;
        profile.Name = "DeepSeek";
        profile.Kind = AiProviderKinds.OpenAiCompatible;
        profile.BaseUrl = AiProviderModelDefaults.DeepSeekBaseUrl;
        profile.OrganizationId = null;
        profile.CustomCapabilities = wantedCaps;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            profile.ApiKey = apiKey;
        }

        // Route the enabled capabilities. Never touch STT/TTS: DeepSeek cannot serve audio.
        if (wantedCaps.HasFlag(AiCapability.FollowUpLlm))
        {
            config.Routing.Llm.ProfileId = profile.Id;
            config.Routing.Llm.Model = model;
        }
        else if (string.Equals(config.Routing.Llm.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            config.Routing.Llm.ProfileId = config.Profiles
                .FirstOrDefault(p => !string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase) &&
                                     p.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm))?.Id;
        }

        if (wantedCaps.HasFlag(AiCapability.Grading))
        {
            config.Routing.Grading.ProfileId = profile.Id;
            config.Routing.Grading.Model = model;
        }
        else if (string.Equals(config.Routing.Grading.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            config.Routing.Grading.ProfileId = config.Profiles
                .FirstOrDefault(p => !string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase) &&
                                     p.CustomCapabilities.HasFlag(AiCapability.Grading))?.Id;
        }

        config.UseRealAi = true;

        await _credentialStore.SaveConfigurationAsync(config, ct);
        TempData["SuccessMessage"] = isNew
            ? "Đã kết nối DeepSeek và định tuyến LLM/Grading thành công."
            : "Đã cập nhật kết nối DeepSeek thành công.";
        return RedirectToPage();
    }

    /// <summary>Verifies a DeepSeek key against GET /v1/models without persisting anything.</summary>
    public async Task<IActionResult> OnPostTestDeepSeekAsync(CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();
        var existing = FindDeepSeekProfile(config);

        var probe = new AiProviderProfile
        {
            Id = existing?.Id ?? "deepseek-probe",
            Name = "DeepSeek",
            Kind = AiProviderKinds.OpenAiCompatible,
            BaseUrl = AiProviderModelDefaults.DeepSeekBaseUrl,
            ApiKey = string.IsNullOrWhiteSpace(DeepSeekInput.ApiKey) ? existing?.ApiKey : DeepSeekInput.ApiKey!.Trim(),
            CustomCapabilities = AiCapability.FollowUpLlm | AiCapability.Grading
        };

        if (string.IsNullOrWhiteSpace(probe.ApiKey))
        {
            TempData["TestErrorMessage"] = "Vui lòng nhập API key DeepSeek trước khi kiểm tra.";
            return RedirectToPage();
        }

        var (ok, message) = await _connectionTester.TestProfileAsync(probe, ct);
        if (ok)
        {
            var models = await _connectionTester.ListModelsAsync(probe, ct);
            var suffix = models.Count > 0 ? $" Model khả dụng: {string.Join(", ", models)}." : string.Empty;
            TempData["TestSuccessMessage"] = $"[DeepSeek] {message}{suffix}";
        }
        else
        {
            TempData["TestErrorMessage"] = $"[DeepSeek] {message}";
        }

        return RedirectToPage();
    }

    /// <summary>Removes the DeepSeek profile and clears routing that pointed to it.</summary>
    public async Task<IActionResult> OnPostRemoveDeepSeekAsync(CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();
        var existing = FindDeepSeekProfile(config);
        if (existing == null)
        {
            return RedirectToPage();
        }

        config.Profiles.Remove(existing);
        if (string.Equals(config.Routing.Llm.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase))
        {
            config.Routing.Llm.ProfileId = config.Profiles.FirstOrDefault(p => p.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm))?.Id;
        }
        if (string.Equals(config.Routing.Grading.ProfileId, existing.Id, StringComparison.OrdinalIgnoreCase))
        {
            config.Routing.Grading.ProfileId = config.Profiles.FirstOrDefault(p => p.CustomCapabilities.HasFlag(AiCapability.Grading))?.Id;
        }

        await _credentialStore.SaveConfigurationAsync(config, ct);
        TempData["SuccessMessage"] = "Đã ngắt kết nối DeepSeek.";
        return RedirectToPage();
    }

    /// <summary>Finds the DeepSeek profile: fixed id first, then any OpenAI-compatible profile pointing at deepseek.</summary>
    public static AiProviderProfile? FindDeepSeekProfile(AiConfigurationModel config) =>
        config.FindProfile(AiProviderModelDefaults.DeepSeekProfileId)
        ?? config.Profiles.FirstOrDefault(p =>
            string.Equals(p.Kind, AiProviderKinds.OpenAiCompatible, StringComparison.OrdinalIgnoreCase) &&
            p.BaseUrl.Contains("deepseek", StringComparison.OrdinalIgnoreCase));

    public async Task<IActionResult> OnPostSaveRoutingAsync(CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();

        if (!string.IsNullOrWhiteSpace(RoutingInput.SttProfileId))
        {
            var p = config.FindProfile(RoutingInput.SttProfileId);
            if (p == null)
            {
                ModelState.AddModelError("RoutingInput.SttProfileId", "Hồ sơ Speech-to-Text (STT) đã chọn không tồn tại.");
            }
            else if (!p.CustomCapabilities.HasFlag(AiCapability.SpeechToText))
            {
                ModelState.AddModelError("RoutingInput.SttProfileId", $"Hồ sơ '{p.Name}' không hỗ trợ năng lực Speech-to-Text (STT).");
            }
            else
            {
                var defaultModel = DefaultModelFor(p, AiCapability.SpeechToText);
                if (string.IsNullOrWhiteSpace(RoutingInput.SttModel))
                {
                    RoutingInput.SttModel = defaultModel;
                }
                else
                {
                    var modelTrimmed = RoutingInput.SttModel.Trim();
                    if (p.Kind == AiProviderKinds.Gemini && IsLikelyOpenAiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.SttModel",
                            $"Model '{modelTrimmed}' là của OpenAI, không tương thích với nhà cung cấp Google Gemini. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                    else if (p.Kind == AiProviderKinds.OpenAi && IsLikelyGeminiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.SttModel",
                            $"Model '{modelTrimmed}' là của Google Gemini, không tương thích với nhà cung cấp OpenAI. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(RoutingInput.TtsProfileId))
        {
            var p = config.FindProfile(RoutingInput.TtsProfileId);
            if (p == null)
            {
                ModelState.AddModelError("RoutingInput.TtsProfileId", "Hồ sơ Text-to-Speech (TTS) đã chọn không tồn tại.");
            }
            else if (!p.CustomCapabilities.HasFlag(AiCapability.TextToSpeech))
            {
                ModelState.AddModelError("RoutingInput.TtsProfileId", $"Hồ sơ '{p.Name}' không hỗ trợ năng lực Text-to-Speech (TTS).");
            }
            else
            {
                var defaultModel = DefaultModelFor(p, AiCapability.TextToSpeech);
                var defaultVoice = DefaultVoiceFor(p.Kind);

                if (string.IsNullOrWhiteSpace(RoutingInput.TtsModel))
                {
                    RoutingInput.TtsModel = defaultModel;
                }
                else
                {
                    var modelTrimmed = RoutingInput.TtsModel.Trim();
                    if (p.Kind == AiProviderKinds.Gemini && IsLikelyOpenAiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.TtsModel",
                            $"Model '{modelTrimmed}' là của OpenAI, không tương thích với nhà cung cấp Google Gemini. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                    else if (p.Kind == AiProviderKinds.OpenAi && IsLikelyGeminiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.TtsModel",
                            $"Model '{modelTrimmed}' là của Google Gemini, không tương thích với nhà cung cấp OpenAI. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                }

                if (string.IsNullOrWhiteSpace(RoutingInput.TtsVoice))
                {
                    RoutingInput.TtsVoice = defaultVoice;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(RoutingInput.LlmProfileId))
        {
            var p = config.FindProfile(RoutingInput.LlmProfileId);
            if (p == null)
            {
                ModelState.AddModelError("RoutingInput.LlmProfileId", "Hồ sơ Follow-up LLM đã chọn không tồn tại.");
            }
            else if (!p.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm))
            {
                ModelState.AddModelError("RoutingInput.LlmProfileId", $"Hồ sơ '{p.Name}' không hỗ trợ năng lực Follow-up LLM.");
            }
            else
            {
                var defaultModel = DefaultModelFor(p, AiCapability.FollowUpLlm);

                if (string.IsNullOrWhiteSpace(RoutingInput.LlmModel))
                {
                    RoutingInput.LlmModel = defaultModel;
                }
                else
                {
                    var modelTrimmed = RoutingInput.LlmModel.Trim();
                    if (p.Kind == AiProviderKinds.Gemini && IsLikelyOpenAiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.LlmModel",
                            $"Model '{modelTrimmed}' là của OpenAI, không tương thích với nhà cung cấp Google Gemini. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                    else if (p.Kind == AiProviderKinds.OpenAi && IsLikelyGeminiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.LlmModel",
                            $"Model '{modelTrimmed}' là của Google Gemini, không tương thích với nhà cung cấp OpenAI. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(RoutingInput.GradingProfileId))
        {
            var p = config.FindProfile(RoutingInput.GradingProfileId);
            if (p == null)
            {
                ModelState.AddModelError("RoutingInput.GradingProfileId", "Hồ sơ Chấm điểm AI đã chọn không tồn tại.");
            }
            else if (!p.CustomCapabilities.HasFlag(AiCapability.Grading))
            {
                ModelState.AddModelError("RoutingInput.GradingProfileId", $"Hồ sơ '{p.Name}' không hỗ trợ năng lực Chấm điểm AI.");
            }
            else
            {
                var defaultModel = DefaultModelFor(p, AiCapability.Grading);

                if (string.IsNullOrWhiteSpace(RoutingInput.GradingModel))
                {
                    RoutingInput.GradingModel = defaultModel;
                }
                else
                {
                    var modelTrimmed = RoutingInput.GradingModel.Trim();
                    if (p.Kind == AiProviderKinds.Gemini && IsLikelyOpenAiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.GradingModel",
                            $"Model '{modelTrimmed}' là của OpenAI, không tương thích với nhà cung cấp Google Gemini. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                    else if (p.Kind == AiProviderKinds.OpenAi && IsLikelyGeminiModel(modelTrimmed))
                    {
                        ModelState.AddModelError("RoutingInput.GradingModel",
                            $"Model '{modelTrimmed}' là của Google Gemini, không tương thích với nhà cung cấp OpenAI. Vui lòng sử dụng '{defaultModel}' hoặc để trống.");
                    }
                }
            }
        }

        if (ModelState.IsValid)
        {
            await ValidateGeminiModelAsync(config.FindProfile(RoutingInput.SttProfileId), AiCapability.SpeechToText, RoutingInput.SttModel, "RoutingInput.SttModel", ct);
            await ValidateGeminiModelAsync(config.FindProfile(RoutingInput.TtsProfileId), AiCapability.TextToSpeech, RoutingInput.TtsModel, "RoutingInput.TtsModel", ct);
            await ValidateGeminiModelAsync(config.FindProfile(RoutingInput.LlmProfileId), AiCapability.FollowUpLlm, RoutingInput.LlmModel, "RoutingInput.LlmModel", ct);
            await ValidateGeminiModelAsync(config.FindProfile(RoutingInput.GradingProfileId), AiCapability.Grading, RoutingInput.GradingModel, "RoutingInput.GradingModel", ct);
        }

        if (!ModelState.IsValid)
        {
            var submitted = RoutingInput;
            LoadViewModelData();
            RoutingInput = submitted;
            return Page();
        }

        config.UseRealAi = RoutingInput.UseRealAi;

        config.Routing.Stt.ProfileId = RoutingInput.SttProfileId;
        config.Routing.Stt.Model = string.IsNullOrWhiteSpace(RoutingInput.SttModel) ? null : RoutingInput.SttModel.Trim();

        config.Routing.Tts.ProfileId = RoutingInput.TtsProfileId;
        config.Routing.Tts.Model = string.IsNullOrWhiteSpace(RoutingInput.TtsModel) ? null : RoutingInput.TtsModel.Trim();
        config.Routing.Tts.Voice = string.IsNullOrWhiteSpace(RoutingInput.TtsVoice) ? null : RoutingInput.TtsVoice.Trim();

        config.Routing.Llm.ProfileId = RoutingInput.LlmProfileId;
        config.Routing.Llm.Model = string.IsNullOrWhiteSpace(RoutingInput.LlmModel) ? null : RoutingInput.LlmModel.Trim();

        config.Routing.Grading.ProfileId = RoutingInput.GradingProfileId;
        config.Routing.Grading.Model = string.IsNullOrWhiteSpace(RoutingInput.GradingModel) ? null : RoutingInput.GradingModel.Trim();

        await _credentialStore.SaveConfigurationAsync(config, ct);
        TempData["SuccessMessage"] = "Đã lưu thiết lập định tuyến năng lực AI thành công.";
        return RedirectToPage();
    }

    private string DefaultModelFor(string kind, AiCapability capability) =>
        _providerRegistry.GetAdapter(kind)?.GetDefaultModel(capability)
        ?? (string.Equals(kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase)
            ? GeminiModels.DefaultFor(capability)
            : capability switch
            {
                AiCapability.SpeechToText => "whisper-1",
                AiCapability.TextToSpeech => "tts-1",
                _ => "gpt-4o-mini"
            });

    /// <summary>
    /// Vendor-aware default model. <see cref="AiProviderKinds.OpenAiCompatible"/> covers DeepSeek, Groq,
    /// Together, Ollama... so the provider default (always an OpenAI name) would be wrong for them.
    /// </summary>
    private string DefaultModelFor(AiProviderProfile? profile, AiCapability capability, string? kindOverride = null)
    {
        var kind = kindOverride ?? profile?.Kind ?? AiProviderKinds.OpenAi;
        var fallback = DefaultModelFor(kind, capability);
        return AiProviderModelDefaults.Resolve(profile?.Kind ?? kind, profile?.BaseUrl, capability, fallback);
    }

    private static string DefaultVoiceFor(string kind) =>
        string.Equals(kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase) ? GeminiModels.DefaultVoice : "alloy";

    /// <summary>True khi model hiện tại vẫn dùng được cho loại nhà cung cấp mới (không cần đặt lại).</summary>
    private static bool IsModelCompatible(string kind, AiCapability capability, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (string.Equals(kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return IsLikelyGeminiModel(model) && GeminiModels.IsSuitableFor(capability, model);
        }
        return !IsLikelyGeminiModel(model);
    }

    /// <summary>
    /// Với hồ sơ Gemini: model TTS phải là model "tts", STT/LLM/Grading không được là model TTS, và tên model phải có trong
    /// danh sách ListModels thật của hồ sơ (nếu lấy được). Sai thì báo lỗi tại trang kèm gợi ý model hợp lệ gần nhất.
    /// </summary>
    private async Task ValidateGeminiModelAsync(AiProviderProfile? profile, AiCapability capability, string? model, string field, CancellationToken ct)
    {
        if (profile == null || string.IsNullOrWhiteSpace(model) ||
            !string.Equals(profile.Kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var name = model.Trim();
        var capabilityName = capability switch
        {
            AiCapability.SpeechToText => "Nhận diện giọng nói (STT)",
            AiCapability.TextToSpeech => "Đọc câu hỏi (TTS)",
            AiCapability.Grading => "Chấm điểm AI (Grading)",
            _ => "Câu hỏi phụ (LLM)"
        };

        var liveModels = await _connectionTester.TryListLiveModelsAsync(profile, ct);
        var suggestion = GeminiModels.SuggestModel(capability, name, liveModels ?? GeminiModels.KnownModels) ?? GeminiModels.DefaultFor(capability);

        if (!GeminiModels.IsSuitableFor(capability, name))
        {
            var reason = capability == AiCapability.TextToSpeech
                ? "không phải model đọc giọng nói (tên model TTS của Gemini có chữ \"tts\")"
                : "là model đọc giọng nói (TTS), không dùng được cho năng lực này";
            ModelState.AddModelError(field, $"{capabilityName}: model '{name}' {reason}. Gợi ý: '{suggestion}'.");
            return;
        }

        if (liveModels is { Count: > 0 } && !liveModels.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(field,
                $"{capabilityName}: model '{name}' không có trong danh sách model của hồ sơ '{profile.Name}' (theo ListModels). Gợi ý: '{suggestion}'.");
        }
    }

    private static bool IsLikelyOpenAiModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.Trim().ToLowerInvariant();
        return m.StartsWith("whisper") || m.StartsWith("tts-") || m.StartsWith("gpt-") || m.StartsWith("o1-") || m.StartsWith("o3-") || m.StartsWith("text-embedding");
    }

    private static bool IsLikelyGeminiModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        var m = model.Trim().ToLowerInvariant();
        return m.StartsWith("gemini-") || m.StartsWith("models/gemini-");
    }

    public async Task<IActionResult> OnPostTestProfileAsync(string profileId, CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();
        var profile = config.FindProfile(profileId);
        if (profile == null)
        {
            TempData["TestErrorMessage"] = "Không tìm thấy hồ sơ nhà cung cấp để kiểm tra.";
            return RedirectToPage();
        }

        var (ok, message) = await _connectionTester.TestProfileAsync(profile, ct);
        if (ok)
        {
            TempData["TestSuccessMessage"] = $"[{profile.Name}] {message}";
        }
        else
        {
            TempData["TestErrorMessage"] = $"[{profile.Name}] {message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetListModelsAsync(string profileId, CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();
        var profile = config.FindProfile(profileId);
        if (profile == null)
        {
            return new JsonResult(new { success = false, message = "Profile not found." });
        }

        var models = await _connectionTester.ListModelsAsync(profile, ct);
        return new JsonResult(new { success = true, models });
    }

    // Legacy handler maintained for backward compatibility
    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct = default)
    {
        var currentStatus = _credentialStore.GetStatus();

        if (UseRealAi && !currentStatus.HasApiKey && string.IsNullOrWhiteSpace(ApiKey))
        {
            ModelState.AddModelError(nameof(ApiKey), "Vui lòng nhập API key trước khi bật chế độ AI thật.");
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            var trimmed = ApiKey.Trim();
            if (trimmed.Length is < 10 or > 400 || trimmed.Any(char.IsWhiteSpace))
            {
                ModelState.AddModelError(nameof(ApiKey), "API key không hợp lệ.");
            }
        }

        if (!ModelState.IsValid)
        {
            LoadViewModelData();
            return Page();
        }

        try
        {
            await _credentialStore.SaveAsync(ApiKey, UseRealAi, ct);
            TempData["SuccessMessage"] = "Đã lưu cấu hình AI thành công.";
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving AI settings.");
            ModelState.AddModelError(string.Empty, "Đã xảy ra lỗi khi lưu cấu hình AI.");
            LoadViewModelData();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostClearAsync(CancellationToken ct = default)
    {
        await _credentialStore.ClearAsync(ct);
        TempData["SuccessMessage"] = "Đã xoá API key. Hệ thống quay về chế độ mô phỏng (mock).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestAsync(CancellationToken ct = default)
    {
        var (ok, message) = await _connectionTester.TestConnectionAsync(ct);
        if (ok)
        {
            TempData["TestSuccessMessage"] = message;
        }
        else
        {
            TempData["TestErrorMessage"] = message;
        }

        return RedirectToPage();
    }

    private void LoadViewModelData()
    {
        Status = _credentialStore.GetStatus();
        Configuration = _credentialStore.GetConfiguration();
        UseRealAi = Configuration.UseRealAi;
        ApiKey = null;

        DeepSeekProfile = FindDeepSeekProfile(Configuration);
        DeepSeekGradingEnabled = DeepSeekProfile != null &&
            string.Equals(Configuration.Routing.Grading.ProfileId, DeepSeekProfile.Id, StringComparison.OrdinalIgnoreCase);
        DeepSeekLlmEnabled = DeepSeekProfile != null &&
            string.Equals(Configuration.Routing.Llm.ProfileId, DeepSeekProfile.Id, StringComparison.OrdinalIgnoreCase);

        DeepSeekInput = new DeepSeekInputModel
        {
            ApiKey = null,
            Model = DeepSeekModel,
            EnableLlm = DeepSeekProfile?.CustomCapabilities.HasFlag(AiCapability.FollowUpLlm) ?? true,
            EnableGrading = DeepSeekProfile?.CustomCapabilities.HasFlag(AiCapability.Grading) ?? true
        };

        RoutingInput = new RoutingInputModel
        {
            UseRealAi = Configuration.UseRealAi,
            SttProfileId = Configuration.Routing.Stt.ProfileId,
            SttModel = Configuration.Routing.Stt.Model,
            TtsProfileId = Configuration.Routing.Tts.ProfileId,
            TtsModel = Configuration.Routing.Tts.Model,
            TtsVoice = Configuration.Routing.Tts.Voice,
            LlmProfileId = Configuration.Routing.Llm.ProfileId,
            LlmModel = Configuration.Routing.Llm.Model,
            GradingProfileId = Configuration.Routing.Grading.ProfileId,
            GradingModel = Configuration.Routing.Grading.Model
        };
    }

    public class ProfileInputModel
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = AiProviderKinds.OpenAi;
        public string BaseUrl { get; set; } = string.Empty;
        public string? ApiKey { get; set; }
        public string? OrganizationId { get; set; }
        public bool EnableStt { get; set; } = true;
        public bool EnableTts { get; set; } = true;
        public bool EnableLlm { get; set; } = true;
        public bool EnableGrading { get; set; } = true;
    }

    public class DeepSeekInputModel
    {
        /// <summary>API key DeepSeek. Để trống khi đã có key để giữ nguyên.</summary>
        public string? ApiKey { get; set; }
        public string Model { get; set; } = AiProviderModelDefaults.DeepSeekChat;
        public bool EnableLlm { get; set; } = true;
        public bool EnableGrading { get; set; } = true;
    }

    public class RoutingInputModel
    {
        public bool UseRealAi { get; set; }
        public string? SttProfileId { get; set; }
        public string? SttModel { get; set; }
        public string? TtsProfileId { get; set; }
        public string? TtsModel { get; set; }
        public string? TtsVoice { get; set; }
        public string? LlmProfileId { get; set; }
        public string? LlmModel { get; set; }
        public string? GradingProfileId { get; set; }
        public string? GradingModel { get; set; }
    }
}
