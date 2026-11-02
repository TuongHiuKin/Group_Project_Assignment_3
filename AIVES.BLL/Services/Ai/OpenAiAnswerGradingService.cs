using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Models.Grading;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Dịch vụ chấm điểm câu trả lời vấn đáp AI sử dụng OpenAI hoặc dịch vụ tương thích OpenAI Chat Completions API
/// với chế độ structured JSON output (response_format: { "type": "json_object" }).
/// </summary>
public sealed class OpenAiAnswerGradingService : IAnswerGradingService
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";
    private readonly HttpClient _httpClient;
    private readonly AiProviderProfile _profile;
    private readonly string _model;
    private readonly int _timeoutSeconds;
    private readonly IAnswerGradingService? _fallbackService;
    private readonly ILogger<OpenAiAnswerGradingService>? _logger;

    public OpenAiAnswerGradingService(
        HttpClient httpClient,
        AiProviderProfile profile,
        string? model = null,
        int timeoutSeconds = 30,
        ILogger<OpenAiAnswerGradingService>? logger = null,
        IAnswerGradingService? fallbackService = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
        _logger = logger;
        _fallbackService = fallbackService;
    }

    public async Task<QuestionGradingResultDto> GradeAnswerAsync(
        GradingInputDto input, 
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        var isCustomEndpoint = !string.IsNullOrWhiteSpace(_profile.BaseUrl) &&
            !_profile.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(_profile.ApiKey) && !isCustomEndpoint)
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback grading service (OpenAI API key is not configured).");
                return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
            }

            return CreateFailedResult(input, "Chưa cấu hình API key cho hồ sơ OpenAI.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var baseUrl = string.IsNullOrWhiteSpace(_profile.BaseUrl)
            ? DefaultBaseUrl
            : _profile.BaseUrl.TrimEnd('/');

        var endpoint = $"{baseUrl}/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        if (!string.IsNullOrWhiteSpace(_profile.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _profile.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(_profile.OrganizationId))
        {
            request.Headers.Add("OpenAI-Organization", _profile.OrganizationId);
        }

        var userPrompt = AiPrompts.BuildVivaGradingUserPrompt(input);

        var payload = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "system", content = AiPrompts.VivaAnswerGradingSystemPrompt },
                new { role = "user", content = userPrompt }
            },
            response_format = new { type = "json_object" },
            temperature = 0.2,
            max_tokens = 2048
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, 
                HttpCompletionOption.ResponseHeadersRead, 
                linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger?.LogInformation("OpenAI Grading canceled by caller for QuestionId: {QuestionId}.", input.QuestionId);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("OpenAI Grading timed out after {Timeout} seconds for QuestionId: {QuestionId}.", _timeoutSeconds, input.QuestionId);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Falling back to offline grading service due to OpenAI timeout.");
                return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
            }

            return CreateFailedResult(input, $"Quá thời gian chờ phản hồi từ OpenAI sau {_timeoutSeconds} giây.");
        }
        catch (Exception ex)
        {
            var sanitizedErr = AiKeySanitizer.Sanitize(ex.Message, _profile.ApiKey);
            _logger?.LogError(ex, "OpenAI Grading network exception for QuestionId {QuestionId}: {Error}", input.QuestionId, sanitizedErr);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Falling back to offline grading service due to OpenAI network error.");
                return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
            }

            return CreateFailedResult(input, $"Lỗi mạng khi kết nối tới OpenAI: {sanitizedErr}");
        }

        sw.Stop();
        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, _profile.ApiKey);
                var errorMsg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger?.LogWarning("OpenAI Grading failed with status {StatusCode} in {Elapsed}ms for QuestionId {QuestionId}: {Error}",
                    (int)response.StatusCode, sw.ElapsedMilliseconds, input.QuestionId, errorMsg);

                if (_fallbackService != null)
                {
                    _logger?.LogInformation("Falling back to offline grading service due to OpenAI API failure.");
                    return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
                }

                return CreateFailedResult(input, $"Lỗi API OpenAI (HTTP {(int)response.StatusCode}): {errorMsg}");
            }

            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;
                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var contentElem))
                    {
                        var jsonText = contentElem.GetString() ?? string.Empty;
                        var parsedResponse = AiPrompts.ExtractAndParseGradingJson(jsonText);

                        _logger?.LogInformation("OpenAI Grading successfully processed QuestionId {QuestionId} in {Elapsed}ms with score {Score}/{MaxPoints}.",
                            input.QuestionId, sw.ElapsedMilliseconds, parsedResponse.TotalScore, input.MaxPoints);

                        return MapToGradingResult(input, parsedResponse);
                    }
                }

                _logger?.LogWarning("OpenAI Grading response had no valid text choices for QuestionId {QuestionId}.", input.QuestionId);
                if (_fallbackService != null)
                {
                    return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
                }

                return CreateFailedResult(input, "Phản hồi từ OpenAI không chứa lựa chọn văn bản hợp lệ.");
            }
            catch (JsonException ex)
            {
                var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, _profile.ApiKey);
                _logger?.LogWarning(ex, "Failed to parse OpenAI Grading JSON response: {Body}", sanitizedBody);
                if (_fallbackService != null)
                {
                    _logger?.LogInformation("Falling back to offline grading service due to malformed JSON from OpenAI.");
                    return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
                }

                return CreateFailedResult(input, $"Không phân tích được định dạng JSON từ OpenAI: {ex.Message}");
            }
        }
    }

    private QuestionGradingResultDto MapToGradingResult(GradingInputDto input, AnswerGradingLlmResponse parsed)
    {
        var criteriaMap = (input.RubricCriteria ?? new List<RubricCriterionDto>())
            .ToDictionary(c => c.CriterionCode, StringComparer.OrdinalIgnoreCase);

        var outcomes = new List<CriterionOutcomeDto>();

        foreach (var c in parsed.CriteriaOutcomes)
        {
            criteriaMap.TryGetValue(c.CriterionCode, out var matchedRubric);
            var maxPts = matchedRubric != null ? matchedRubric.MaxPoints : (c.MaxPoints > 0 ? c.MaxPoints : 10m);
            var clampedAwarded = Math.Clamp(c.AwardedPoints, 0m, maxPts);

            outcomes.Add(new CriterionOutcomeDto
            {
                CriterionCode = c.CriterionCode,
                CriterionName = c.CriterionName ?? matchedRubric?.CriterionName ?? c.CriterionCode,
                MaxPoints = maxPts,
                AwardedPoints = clampedAwarded,
                IsMet = c.IsMet,
                Explanation = c.Explanation ?? string.Empty
            });
        }

        foreach (var rubric in input.RubricCriteria ?? Enumerable.Empty<RubricCriterionDto>())
        {
            if (!outcomes.Any(o => string.Equals(o.CriterionCode, rubric.CriterionCode, StringComparison.OrdinalIgnoreCase)))
            {
                outcomes.Add(new CriterionOutcomeDto
                {
                    CriterionCode = rubric.CriterionCode,
                    CriterionName = rubric.CriterionName,
                    MaxPoints = rubric.MaxPoints,
                    AwardedPoints = 0m,
                    IsMet = false,
                    Explanation = "Chưa được đánh giá trong phản hồi của AI."
                });
            }
        }

        var clampedTotal = Math.Clamp(Math.Round(parsed.TotalScore, 2), 0m, input.MaxPoints);
        var providerName = string.Equals(_profile.Kind, AiProviderKinds.OpenAiCompatible, StringComparison.OrdinalIgnoreCase)
            ? "OpenAI-Compatible"
            : "OpenAI";

        return new QuestionGradingResultDto
        {
            Id = Guid.NewGuid(),
            SessionId = input.SessionId,
            QuestionId = input.QuestionId,
            QuestionTitle = input.QuestionText,
            BloomLevel = input.BloomLevel,
            MaxPoints = input.MaxPoints,
            AiScore = clampedTotal,
            AiFeedback = parsed.Feedback,
            Strengths = parsed.Strengths,
            Weaknesses = parsed.Weaknesses,
            CriteriaOutcomes = outcomes,
            AiProvider = providerName,
            AiModel = _model,
            State = GradingState.AiGraded
        };
    }

    private QuestionGradingResultDto CreateFailedResult(GradingInputDto input, string errorMessage)
    {
        var providerName = string.Equals(_profile.Kind, AiProviderKinds.OpenAiCompatible, StringComparison.OrdinalIgnoreCase)
            ? "OpenAI-Compatible"
            : "OpenAI";

        return new QuestionGradingResultDto
        {
            Id = Guid.NewGuid(),
            SessionId = input.SessionId,
            QuestionId = input.QuestionId,
            QuestionTitle = input.QuestionText,
            BloomLevel = input.BloomLevel,
            MaxPoints = input.MaxPoints,
            AiScore = 0m,
            AiFeedback = errorMessage,
            State = GradingState.Failed,
            AiProvider = providerName,
            AiModel = _model
        };
    }

    private static string? ExtractErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var errorElem) &&
                errorElem.TryGetProperty("message", out var msgElem))
            {
                return msgElem.GetString();
            }
        }
        catch
        {
            // Bỏ qua lỗi parse nếu body lỗi không phải json
        }
        return null;
    }
}

