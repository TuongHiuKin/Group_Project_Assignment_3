using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Ai;

public sealed class OpenAiConnectionTester : IAiConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly IAiCredentialStore _credentialStore;
    private readonly IAiProviderRegistry _registry;
    private readonly IOptionsMonitor<AiServiceOptions> _optionsMonitor;
    private readonly ILogger<OpenAiConnectionTester> _logger;

    [ActivatorUtilitiesConstructor]
    public OpenAiConnectionTester(
        HttpClient httpClient,
        IAiCredentialStore credentialStore,
        IAiProviderRegistry registry,
        IOptionsMonitor<AiServiceOptions> optionsMonitor,
        ILogger<OpenAiConnectionTester> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _optionsMonitor = optionsMonitor ?? throw new ArgumentNullException(nameof(optionsMonitor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public OpenAiConnectionTester(
        HttpClient httpClient,
        IAiCredentialStore credentialStore,
        IOptionsMonitor<AiServiceOptions> optionsMonitor,
        ILogger<OpenAiConnectionTester> logger)
        : this(httpClient, credentialStore, new AiProviderRegistry([new OpenAiProviderAdapter(), new GeminiProviderAdapter(), new OpenAiCompatibleProviderAdapter()]), optionsMonitor, logger)
    {
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var config = _credentialStore.GetConfiguration();
        var primaryProfile = config.FindProfile(config.Routing.Llm.ProfileId)
            ?? config.Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ApiKey))
            ?? config.Profiles.FirstOrDefault();

        if (primaryProfile == null)
        {
            // Fallback to legacy configuration
            var legacyKey = _credentialStore.GetApiKey();
            if (string.IsNullOrWhiteSpace(legacyKey))
            {
                legacyKey = _optionsMonitor.CurrentValue.OpenAI.ApiKey;
            }

            if (string.IsNullOrWhiteSpace(legacyKey))
            {
                return (false, "Chưa cấu hình nhà cung cấp AI hoặc API key.");
            }

            primaryProfile = new AiProviderProfile
            {
                Id = "openai-fallback",
                Name = "OpenAI",
                Kind = AiProviderKinds.OpenAi,
                BaseUrl = _optionsMonitor.CurrentValue.OpenAI.BaseUrl ?? "https://api.openai.com/v1",
                ApiKey = legacyKey
            };
        }

        return await TestProfileAsync(primaryProfile, ct).ConfigureAwait(false);
    }

    public async Task<(bool Ok, string Message)> TestProfileAsync(AiProviderProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var adapter = _registry.GetAdapter(profile.Kind);
        if (adapter == null)
        {
            return (false, $"Không tìm thấy adapter hỗ trợ loại nhà cung cấp '{profile.Kind}'.");
        }

        try
        {
            return await adapter.TestConnectionAsync(_httpClient, profile, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error testing connection for profile {ProfileId}", profile.Id);
            var sanitized = AiKeySanitizer.Sanitize(ex.Message, profile.ApiKey);
            return (false, $"Lỗi không xác định khi kiểm tra kết nối: {sanitized}");
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(AiProviderProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var adapter = _registry.GetAdapter(profile.Kind);
        if (adapter == null)
        {
            return Array.Empty<string>();
        }

        try
        {
            return await adapter.ListModelsAsync(_httpClient, profile, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list models for profile {ProfileId}", profile.Id);
            return Array.Empty<string>();
        }
    }
}
