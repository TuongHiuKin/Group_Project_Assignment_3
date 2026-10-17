using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
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

    private string? _currentApiKey;
    private bool _useRealAi;
    private DateTime? _updatedAtUtc;

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
        var hasKey = !string.IsNullOrWhiteSpace(_currentApiKey);
        string? keyHint = null;

        if (hasKey && _currentApiKey != null)
        {
            keyHint = _currentApiKey.Length >= 4
                ? "…" + _currentApiKey[^4..]
                : "…" + _currentApiKey;
        }

        return new AiCredentialStatus(hasKey, _useRealAi, keyHint, _updatedAtUtc);
    }

    public string? GetApiKey()
    {
        return _currentApiKey;
    }

    public async Task SaveAsync(string? newApiKey, bool useRealAi, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(newApiKey))
            {
                var trimmed = newApiKey.Trim();
                if (trimmed.Length is < 20 or > 300 || trimmed.Any(char.IsWhiteSpace))
                {
                    throw new ArgumentException("API key must be between 20 and 300 characters without whitespace.", nameof(newApiKey));
                }

                _currentApiKey = trimmed;
            }

            _useRealAi = useRealAi;
            _updatedAtUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(_storePath))
            {
                var dir = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var model = new StoredCredentialModel
                {
                    ProtectedApiKey = !string.IsNullOrWhiteSpace(_currentApiKey)
                        ? _protector.Protect(_currentApiKey)
                        : null,
                    UseRealAi = _useRealAi,
                    UpdatedAtUtc = _updatedAtUtc
                };

                var json = JsonSerializer.Serialize(model, new JsonSerializerOptions
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

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _currentApiKey = null;
            _useRealAi = false;
            _updatedAtUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(_storePath) && File.Exists(_storePath))
            {
                try
                {
                    File.Delete(_storePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete stored AI credential file.");
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
            return;
        }

        try
        {
            var json = File.ReadAllText(_storePath);
            var model = JsonSerializer.Deserialize<StoredCredentialModel>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (model == null)
            {
                return;
            }

            _useRealAi = model.UseRealAi;
            _updatedAtUtc = model.UpdatedAtUtc;

            if (!string.IsNullOrWhiteSpace(model.ProtectedApiKey))
            {
                try
                {
                    _currentApiKey = _protector.Unprotect(model.ProtectedApiKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to unprotect stored AI API key. Treating as unconfigured.");
                    _currentApiKey = null;
                    _useRealAi = false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read or parse stored AI credentials file.");
            _currentApiKey = null;
            _useRealAi = false;
            _updatedAtUtc = null;
        }
    }

    private sealed class StoredCredentialModel
    {
        public string? ProtectedApiKey { get; set; }
        public bool UseRealAi { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
    }
}
