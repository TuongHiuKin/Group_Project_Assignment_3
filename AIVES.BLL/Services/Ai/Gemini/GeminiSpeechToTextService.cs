using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.Ai.Gemini;

/// <summary>
/// Speech-to-Text service adapter using Google Gemini generateContent REST API with inline audio data.
/// </summary>
public sealed class GeminiSpeechToTextService : ISpeechToTextService
{
    private readonly HttpClient _httpClient;
    private readonly AiProviderProfile _profile;
    private readonly string _model;
    private readonly int _timeoutSeconds;
    private readonly ISpeechToTextService? _fallbackService;
    private readonly ILogger<GeminiSpeechToTextService>? _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public GeminiSpeechToTextService(
        HttpClient httpClient,
        AiProviderProfile profile,
        string? model = null,
        int timeoutSeconds = 35,
        ILogger<GeminiSpeechToTextService>? logger = null,
        ISpeechToTextService? fallbackService = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _retryDelays = retryDelays ?? GeminiRetryPolicy.DefaultDelays;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _model = string.IsNullOrWhiteSpace(model) ? GeminiModels.DefaultSpeechToText : model;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 35;
        _logger = logger;
        _fallbackService = fallbackService;
    }

    public async Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (audioStream == null)
        {
            _logger?.LogWarning("Gemini STT received a null audio stream.");
            return new SpeechToTextResult(false, string.Empty, 0m, "Audio stream cannot be null.");
        }

        if (audioStream.CanSeek && audioStream.Length == 0)
        {
            _logger?.LogWarning("Gemini STT received an empty audio stream.");
            return new SpeechToTextResult(false, string.Empty, 0m, "Audio stream is empty.");
        }

        if (string.IsNullOrWhiteSpace(_profile.ApiKey))
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback STT service (Gemini API key is not configured).");
                return await _fallbackService.TranscribeAsync(audioStream, contentType, ct).ConfigureAwait(false);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, "Chưa cấu hình API key cho Gemini (API key is not configured).");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        // Read audio payload and encode to base64
        byte[] audioBytes;
        if (audioStream is MemoryStream ms)
        {
            audioBytes = ms.ToArray();
        }
        else
        {
            using var copyMs = new MemoryStream();
            await audioStream.CopyToAsync(copyMs, linkedCts.Token).ConfigureAwait(false);
            audioBytes = copyMs.ToArray();
        }

        // Clean MIME type: remove codec parameters (e.g. "audio/webm;codecs=opus" -> "audio/webm")
        var cleanMimeType = (string.IsNullOrWhiteSpace(contentType) ? "audio/webm" : contentType).Split(';')[0].Trim();
        var base64Audio = Convert.ToBase64String(audioBytes);

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
                    parts = new object[]
                    {
                        new
                        {
                            inline_data = new
                            {
                                mime_type = cleanMimeType,
                                data = base64Audio
                            }
                        },
                        new
                        {
                            text = "Chuyển âm thanh tiếng Việt trong đoạn ghi âm thành văn bản (Speech to text). Trả về chính xác những từ người nói đã phát âm (kể cả từ ngắn như 'alo', 'xin chào'). Chỉ trả về văn bản đã nhận diện, không giải thích."
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
            response = await GeminiRetryPolicy.SendAsync(_httpClient, request, _retryDelays, _logger, "Gemini STT", linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger?.LogInformation("Gemini STT canceled by caller.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Gemini STT timed out after {Timeout} seconds.", _timeoutSeconds);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Gemini STT timed out. Falling back to mock STT.");
                using var fallbackStream = new MemoryStream(audioBytes);
                return await _fallbackService.TranscribeAsync(fallbackStream, contentType, ct).ConfigureAwait(false);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, $"Quá thời gian chờ nhận diện giọng nói Gemini sau {_timeoutSeconds} giây (timed out).");
        }
        catch (Exception ex)
        {
            var sanitizedErr = AiKeySanitizer.Sanitize(ex.Message, _profile.ApiKey);
            _logger?.LogError(ex, "Gemini STT network exception: {Error}", sanitizedErr);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Gemini STT exception. Falling back to mock STT.");
                using var fallbackStream = new MemoryStream(audioBytes);
                return await _fallbackService.TranscribeAsync(fallbackStream, contentType, ct).ConfigureAwait(false);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, $"Lỗi kết nối mạng tới Gemini STT: {sanitizedErr}");
        }

        sw.Stop();
        var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, _profile.ApiKey);
            var errorMsg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            _logger?.LogWarning("Gemini STT failed with status {StatusCode} in {Elapsed}ms: {Error}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorMsg);

            if (_fallbackService != null)
            {
                _logger?.LogInformation("Falling back to mock STT service due to Gemini API failure.");
                using var fallbackStream = new MemoryStream(audioBytes);
                return await _fallbackService.TranscribeAsync(fallbackStream, contentType, ct).ConfigureAwait(false);
            }

            return new SpeechToTextResult(false, string.Empty, 0m, $"Lỗi nhận diện giọng nói Gemini (HTTP {(int)response.StatusCode}): {errorMsg}");
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
                    var textPart = parts[0];
                    if (textPart.TryGetProperty("text", out var textElem))
                    {
                        var transcribedText = textElem.GetString()?.Trim() ?? string.Empty;

                        // Gemini API does not return a speech confidence score; default to 0.90m as documented in issue requirements
                        const decimal geminiDefaultConfidence = 0.90m;

                        _logger?.LogInformation("Gemini STT succeeded in {Elapsed}ms. Length: {Len}",
                            sw.ElapsedMilliseconds, transcribedText.Length);
                        return new SpeechToTextResult(true, transcribedText, geminiDefaultConfidence);
                    }
                }
            }

            _logger?.LogWarning("Gemini STT response had no valid text candidate in response body.");
            if (_fallbackService != null)
            {
                using var fallbackStream = new MemoryStream(audioBytes);
                return await _fallbackService.TranscribeAsync(fallbackStream, contentType, ct).ConfigureAwait(false);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, "Gemini did not return any transcription text.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse Gemini STT JSON response.");
            if (_fallbackService != null)
            {
                using var fallbackStream = new MemoryStream(audioBytes);
                return await _fallbackService.TranscribeAsync(fallbackStream, contentType, ct).ConfigureAwait(false);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, "Failed to parse Gemini STT response.");
        }
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
        catch
        {
            // Not valid JSON
        }
        return null;
    }
}
