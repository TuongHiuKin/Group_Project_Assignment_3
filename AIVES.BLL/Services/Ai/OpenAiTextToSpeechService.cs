using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Text-to-Speech service adapter using OpenAI Audio Speech REST API.
/// Enforces 10-second timeout, disk-caching to wwwroot/audio/cache, and graceful fallback.
/// </summary>
public class OpenAiTextToSpeechService : ITextToSpeechService
{
    private const string DefaultEndpoint = "https://api.openai.com/v1/audio/speech";
    private readonly HttpClient _httpClient;
    private readonly IOptions<AiServiceOptions> _options;
    private readonly ITextToSpeechService? _fallbackService;
    private readonly ILogger<OpenAiTextToSpeechService>? _logger;
    private readonly string? _cacheDirectory;

    public OpenAiTextToSpeechService(
        HttpClient httpClient,
        IOptions<AiServiceOptions> options,
        ILogger<OpenAiTextToSpeechService>? logger = null,
        ITextToSpeechService? fallbackService = null,
        string? cacheDirectory = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _fallbackService = fallbackService;
        _cacheDirectory = cacheDirectory;
    }

    /// <inheritdoc />
    public async Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger?.LogWarning("TTS received empty text input.");
            return new TextToSpeechResult(false, null, null, "Text to synthesize cannot be empty.");
        }

        var config = _options.Value;

        // Fallback to mock if configured or if no API key is available
        if (config.UseMock || string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback TTS service (UseMock: {UseMock}, HasKey: {HasKey})",
                    config.UseMock, !string.IsNullOrWhiteSpace(config.OpenAI.ApiKey));
                return await _fallbackService.SynthesizeAsync(text, voiceLocale, ct);
            }

            if (string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
            {
                _logger?.LogWarning("OpenAI TTS failed: API key is not configured.");
                return new TextToSpeechResult(false, null, null, "OpenAI API key is not configured.");
            }
        }

        // Generate deterministic cache key and URL
        var hash = ComputeHash($"{voiceLocale}:{text.Trim()}");
        var relativeUrl = $"/audio/cache/tts-{hash}.mp3";

        // Check local disk cache if configured
        if (!string.IsNullOrWhiteSpace(_cacheDirectory))
        {
            var cachedFilePath = Path.Combine(_cacheDirectory, $"tts-{hash}.mp3");
            if (File.Exists(cachedFilePath))
            {
                try
                {
                    var cachedBytes = await File.ReadAllBytesAsync(cachedFilePath, ct);
                    if (cachedBytes.Length > 0)
                    {
                        _logger?.LogInformation("TTS served audio from disk cache: {Url} ({Bytes} bytes)",
                            relativeUrl, cachedBytes.Length);
                        return new TextToSpeechResult(true, relativeUrl, cachedBytes);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogWarning(ex, "Failed to read cached TTS file, proceeding with API call.");
                }
            }
        }

        var timeoutSeconds = config.TextToSpeechTimeoutSeconds > 0 ? config.TextToSpeechTimeoutSeconds : 10;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var endpoint = string.IsNullOrWhiteSpace(config.OpenAI.BaseUrl)
            ? DefaultEndpoint
            : $"{config.OpenAI.BaseUrl.TrimEnd('/')}/audio/speech";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAI.ApiKey);
        if (!string.IsNullOrWhiteSpace(config.OpenAI.OrganizationId))
        {
            request.Headers.Add("OpenAI-Organization", config.OpenAI.OrganizationId);
        }

        var payload = new
        {
            model = config.OpenAI.TtsModel ?? "tts-1",
            input = text,
            voice = config.OpenAI.TtsVoice ?? "alloy",
            response_format = "mp3"
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger?.LogInformation("TTS operation canceled by caller.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("TTS synthesis timed out after {Timeout} seconds.", timeoutSeconds);
            return new TextToSpeechResult(false, null, null, $"TTS operation timed out after {timeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "HTTP exception during TTS synthesis.");
            return new TextToSpeechResult(false, null, null, $"Network error contacting TTS service: {ex.Message}");
        }

        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var errorMsg = ExtractErrorMessage(errorBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            _logger?.LogWarning("OpenAI TTS failed with status {StatusCode} in {Elapsed}ms: {Error}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorMsg);
            return new TextToSpeechResult(false, null, null, $"TTS service error: {errorMsg}");
        }

        var audioBytes = await response.Content.ReadAsByteArrayAsync(ct);

        // Save to disk cache if cache directory is configured
        if (!string.IsNullOrWhiteSpace(_cacheDirectory) && audioBytes.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                var cachedFilePath = Path.Combine(_cacheDirectory, $"tts-{hash}.mp3");
                await File.WriteAllBytesAsync(cachedFilePath, audioBytes, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogWarning(ex, "Failed to save synthesized TTS audio to disk cache.");
            }
        }

        _logger?.LogInformation("OpenAI TTS synthesized {Length} chars into {Bytes} bytes ({Elapsed}ms) -> {Url}",
            text.Length, audioBytes.Length, sw.ElapsedMilliseconds, relativeUrl);

        return new TextToSpeechResult(
            IsSuccess: true,
            AudioUrl: relativeUrl,
            AudioData: audioBytes
        );
    }

    private static string ComputeHash(string input)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string? ExtractErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                if (errorObj.TryGetProperty("message", out var msgProp))
                {
                    return msgProp.GetString();
                }
            }
        }
        catch
        {
            // Ignore parse errors on raw error content
        }

        return null;
    }
}
