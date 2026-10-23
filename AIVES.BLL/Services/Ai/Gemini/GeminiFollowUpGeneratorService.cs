using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.Ai.Gemini;

/// <summary>
/// Follow-up LLM generator service adapter using Google Gemini generateContent REST API.
/// Generates contextual 1-2 sentence viva probing questions conforming to follow-up safety policies.
/// </summary>
public sealed class GeminiFollowUpGeneratorService : IFollowUpGeneratorService
{
    private readonly HttpClient _httpClient;
    private readonly AiProviderProfile _profile;
    private readonly string _model;
    private readonly FollowUpPolicyValidator _validator;
    private readonly int _timeoutSeconds;
    private readonly IFollowUpGeneratorService? _fallbackService;
    private readonly ILogger<GeminiFollowUpGeneratorService>? _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public GeminiFollowUpGeneratorService(
        HttpClient httpClient,
        AiProviderProfile profile,
        string? model = null,
        FollowUpPolicyValidator? validator = null,
        int timeoutSeconds = 30,
        ILogger<GeminiFollowUpGeneratorService>? logger = null,
        IFollowUpGeneratorService? fallbackService = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _retryDelays = retryDelays ?? GeminiRetryPolicy.DefaultDelays;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _model = string.IsNullOrWhiteSpace(model) ? GeminiModels.DefaultFollowUp : model;
        _validator = validator ?? new FollowUpPolicyValidator();
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
        _logger = logger;
        _fallbackService = fallbackService;
    }

    public async Task<FollowUpResult> GenerateFollowUpQuestionAsync(
        string originalQuestion,
        string studentAnswerTranscript,
        string subjectContext,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(originalQuestion))
        {
            _logger?.LogWarning("Gemini Follow-up received empty original question.");
            return new FollowUpResult(false, string.Empty, string.Empty, "Original question cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(studentAnswerTranscript))
        {
            _logger?.LogWarning("Gemini Follow-up received empty student transcript.");
            return new FollowUpResult(false, string.Empty, string.Empty, "Student transcript cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(_profile.ApiKey))
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback follow-up service (Gemini API key is not configured).");
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct).ConfigureAwait(false);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, "Gemini API key is not configured.");
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

        var userPrompt = $"""
        Subject Context: {subjectContext}
        Original Question: {originalQuestion}
        Student Answer Transcript: {studentAnswerTranscript}
        """;

        var payload = new
        {
            system_instruction = new
            {
                parts = new[]
                {
                    new { text = AiPrompts.VivaFollowUpSystemPrompt }
                }
            },
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = userPrompt }
                    }
                }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                temperature = 0.4
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
            response = await GeminiRetryPolicy.SendAsync(_httpClient, request, _retryDelays, _logger, "Gemini Follow-up", linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger?.LogInformation("Gemini Follow-up canceled by caller.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Gemini Follow-up timed out after {Timeout} seconds.", _timeoutSeconds);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Gemini Follow-up timed out. Falling back to mock service.");
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct).ConfigureAwait(false);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, $"Gemini Follow-up timed out after {_timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            var sanitizedErr = AiKeySanitizer.Sanitize(ex.Message, _profile.ApiKey);
            _logger?.LogError(ex, "Gemini Follow-up network exception: {Error}", sanitizedErr);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Gemini Follow-up network error. Falling back to mock service.");
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct).ConfigureAwait(false);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, $"Network error contacting Gemini Follow-up: {sanitizedErr}");
        }

        sw.Stop();
        var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, _profile.ApiKey);
            var errorMsg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            _logger?.LogWarning("Gemini Follow-up failed with status {StatusCode} in {Elapsed}ms: {Error}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorMsg);

            if (_fallbackService != null)
            {
                _logger?.LogInformation("Falling back to mock Follow-up service due to Gemini API failure.");
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct).ConfigureAwait(false);
            }

            return new FollowUpResult(false, string.Empty, string.Empty, $"Gemini LLM error: {errorMsg}");
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
                        var jsonText = textElem.GetString() ?? string.Empty;
                        using var innerDoc = JsonDocument.Parse(jsonText);
                        var innerRoot = innerDoc.RootElement;

                        var questionText = innerRoot.GetProperty("followUpQuestion").GetString() ?? string.Empty;
                        var intentText = innerRoot.GetProperty("cognitiveIntent").GetString() ?? "Clarification";

                        var rawResult = new FollowUpResult(true, questionText, intentText);
                        var validatedResult = _validator.Validate(rawResult, originalQuestion, studentAnswerTranscript);

                        _logger?.LogInformation("Gemini Follow-up processed in {Elapsed}ms [Intent: {Intent}]",
                            sw.ElapsedMilliseconds, validatedResult.CognitiveIntent);
                        return validatedResult;
                    }
                }
            }

            _logger?.LogWarning("Gemini Follow-up response had no valid text candidate.");
            if (_fallbackService != null)
            {
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct).ConfigureAwait(false);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, "Gemini did not return a follow-up question.");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to parse Gemini Follow-up JSON response.");
            if (_fallbackService != null)
            {
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct).ConfigureAwait(false);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, "Failed to parse Gemini follow-up response.");
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
        catch { }
        return null;
    }
}
