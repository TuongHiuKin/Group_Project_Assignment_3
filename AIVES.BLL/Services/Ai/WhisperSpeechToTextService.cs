using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Speech-to-Text service adapter using OpenAI Whisper or OpenAI-compatible audio/transcriptions REST API.
/// Enforces timeout, cancellation propagation, multipart form upload, and graceful fallback.
/// </summary>
public class WhisperSpeechToTextService : ISpeechToTextService
{
    private const string DefaultEndpoint = "https://api.openai.com/v1/audio/transcriptions";
    public const string NoSpeechDetectedMessage =
        "Không nghe thấy câu trả lời (bản ghi chỉ có khoảng lặng hoặc tạp âm). Vui lòng nói rõ hơn và ghi âm lại.";
    private readonly HttpClient _httpClient;
    private readonly IOptions<AiServiceOptions> _options;
    private readonly ISpeechToTextService? _fallbackService;
    private readonly ILogger<WhisperSpeechToTextService>? _logger;

    public WhisperSpeechToTextService(
        HttpClient httpClient,
        IOptions<AiServiceOptions> options,
        ILogger<WhisperSpeechToTextService>? logger = null,
        ISpeechToTextService? fallbackService = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _fallbackService = fallbackService;
    }

    /// <inheritdoc />
    public async Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (audioStream == null)
        {
            _logger?.LogWarning("STT received a null audio stream.");
            return new SpeechToTextResult(false, string.Empty, 0m, "Audio stream cannot be null.");
        }

        if (audioStream.CanSeek && audioStream.Length == 0)
        {
            _logger?.LogWarning("STT received an empty audio stream.");
            return new SpeechToTextResult(false, string.Empty, 0m, "Audio stream is empty.");
        }

        var config = _options.Value;
        var isCustomEndpoint = !string.IsNullOrWhiteSpace(config.OpenAI.BaseUrl) &&
            !config.OpenAI.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

        // Fallback to mock if configured or if no API key is available (unless using custom/local endpoint)
        if (config.UseMock || (string.IsNullOrWhiteSpace(config.OpenAI.ApiKey) && !isCustomEndpoint))
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback STT service (UseMock: {UseMock}, HasKey: {HasKey})",
                    config.UseMock, !string.IsNullOrWhiteSpace(config.OpenAI.ApiKey));
                return await _fallbackService.TranscribeAsync(audioStream, contentType, ct);
            }

            if (string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
            {
                _logger?.LogWarning("OpenAI STT failed: API key is not configured.");
                return new SpeechToTextResult(false, string.Empty, 0m, "OpenAI API key is not configured.");
            }
        }

        var timeoutSeconds = config.SpeechToTextTimeoutSeconds > 0 ? config.SpeechToTextTimeoutSeconds : 15;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var endpoint = string.IsNullOrWhiteSpace(config.OpenAI.BaseUrl)
            ? DefaultEndpoint
            : $"{config.OpenAI.BaseUrl.TrimEnd('/')}/audio/transcriptions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAI.ApiKey);
        }
        if (!string.IsNullOrWhiteSpace(config.OpenAI.OrganizationId))
        {
            request.Headers.Add("OpenAI-Organization", config.OpenAI.OrganizationId);
        }

        using var form = new MultipartFormDataContent();
        using var streamContent = new StreamContent(audioStream);

        var validContentType = string.IsNullOrWhiteSpace(contentType) ? "audio/webm" : contentType;
        var mediaType = validContentType.Split(';')[0].Trim();
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        var fileExtension = ResolveFileExtension(mediaType);
        form.Add(streamContent, "file", $"audio.{fileExtension}");
        form.Add(new StringContent(config.OpenAI.WhisperModel ?? "whisper-1"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        // Greedy decoding: higher temperatures make Whisper invent text on silence.
        form.Add(new StringContent("0"), "temperature");

        if (!string.IsNullOrWhiteSpace(config.OpenAI.TranscriptionLanguage))
        {
            form.Add(new StringContent(config.OpenAI.TranscriptionLanguage), "language");
        }

        request.Content = form;

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger?.LogInformation("STT operation canceled by caller.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("STT transcription timed out after {Timeout} seconds.", timeoutSeconds);
            if (_fallbackService != null)
            {
                return await _fallbackService.TranscribeAsync(audioStream, contentType, ct);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, $"STT operation timed out after {timeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            var sanitizedErr = AiKeySanitizer.Sanitize(ex.Message, config.OpenAI.ApiKey);
            _logger?.LogError(ex, "HTTP exception during STT transcription: {Error}", sanitizedErr);
            if (_fallbackService != null)
            {
                return await _fallbackService.TranscribeAsync(audioStream, contentType, ct);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, $"Network error contacting STT service: {sanitizedErr}");
        }

        sw.Stop();

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, config.OpenAI.ApiKey);
            var errorMsg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            _logger?.LogWarning("OpenAI STT failed with status {StatusCode} in {Elapsed}ms: {Error}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorMsg);

            if (_fallbackService != null)
            {
                return await _fallbackService.TranscribeAsync(audioStream, contentType, ct);
            }
            return new SpeechToTextResult(false, string.Empty, 0m, $"STT service error: {errorMsg}");
        }

        return ParseSuccessResponse(responseBody, sw.ElapsedMilliseconds);
    }

    private SpeechToTextResult ParseSuccessResponse(string json, long elapsedMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("text", out var textProp))
            {
                _logger?.LogWarning("Whisper response missing 'text' property.");
                return new SpeechToTextResult(false, string.Empty, 0m, "Malformed response from STT service.");
            }

            var rawText = textProp.GetString()?.Trim() ?? string.Empty;
            var text = WhisperHallucinationFilter.Clean(root, rawText);
            if (text.Length == 0)
            {
                // Silence or noise: Whisper returns nothing or invents outro phrases. Ask the student to record again.
                _logger?.LogWarning("Whisper STT heard no speech in {Elapsed}ms (raw transcript length {Length}).",
                    elapsedMs, rawText.Length);
                return new SpeechToTextResult(false, string.Empty, 0m, NoSpeechDetectedMessage);
            }

            decimal confidence = CalculateConfidence(root);

            _logger?.LogInformation("Whisper STT completed in {Elapsed}ms with confidence {Confidence}",
                elapsedMs, confidence);

            return new SpeechToTextResult(
                IsSuccess: true,
                TranscriptText: text,
                ConfidenceScore: confidence
            );
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse Whisper JSON response.");
            return new SpeechToTextResult(false, string.Empty, 0m, "Failed to parse STT response.");
        }
    }

    private static decimal CalculateConfidence(JsonElement root)
    {
        if (root.TryGetProperty("segments", out var segments) && segments.GetArrayLength() > 0)
        {
            decimal totalProb = 0m;
            int count = 0;

            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.TryGetProperty("no_speech_prob", out var noSpeechProp) &&
                    noSpeechProp.TryGetDecimal(out var noSpeechProb))
                {
                    decimal segmentConfidence = Math.Clamp(1.0m - noSpeechProb, 0.0m, 1.0m);
                    totalProb += segmentConfidence;
                    count++;
                }
            }

            if (count > 0)
            {
                return Math.Round(totalProb / count, 2);
            }
        }

        return 0.95m;
    }

    private static string ResolveFileExtension(string mediaType)
    {
        return mediaType.ToLowerInvariant() switch
        {
            "audio/webm" => "webm",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/ogg" => "ogg",
            "audio/m4a" or "audio/mp4" => "m4a",
            _ => "webm"
        };
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
