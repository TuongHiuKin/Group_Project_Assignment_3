using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// Follow-up question generator adapter using OpenAI Chat Completion API (GPT-4o-mini).
/// Strictly generates targeted viva probing questions without grading or scoring (Assignment 2 scope).
/// </summary>
public class OpenAiFollowUpGeneratorService : IFollowUpGeneratorService
{
    private const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
    private readonly HttpClient _httpClient;
    private readonly IOptions<AiServiceOptions> _options;
    private readonly IFollowUpGeneratorService? _fallbackService;
    private readonly ILogger<OpenAiFollowUpGeneratorService>? _logger;

    private const string SystemPrompt = """
    You are an expert IT viva examination examiner.
    Your task is to analyze the original question and the student's verbal answer transcript, and formulate exactly ONE targeted follow-up question (1 to 2 concise sentences).

    CRITICAL RULES:
    1. STRICTLY FORBIDDEN: Do NOT provide grades, numerical scores, evaluation points, or pass/fail decisions.
    2. The follow-up question must directly challenge or probe deeper into what the student stated or omitted.
    3. Determine the cognitive intent: 'Clarification', 'Deep-Dive', 'Edge-Case', or 'Counter-Example'.
    4. Respond strictly in valid JSON format:
    {
      "followUpQuestion": "<1-2 sentence question>",
      "cognitiveIntent": "<Clarification|Deep-Dive|Edge-Case|Counter-Example>"
    }
    """;

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

        // Fallback to mock if configured or if no API key is available
        if (config.UseMock || string.IsNullOrWhiteSpace(config.OpenAI.ApiKey))
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.OpenAI.ApiKey);
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
                new { role = "system", content = SystemPrompt },
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
            return new FollowUpResult(false, string.Empty, string.Empty, $"Follow-up generation timed out after {timeoutSeconds} seconds.");
        }
        catch (HttpRequestException ex)
        {
            _logger?.LogError(ex, "HTTP exception during follow-up generation.");
            return new FollowUpResult(false, string.Empty, string.Empty, $"Network error contacting LLM service: {ex.Message}");
        }

        sw.Stop();

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorMsg = ExtractErrorMessage(responseBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            _logger?.LogWarning("OpenAI LLM failed with status {StatusCode} in {Elapsed}ms: {Error}",
                (int)response.StatusCode, sw.ElapsedMilliseconds, errorMsg);
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
