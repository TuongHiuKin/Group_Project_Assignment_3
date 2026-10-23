using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Ai;

public sealed class ProtectedAiCredentialStore : IAiCredentialStore
{
    private const string Purpose = "AIVES.AiCredentials.v1";
    private readonly IDataProtector _protector;
    private readonly string? _storePath;
    private readonly ILogger<ProtectedAiCredentialStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private AiConfigurationModel _configuration = CreateDefaultConfiguration();
    private DateTime _lastFileWriteUtc = DateTime.MinValue;

    private void EnsureLatestCredentials()
    {
        if (string.IsNullOrWhiteSpace(_storePath) || !File.Exists(_storePath)) return;
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(_storePath);
            if (lastWrite > _lastFileWriteUtc)
            {
                LoadInitialCredentials();
            }
        }
        catch { }
    }

    public ProtectedAiCredentialStore(
        IDataProtectionProvider protectionProvider,
        IOptions<AiServiceOptions> options,
        ILogger<ProtectedAiCredentialStore> logger)
    {
        ArgumentNullException.ThrowIfNull(protectionProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _protector = protectionProvider.CreateProtector(Purpose);
        _storePath = options.Value.CredentialStorePath;
        _logger = logger;

        LoadInitialCredentials();
    }

    public AiCredentialStatus GetStatus()
    {
        EnsureLatestCredentials();
        var hasKey = _configuration.Profiles.Any(p => !string.IsNullOrWhiteSpace(p.ApiKey));
        string? keyHint = null;

        EnsureLatestCredentials();
        var primaryProfile = _configuration.FindProfile(_configuration.Routing.Llm.ProfileId)
            ?? _configuration.Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ApiKey));

        if (primaryProfile != null && !string.IsNullOrWhiteSpace(primaryProfile.ApiKey))
        {
            keyHint = primaryProfile.GetKeyHint();
        }

        return new AiCredentialStatus(hasKey, _configuration.UseRealAi, keyHint, _configuration.UpdatedAtUtc);
    }

    public string? GetApiKey()
    {
        EnsureLatestCredentials();
        var primaryProfile = _configuration.FindProfile(_configuration.Routing.Llm.ProfileId)
            ?? _configuration.Profiles.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.ApiKey));
        return primaryProfile?.ApiKey;
    }

    public AiConfigurationModel GetConfiguration()
    {
        EnsureLatestCredentials();
        return CloneConfiguration(_configuration);
    }

    public async Task SaveConfigurationAsync(AiConfigurationModel config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            config.UpdatedAtUtc = DateTime.UtcNow;
            _configuration = CloneConfiguration(config);

            if (!string.IsNullOrWhiteSpace(_storePath))
            {
                var dir = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var storedModel = new StoredConfigurationModel
                {
                    SchemaVersion = 2,
                    UseRealAi = _configuration.UseRealAi,
                    UpdatedAtUtc = _configuration.UpdatedAtUtc,
                    Profiles = _configuration.Profiles.Select(p => new StoredProfileModel
                    {
                        Id = p.Id,
                        Name = p.Name,
                        Kind = p.Kind,
                        BaseUrl = p.BaseUrl,
                        OrganizationId = p.OrganizationId,
                        CustomCapabilities = (int)p.CustomCapabilities,
                        ProtectedApiKey = !string.IsNullOrWhiteSpace(p.ApiKey) ? _protector.Protect(p.ApiKey) : null
                    }).ToList(),
                    Routing = new StoredRoutingModel
                    {
                        Stt = new StoredTargetModel { ProfileId = _configuration.Routing.Stt.ProfileId, Model = _configuration.Routing.Stt.Model },
                        Tts = new StoredTtsTargetModel { ProfileId = _configuration.Routing.Tts.ProfileId, Model = _configuration.Routing.Tts.Model, Voice = _configuration.Routing.Tts.Voice },
                        Llm = new StoredTargetModel { ProfileId = _configuration.Routing.Llm.ProfileId, Model = _configuration.Routing.Llm.Model }
                    }
                };

                var json = JsonSerializer.Serialize(storedModel, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });

                var tmpPath = _storePath + ".tmp";
                await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
                File.Move(tmpPath, _storePath, overwrite: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string? newApiKey, bool useRealAi, CancellationToken ct = default)
    {
        var config = GetConfiguration();
        config.UseRealAi = useRealAi;

        var defaultProfile = config.Profiles.FirstOrDefault(p => string.Equals(p.Kind, AiProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase))
            ?? config.Profiles.FirstOrDefault();

        if (defaultProfile == null)
        {
            defaultProfile = new AiProviderProfile
            {
                Id = "openai-default",
                Name = "OpenAI",
                Kind = AiProviderKinds.OpenAi,
                BaseUrl = "https://api.openai.com/v1"
            };
            config.Profiles.Add(defaultProfile);
        }

        if (!string.IsNullOrWhiteSpace(newApiKey))
        {
            var trimmed = newApiKey.Trim();
            if (trimmed.Length is < 20 or > 300 || trimmed.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException("API key must be between 20 and 300 characters without whitespace.", nameof(newApiKey));
            }
            defaultProfile.ApiKey = trimmed;
        }

        await SaveConfigurationAsync(config, ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var profile in _configuration.Profiles)
            {
                profile.ApiKey = null;
            }
            _configuration.UseRealAi = false;
            _configuration.UpdatedAtUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(_storePath) && File.Exists(_storePath))
            {
                try
                {
                    File.Delete(_storePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete stored AI credential file. Overwriting with empty configuration.");
                    var emptyModel = new StoredConfigurationModel
                    {
                        SchemaVersion = 2,
                        UseRealAi = false,
                        UpdatedAtUtc = _configuration.UpdatedAtUtc,
                        Profiles = [],
                        Routing = new StoredRoutingModel()
                    };
                    var json = JsonSerializer.Serialize(emptyModel, new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                    var tmpPath = _storePath + ".tmp";
                    await File.WriteAllTextAsync(tmpPath, json, ct).ConfigureAwait(false);
                    File.Move(tmpPath, _storePath, overwrite: true);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void LoadInitialCredentials()
    {
        if (string.IsNullOrWhiteSpace(_storePath) || !File.Exists(_storePath))
        {
            _configuration = CreateDefaultConfiguration();
            return;
        }

        try
        {
            _lastFileWriteUtc = File.GetLastWriteTimeUtc(_storePath);
            var json = File.ReadAllText(_storePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if this is Schema v2 (has "schemaVersion" >= 2 or "profiles")
            if (root.TryGetProperty("schemaVersion", out var versionElem) && versionElem.GetInt32() >= 2
                || root.TryGetProperty("profiles", out _))
            {
                var stored = JsonSerializer.Deserialize<StoredConfigurationModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (stored != null)
                {
                    var loadedConfig = new AiConfigurationModel
                    {
                        SchemaVersion = stored.SchemaVersion,
                        UseRealAi = stored.UseRealAi,
                        UpdatedAtUtc = stored.UpdatedAtUtc,
                        Routing = new AiCapabilityRouting
                        {
                            Stt = new AiCapabilityTarget { ProfileId = stored.Routing.Stt?.ProfileId, Model = stored.Routing.Stt?.Model },
                            Tts = new AiTtsCapabilityTarget { ProfileId = stored.Routing.Tts?.ProfileId, Model = stored.Routing.Tts?.Model, Voice = stored.Routing.Tts?.Voice },
                            Llm = new AiCapabilityTarget { ProfileId = stored.Routing.Llm?.ProfileId, Model = stored.Routing.Llm?.Model }
                        }
                    };

                    foreach (var p in stored.Profiles)
                    {
                        string? unprotectKey = null;
                        if (!string.IsNullOrWhiteSpace(p.ProtectedApiKey))
                        {
                            try
                            {
                                unprotectKey = _protector.Unprotect(p.ProtectedApiKey);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to unprotect API key for profile {ProfileId}", p.Id);
                            }
                        }

                        loadedConfig.Profiles.Add(new AiProviderProfile
                        {
                            Id = p.Id,
                            Name = p.Name,
                            Kind = p.Kind,
                            BaseUrl = p.BaseUrl,
                            ApiKey = unprotectKey,
                            OrganizationId = p.OrganizationId,
                            CustomCapabilities = (AiCapability)p.CustomCapabilities
                        });
                    }

                    // Fix-up for any legacy migration where an OpenAI profile inadvertently held a Google Gemini key (AIzaSy)
                    var geminiProfiles = loadedConfig.Profiles
                        .Where(p => string.Equals(p.Kind, AiProviderKinds.Gemini, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    foreach (var p in loadedConfig.Profiles)
                    {
                        if (string.Equals(p.Kind, AiProviderKinds.OpenAi, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(p.ApiKey)
                            && p.ApiKey.StartsWith("AIzaSy", StringComparison.Ordinal))
                        {
                            if (geminiProfiles.Count > 0)
                            {
                                p.ApiKey = null;
                            }
                            else
                            {
                                p.Kind = AiProviderKinds.Gemini;
                                p.Name = "Google Gemini";
                                p.BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
                            }
                        }
                    }

                    _configuration = loadedConfig;
                    return;
                }
            }

            // Legacy v1 schema: { protectedApiKey, useRealAi, updatedAtUtc }
            var legacy = JsonSerializer.Deserialize<StoredLegacyCredentialModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (legacy != null)
            {
                string? legacyKey = null;
                if (!string.IsNullOrWhiteSpace(legacy.ProtectedApiKey))
                {
                    try
                    {
                        legacyKey = _protector.Unprotect(legacy.ProtectedApiKey);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to unprotect legacy AI API key.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(legacyKey) && legacyKey.StartsWith("AIzaSy", StringComparison.Ordinal))
                {
                    _configuration = new AiConfigurationModel
                    {
                        SchemaVersion = 2,
                        UseRealAi = legacy.UseRealAi,
                        UpdatedAtUtc = legacy.UpdatedAtUtc,
                        Profiles =
                        [
                            new AiProviderProfile
                            {
                                Id = "gemini-default",
                                Name = "Google Gemini",
                                Kind = AiProviderKinds.Gemini,
                                BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
                                ApiKey = legacyKey,
                                CustomCapabilities = AiCapability.All
                            }
                        ],
                        Routing = new AiCapabilityRouting
                        {
                            Stt = new AiCapabilityTarget { ProfileId = "gemini-default", Model = "gemini-1.5-flash" },
                            Tts = new AiTtsCapabilityTarget { ProfileId = "gemini-default", Model = "gemini-2.0-flash", Voice = "Puck" },
                            Llm = new AiCapabilityTarget { ProfileId = "gemini-default", Model = "gemini-1.5-flash" }
                        }
                    };
                }
                else
                {
                    _configuration = CreateDefaultConfiguration();
                    _configuration.UseRealAi = legacy.UseRealAi;
                    _configuration.UpdatedAtUtc = legacy.UpdatedAtUtc;

                    var defaultProfile = _configuration.Profiles.FirstOrDefault();
                    if (defaultProfile != null)
                    {
                        defaultProfile.ApiKey = legacyKey;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read or parse stored AI credentials file. Initializing defaults.");
            _configuration = CreateDefaultConfiguration();
        }
    }

    private static AiConfigurationModel CreateDefaultConfiguration()
    {
        return new AiConfigurationModel
        {
            SchemaVersion = 2,
            UseRealAi = false,
            UpdatedAtUtc = null,
            Profiles =
            [
                new AiProviderProfile
                {
                    Id = "openai-default",
                    Name = "OpenAI",
                    Kind = AiProviderKinds.OpenAi,
                    BaseUrl = "https://api.openai.com/v1",
                    ApiKey = null,
                    CustomCapabilities = AiCapability.All
                }
            ],
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = "openai-default", Model = "whisper-1" },
                Tts = new AiTtsCapabilityTarget { ProfileId = "openai-default", Model = "tts-1", Voice = "alloy" },
                Llm = new AiCapabilityTarget { ProfileId = "openai-default", Model = "gpt-4o-mini" }
            }
        };
    }

    private static AiConfigurationModel CloneConfiguration(AiConfigurationModel source)
    {
        return new AiConfigurationModel
        {
            SchemaVersion = source.SchemaVersion,
            UseRealAi = source.UseRealAi,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Profiles = source.Profiles.Select(p => new AiProviderProfile
            {
                Id = p.Id,
                Name = p.Name,
                Kind = p.Kind,
                BaseUrl = p.BaseUrl,
                ApiKey = p.ApiKey,
                OrganizationId = p.OrganizationId,
                CustomCapabilities = p.CustomCapabilities
            }).ToList(),
            Routing = new AiCapabilityRouting
            {
                Stt = new AiCapabilityTarget { ProfileId = source.Routing.Stt.ProfileId, Model = source.Routing.Stt.Model },
                Tts = new AiTtsCapabilityTarget { ProfileId = source.Routing.Tts.ProfileId, Model = source.Routing.Tts.Model, Voice = source.Routing.Tts.Voice },
                Llm = new AiCapabilityTarget { ProfileId = source.Routing.Llm.ProfileId, Model = source.Routing.Llm.Model }
            }
        };
    }

    private sealed class StoredConfigurationModel
    {
        public int SchemaVersion { get; set; } = 2;
        public bool UseRealAi { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
        public List<StoredProfileModel> Profiles { get; set; } = [];
        public StoredRoutingModel Routing { get; set; } = new();
    }

    private sealed class StoredProfileModel
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string? ProtectedApiKey { get; set; }
        public string? OrganizationId { get; set; }
        public int CustomCapabilities { get; set; } = (int)AiCapability.All;
    }

    private sealed class StoredRoutingModel
    {
        public StoredTargetModel Stt { get; set; } = new();
        public StoredTtsTargetModel Tts { get; set; } = new();
        public StoredTargetModel Llm { get; set; } = new();
    }

    private sealed class StoredTargetModel
    {
        public string? ProfileId { get; set; }
        public string? Model { get; set; }
    }

    private sealed class StoredTtsTargetModel
    {
        public string? ProfileId { get; set; }
        public string? Model { get; set; }
        public string? Voice { get; set; }
    }

    private sealed class StoredLegacyCredentialModel
    {
        public string? ProtectedApiKey { get; set; }
        public bool UseRealAi { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
    }
}
