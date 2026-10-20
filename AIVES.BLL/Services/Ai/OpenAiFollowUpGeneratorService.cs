using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Follow-up question generator adapter using OpenAI or OpenAI-compatible Chat Completion API.
/// Strictly generates targeted viva probing questions without grading or scoring (Assignment 2 scope).
/// </summary>
public class OpenAiFollowUpGeneratorService : IFollowUpGeneratorService
{
    private const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
    private readonly HttpClient _httpClient;
    private readonly IOptions<AiServiceOptions> _options;
    private readonly IFollowUpGeneratorService? _fallbackService;
    private readonly ILogger<OpenAiFollowUpGeneratorService>? _logger;

    public OpenAiFollowUpGeneratorService(
        HttpClient httpClient,
        IOptions<AiServiceOptions> options,
        ILogger<OpenAiFollowUpGeneratorService>? logger = null,
        IFollowUpGeneratorService? fallbackService = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _fallbackService = fallbackService;
    }

    /// <inheritdoc />
    public async Task<FollowUpResult> GenerateFollowUpQuestionAsync(
        string originalQuestion,
        string studentAnswerTranscript,
        string subjectContext,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(originalQuestion))
        {
            _logger?.LogWarning("Follow-up generation received empty original question.");
            return new FollowUpResult(false, string.Empty, string.Empty, "Original question cannot be empty.");
        }

        var config = _options.Value;
        var isCustomEndpoint = !string.IsNullOrWhiteSpace(config.OpenAI.BaseUrl) &&
            !config.OpenAI.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

        // Fallback to mock if configured or if no API key is available (unless using a custom/local endpoint without auth)
        if (config.UseMock || (string.IsNullOrWhiteSpace(config.OpenAI.ApiKey) && !isCustomEndpoint))
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback follow-up service (UseMock: {UseMock}, HasKey: {HasKey})",
                    config.UseMock, !string.IsNullOrWhiteSpace(config.OpenAI.ApiKey));
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct);
            }

            if (string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
            {
                _logger?.LogWarning("OpenAI Follow-up failed: API key is not configured.");
                return new FollowUpResult(false, string.Empty, string.Empty, "OpenAI API key is not configured.");
            }
        }

        var timeoutSeconds = config.FollowUpTimeoutSeconds > 0 ? config.FollowUpTimeoutSeconds : 15;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var endpoint = string.IsNullOrWhiteSpace(config.OpenAI.BaseUrl)
            ? DefaultEndpoint
            : $"{config.OpenAI.BaseUrl.TrimEnd('/')}/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAI.ApiKey);
        }
        if (!string.IsNullOrWhiteSpace(config.OpenAI.OrganizationId))
        {
            request.Headers.Add("OpenAI-Organization", config.OpenAI.OrganizationId);
        }

        var userContent = $"""
        Subject Context: {subjectContext}
        Original Question: {originalQuestion}
        Student Answer Transcript: {studentAnswerTranscript}
        """;

        var payload = new
        {
            model = config.OpenAI.Model ?? "gpt-4o-mini",
            messages = new[]
            {
                new { role = "system", content = AiPrompts.VivaFollowUpSystemPrompt },
                new { role = "user", content = userContent }
            },
            response_format = new { type = "json_object" },
            temperature = 0.4,
            max_tokens = 250
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
            _logger?.LogInformation("Follow-up generation canceled by caller.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Follow-up generation timed out after {Timeout} seconds.", timeoutSeconds);
            if (_fallbackService != null)
            {
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, $"Follow-up generation timed out after {timeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            var sanitizedErr = AiKeySanitizer.Sanitize(ex.Message, config.OpenAI.ApiKey);
            _logger?.LogError(ex, "HTTP exception during follow-up generation: {Error}", sanitizedErr);
            if (_fallbackService != null)
            {
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, $"Network error contacting LLM service: {sanitizedErr}");
        }

        sw.Stop();

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, config.OpenAI.ApiKey);
            var errorMsg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            _logger?.LogWarning("OpenAI LLM failed with status {StatusCode} in {Elapsed}ms: {Error}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorMsg);

            if (_fallbackService != null)
            {
                return await _fallbackService.GenerateFollowUpQuestionAsync(originalQuestion, studentAnswerTranscript, subjectContext, ct);
            }
            return new FollowUpResult(false, string.Empty, string.Empty, $"LLM service error: {errorMsg}");
        }

        return ParseSuccessResponse(responseBody, sw.ElapsedMilliseconds);
    }

    private FollowUpResult ParseSuccessResponse(string json, long elapsedMs)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                _logger?.LogWarning("OpenAI LLM returned no choices: {Json}", json);
                return new FollowUpResult(false, string.Empty, string.Empty, "Malformed response from LLM service: no choices.");
            }

            var firstChoice = choices[0];
            if (!firstChoice.TryGetProperty("message", out var msgProp) ||
                !msgProp.TryGetProperty("content", out var contentProp))
            {
                return new FollowUpResult(false, string.Empty, string.Empty, "Malformed message payload from LLM.");
            }

            var contentJson = contentProp.GetString();
            if (string.IsNullOrWhiteSpace(contentJson))
            {
                return new FollowUpResult(false, string.Empty, string.Empty, "Empty completion content from LLM.");
            }

            using var innerDoc = JsonDocument.Parse(contentJson);
            var innerRoot = innerDoc.RootElement;

            var followUpText = innerRoot.TryGetProperty("followUpQuestion", out var qProp)
                ? qProp.GetString()?.Trim() ?? string.Empty
                : string.Empty;

            var cognitiveIntent = innerRoot.TryGetProperty("cognitiveIntent", out var iProp)
                ? iProp.GetString()?.Trim() ?? "Deep-Dive"
                : "Deep-Dive";

            if (string.IsNullOrWhiteSpace(followUpText))
            {
                return new FollowUpResult(false, string.Empty, string.Empty, "LLM returned empty follow-up question text.");
            }

            _logger?.LogInformation("OpenAI LLM generated follow-up in {Elapsed}ms with intent '{Intent}'",
                elapsedMs, cognitiveIntent);

            return new FollowUpResult(
                IsSuccess: true,
                FollowUpQuestionText: followUpText,
                CognitiveIntent: cognitiveIntent
            );
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to parse OpenAI completion JSON response.");
            return new FollowUpResult(false, string.Empty, string.Empty, "Failed to parse follow-up LLM response.");
        }
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
