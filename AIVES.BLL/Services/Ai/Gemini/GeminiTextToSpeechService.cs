using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.Ai.Gemini;

/// <summary>
/// Text-to-Speech service adapter using Google Gemini generateContent REST API with AUDIO response modalities.
/// Wraps 24 kHz 16-bit mono raw PCM into a standard WAV audio container and caches to disk.
/// </summary>
public sealed class GeminiTextToSpeechService : ITextToSpeechService
{
    private readonly HttpClient _httpClient;
    private readonly AiProviderProfile _profile;
    private readonly string _model;
    private readonly string _voice;
    private readonly string? _cacheDirectory;
    private readonly int _timeoutSeconds;
    private readonly ITextToSpeechService? _fallbackService;
    private readonly ILogger<GeminiTextToSpeechService>? _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public GeminiTextToSpeechService(
        HttpClient httpClient,
        AiProviderProfile profile,
        string? model = null,
        string? voice = null,
        string? cacheDirectory = null,
        int timeoutSeconds = 15,
        ILogger<GeminiTextToSpeechService>? logger = null,
        ITextToSpeechService? fallbackService = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _retryDelays = retryDelays ?? GeminiRetryPolicy.DefaultDelays;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _model = string.IsNullOrWhiteSpace(model) ? GeminiModels.DefaultTextToSpeech : model;
        _voice = string.IsNullOrWhiteSpace(voice) ? "Puck" : voice;
        _cacheDirectory = cacheDirectory;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 10;
        _logger = logger;
        _fallbackService = fallbackService;
    }

    public async Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger?.LogWarning("Gemini TTS received null or empty text.");
            return new TextToSpeechResult(false, null, null, "Text to synthesize cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(_profile.ApiKey))
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback TTS service (Gemini API key is not configured).");
                return await _fallbackService.SynthesizeAsync(text, voiceLocale, ct).ConfigureAwait(false);
            }
            return new TextToSpeechResult(false, null, null, "Gemini API key is not configured.");
        }

        // Cache key and relative URL (using .wav extension for PCM wrapped in RIFF/WAVE header)
        var hash = ComputeHash($"gemini:{_voice}:{_model}:{text.Trim()}");
        var relativeUrl = $"/audio/cache/tts-{hash}.wav";

        if (!string.IsNullOrWhiteSpace(_cacheDirectory))
        {
            var cachedFilePath = Path.Combine(_cacheDirectory, $"tts-{hash}.wav");
            if (File.Exists(cachedFilePath))
            {
                try
                {
                    var cachedBytes = await File.ReadAllBytesAsync(cachedFilePath, ct).ConfigureAwait(false);
                    if (cachedBytes.Length > WavHeaderHelper.HeaderSizeBytes)
                    {
                        _logger?.LogInformation("Gemini TTS served audio from disk cache: {Url} ({Bytes} bytes)",
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var baseUrl = string.IsNullOrWhiteSpace(_profile.BaseUrl)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : _profile.BaseUrl.TrimEnd('/');

        // Critical: API key passed in header x-goog-api-key, never in query string
        var endpoint = $"{baseUrl}/models/{_model}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", _profile.ApiKey);

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text }
                    }
                }
            },
            generationConfig = new
            {
                responseModalities = new[] { "AUDIO" },
                speechConfig = new
                {
                    voiceConfig = new
                    {
                        prebuiltVoiceConfig = new
                        {
                            voiceName = _voice
                        }
                    }
                }
            }
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await GeminiRetryPolicy.SendAsync(_httpClient, request, _retryDelays, _logger, "Gemini TTS", linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger?.LogInformation("Gemini TTS canceled by caller.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Gemini TTS timed out after {Timeout} seconds.", _timeoutSeconds);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Gemini TTS timed out. Falling back to mock TTS.");
                return await _fallbackService.SynthesizeAsync(text, voiceLocale, ct).ConfigureAwait(false);
            }
            return new TextToSpeechResult(false, null, null, $"Gemini TTS operation timed out after {_timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            var sanitizedErr = AiKeySanitizer.Sanitize(ex.Message, _profile.ApiKey);
            _logger?.LogError(ex, "Gemini TTS network exception: {Error}", sanitizedErr);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Gemini TTS exception. Falling back to mock TTS.");
                return await _fallbackService.SynthesizeAsync(text, voiceLocale, ct).ConfigureAwait(false);
            }
            return new TextToSpeechResult(false, null, null, $"Network error contacting Gemini TTS: {sanitizedErr}");
        }

        sw.Stop();
        var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, _profile.ApiKey);
            var errorMsg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            _logger?.LogWarning("Gemini TTS failed with status {StatusCode} in {Elapsed}ms: {Error}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorMsg);

            if (_fallbackService != null)
            {
                _logger?.LogInformation("Falling back to mock TTS service due to Gemini API failure.");
                return await _fallbackService.SynthesizeAsync(text, voiceLocale, ct).ConfigureAwait(false);
            }

            return new TextToSpeechResult(false, null, null, $"Gemini TTS error: {errorMsg}");
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var contentElem) &&
                    contentElem.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("inlineData", out var inlineData) &&
                            inlineData.TryGetProperty("data", out var dataElem))
                        {
                            var base64Pcm = dataElem.GetString();
                            if (!string.IsNullOrWhiteSpace(base64Pcm))
                            {
                                var rawPcmBytes = Convert.FromBase64String(base64Pcm);

                                // Gemini provides 24,000 Hz, 16-bit mono PCM. Wrap into standard RIFF/WAVE header.
                                var wavBytes = WavHeaderHelper.WrapPcm16(rawPcmBytes, sampleRate: 24000, channels: 1, bitsPerSample: 16);

                                if (!string.IsNullOrWhiteSpace(_cacheDirectory))
                                {
                                    try
                                    {
                                        Directory.CreateDirectory(_cacheDirectory);
                                        var targetPath = Path.Combine(_cacheDirectory, $"tts-{hash}.wav");
                                        await File.WriteAllBytesAsync(targetPath, wavBytes, ct).ConfigureAwait(false);
                                    }
                                    catch (Exception cacheEx) when (cacheEx is not OperationCanceledException)
                                    {
                                        _logger?.LogWarning(cacheEx, "Failed to write WAV audio file to cache directory.");
                                    }
                                }

                                _logger?.LogInformation("Gemini TTS succeeded in {Elapsed}ms. Audio bytes: {Bytes}",
                                    sw.ElapsedMilliseconds, wavBytes.Length);
                                return new TextToSpeechResult(true, relativeUrl, wavBytes);
                            }
                        }
                    }
                }
            }

            _logger?.LogWarning("Gemini TTS response had no audio inlineData.");
            if (_fallbackService != null)
            {
                return await _fallbackService.SynthesizeAsync(text, voiceLocale, ct).ConfigureAwait(false);
            }
            return new TextToSpeechResult(false, null, null, "Gemini did not return any audio data.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse Gemini TTS JSON response.");
            if (_fallbackService != null)
            {
                return await _fallbackService.SynthesizeAsync(text, voiceLocale, ct).ConfigureAwait(false);
            }
            return new TextToSpeechResult(false, null, null, "Failed to parse Gemini TTS response.");
        }
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private static string? ExtractErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var errorElem))
            {
                if (errorElem.TryGetProperty("message", out var msgElem))
                {
                    return msgElem.GetString();
                }
            }
        }
        catch { }
        return null;
    }
}
