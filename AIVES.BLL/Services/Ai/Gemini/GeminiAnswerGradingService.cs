using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Models.Grading;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.Ai.Gemini;

/// <summary>
/// Dịch vụ chấm điểm câu trả lời vấn đáp AI sử dụng Google Gemini REST API (generateContent)
/// với chế độ structured JSON output (responseMimeType: "application/json").
/// </summary>
public sealed class GeminiAnswerGradingService : IAnswerGradingService
{
    private readonly HttpClient _httpClient;
    private readonly AiProviderProfile _profile;
    private readonly string _model;
    private readonly int _timeoutSeconds;
    private readonly IAnswerGradingService? _fallbackService;
    private readonly ILogger<GeminiAnswerGradingService>? _logger;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;

    public GeminiAnswerGradingService(
        HttpClient httpClient,
        AiProviderProfile profile,
        string? model = null,
        int timeoutSeconds = 30,
        ILogger<GeminiAnswerGradingService>? logger = null,
        IAnswerGradingService? fallbackService = null,
        IReadOnlyList<TimeSpan>? retryDelays = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _model = string.IsNullOrWhiteSpace(model) ? GeminiModels.DefaultFor(AiCapability.Grading) : model;
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
        _logger = logger;
        _fallbackService = fallbackService;
        _retryDelays = retryDelays ?? GeminiRetryPolicy.DefaultDelays;
    }

    public async Task<QuestionGradingResultDto> GradeAnswerAsync(
        GradingInputDto input, 
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_profile.ApiKey))
        {
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Using fallback grading service (Gemini API key is not configured).");
                return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
            }

            return CreateFailedResult(input, "Chưa cấu hình API key cho hồ sơ Google Gemini.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        var baseUrl = string.IsNullOrWhiteSpace(_profile.BaseUrl)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : _profile.BaseUrl.TrimEnd('/');

        // Truyền API key qua header x-goog-api-key để bảo mật, không đưa lên URL query
        var endpoint = $"{baseUrl}/models/{_model}:generateContent";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("x-goog-api-key", _profile.ApiKey);

        var userPrompt = AiPrompts.BuildVivaGradingUserPrompt(input);

        var payload = new
        {
            system_instruction = new
            {
                parts = new[]
                {
                    new { text = AiPrompts.VivaAnswerGradingSystemPrompt }
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
                temperature = 0.2
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
            response = await GeminiRetryPolicy.SendAsync(
                _httpClient, 
                request, 
                _retryDelays, 
                _logger, 
                "Gemini Grading", 
                linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger?.LogInformation("Gemini Grading canceled by caller for QuestionId: {QuestionId}.", input.QuestionId);
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Gemini Grading timed out after {Timeout} seconds for QuestionId: {QuestionId}.", _timeoutSeconds, input.QuestionId);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Falling back to offline grading service due to Gemini timeout.");
                return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
            }

            return CreateFailedResult(input, $"Quá thời gian chờ phản hồi từ Gemini sau {_timeoutSeconds} giây.");
        }
        catch (Exception ex)
        {
            var sanitizedErr = AiKeySanitizer.Sanitize(ex.Message, _profile.ApiKey);
            _logger?.LogError(ex, "Gemini Grading network exception for QuestionId {QuestionId}: {Error}", input.QuestionId, sanitizedErr);
            if (_fallbackService != null)
            {
                _logger?.LogInformation("Falling back to offline grading service due to Gemini network error.");
                return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
            }

            return CreateFailedResult(input, $"Lỗi mạng khi kết nối tới Gemini: {sanitizedErr}");
        }

        sw.Stop();
        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(linkedCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, _profile.ApiKey);
                var errorMsg = ExtractErrorMessage(sanitizedBody) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                _logger?.LogWarning("Gemini Grading failed with status {StatusCode} in {Elapsed}ms for QuestionId {QuestionId}: {Error}",
                    (int)response.StatusCode, sw.ElapsedMilliseconds, input.QuestionId, errorMsg);

                if (_fallbackService != null)
                {
                    _logger?.LogInformation("Falling back to offline grading service due to Gemini API failure.");
                    return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
                }

                return CreateFailedResult(input, $"Lỗi API Gemini (HTTP {(int)response.StatusCode}): {errorMsg}");
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
                            var parsedResponse = AiPrompts.ExtractAndParseGradingJson(jsonText);

                            _logger?.LogInformation("Gemini Grading successfully processed QuestionId {QuestionId} in {Elapsed}ms with score {Score}/{MaxPoints}.",
                                input.QuestionId, sw.ElapsedMilliseconds, parsedResponse.TotalScore, input.MaxPoints);

                            return MapToGradingResult(input, parsedResponse);
                        }
                    }
                }

                _logger?.LogWarning("Gemini Grading response had no valid text candidate for QuestionId {QuestionId}.", input.QuestionId);
                if (_fallbackService != null)
                {
                    return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
                }

                return CreateFailedResult(input, "Phản hồi từ Gemini không chứa nội dung văn bản hợp lệ.");
            }
            catch (JsonException ex)
            {
                var sanitizedBody = AiKeySanitizer.Sanitize(responseBody, _profile.ApiKey);
                _logger?.LogWarning(ex, "Failed to parse Gemini Grading JSON response: {Body}", sanitizedBody);
                if (_fallbackService != null)
                {
                    _logger?.LogInformation("Falling back to offline grading service due to malformed JSON from Gemini.");
                    return await _fallbackService.GradeAnswerAsync(input, ct).ConfigureAwait(false);
                }

                return CreateFailedResult(input, $"Không phân tích được định dạng JSON từ Gemini: {ex.Message}");
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

        // Đảm bảo mọi tiêu chí từ input rubric đều có trong outcomes
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
            AiProvider = "Gemini",
            AiModel = _model,
            State = GradingState.AiGraded
        };
    }

    private QuestionGradingResultDto CreateFailedResult(GradingInputDto input, string errorMessage)
    {
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
            AiProvider = "Gemini",
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
            // Bỏ qua lỗi parse JSON nếu body lỗi không phải json
        }
        return null;
    }
}
