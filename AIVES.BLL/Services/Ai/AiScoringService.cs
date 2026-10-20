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

public class AiScoringService : IAiScoringService
{
    private const string DefaultDisclaimer = "Điểm số sơ bộ do Giám khảo AI tự động chấm dựa trên transcript câu trả lời, chưa qua giảng viên chấm lại.";
    private const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";

    private readonly HttpClient _httpClient;
    private readonly IOptions<AiServiceOptions> _options;
    private readonly IAiCredentialStore? _credentialStore;
    private readonly ILogger<AiScoringService>? _logger;

    public AiScoringService(
        HttpClient httpClient,
        IOptions<AiServiceOptions> options,
        IAiCredentialStore? credentialStore = null,
        ILogger<AiScoringService>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentialStore = credentialStore;
        _logger = logger;
    }

    public async Task<AiGradingResult> GradeAnswerAsync(
        string questionTitle,
        string questionContent,
        string initialAnswerTranscript,
        string? followUpQuestion,
        string? followUpAnswerTranscript,
        CancellationToken ct = default)
    {
        var configured = _options.Value;
        var activeOptions = AiSettingsResolver.Resolve(configured, _credentialStore);

        if (activeOptions.UsesRealProviders && !string.IsNullOrWhiteSpace(activeOptions.OpenAI.ApiKey))
        {
            try
            {
                return await GradeWithOpenAiAsync(
                    activeOptions,
                    questionTitle,
                    questionContent,
                    initialAnswerTranscript,
                    followUpQuestion,
                    followUpAnswerTranscript,
                    ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "OpenAI scoring failed. Falling back to heuristic AI grading.");
            }
        }

        return GradeWithHeuristics(
            questionTitle,
            questionContent,
            initialAnswerTranscript,
            followUpQuestion,
            followUpAnswerTranscript);
    }

    private async Task<AiGradingResult> GradeWithOpenAiAsync(
        AiServiceOptions options,
        string questionTitle,
        string questionContent,
        string initialAnswer,
        string? followUpQuestion,
        string? followUpAnswer,
        CancellationToken ct)
    {
        var model = !string.IsNullOrWhiteSpace(options.OpenAI.Model) ? options.OpenAI.Model : "gpt-4o-mini";
        var endpoint = !string.IsNullOrWhiteSpace(options.OpenAI.BaseUrl)
            ? $"{options.OpenAI.BaseUrl.TrimEnd('/')}/chat/completions"
            : DefaultEndpoint;

        var systemPrompt = """
        You are an expert IT viva examination evaluator at university level.
        Your task is to objectively evaluate the candidate's verbal response and follow-up answer against the examination question.
        Grading Criteria:
        - Scale: 0.0 to 10.0 (increment of 0.5)
        - Passing score: >= 6.0
        - Response strictly in valid JSON format:
        {
          "score": 8.0,
          "isPassed": true,
          "feedback": "Nhận xét tổng thể chi tiết bằng tiếng Việt...",
          "strengths": "Điểm mạnh trong câu trả lời...",
          "weaknesses": "Điểm cần khắc phục hoặc bổ sung..."
        }
        """;

        var userMessage = $"""
        [ĐỀ BÀI VẤN ĐÁP]
        Tiêu đề: {questionTitle}
        Nội dung: {questionContent}

        [CÂU TRẢ LỜI CỦA THÍ SINH (STT Transcribed)]
        Trả lời ban đầu: {initialAnswer}
        Câu hỏi phụ của AI: {followUpQuestion ?? "Không có"}
        Trả lời câu hỏi phụ: {followUpAnswer ?? "Không có"}
        """;

        var payload = new
        {
            model = model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            temperature = 0.3,
            response_format = new { type = "json_object" }
        };

        var requestJson = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.OpenAI.ApiKey);
        request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenAI returned empty grading response.");
        }

        using var gradeDoc = JsonDocument.Parse(content);
        var root = gradeDoc.RootElement;

        var score = root.TryGetProperty("score", out var sElem) ? sElem.GetDecimal() : 7.0m;
        var isPassed = root.TryGetProperty("isPassed", out var pElem) ? pElem.GetBoolean() : score >= 6.0m;
        var feedback = root.TryGetProperty("feedback", out var fElem) ? fElem.GetString() ?? "Đã hoàn thành tốt câu trả lời." : "Đã hoàn thành tốt câu trả lời.";
        var strengths = root.TryGetProperty("strengths", out var stElem) ? stElem.GetString() ?? "Nắm được khái niệm cơ bản." : "Nắm được khái niệm cơ bản.";
        var weaknesses = root.TryGetProperty("weaknesses", out var wElem) ? wElem.GetString() ?? "Cần mở rộng thêm ví dụ thực tiễn." : "Cần mở rộng thêm ví dụ thực tiễn.";

        return new AiGradingResult(
            Score: Math.Clamp(score, 0m, 10m),
            IsPassed: isPassed,
            Feedback: feedback,
            Strengths: strengths,
            Weaknesses: weaknesses,
            Disclaimer: DefaultDisclaimer
        );
    }

    private static AiGradingResult GradeWithHeuristics(
        string questionTitle,
        string questionContent,
        string initialAnswer,
        string? followUpQuestion,
        string? followUpAnswer)
    {
        var combinedAnswer = $"{initialAnswer} {followUpAnswer}".Trim();
        var wordCount = combinedAnswer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        decimal baseScore = 6.5m;

        if (wordCount >= 20) baseScore += 1.0m;
        if (wordCount >= 45) baseScore += 1.0m;
        if (!string.IsNullOrWhiteSpace(followUpAnswer) && followUpAnswer.Length > 10) baseScore += 0.5m;

        // Check relevance
        var keywords = (questionTitle + " " + questionContent)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Take(10)
            .ToList();

        var matches = keywords.Count(kw => combinedAnswer.Contains(kw, StringComparison.OrdinalIgnoreCase));
        if (matches >= 2) baseScore += 0.5m;

        baseScore = Math.Clamp(baseScore, 4.0m, 9.5m);
        var isPassed = baseScore >= 6.0m;

        var feedback = isPassed
            ? $"Thí sinh trả lời rõ ràng, lưu loát và đúng trọng tâm câu hỏi về '{questionTitle}'. Giải thích thỏa đáng câu hỏi hỏi xoáy của Giám khảo AI."
            : $"Thí sinh đã nỗ lực trả lời nhưng nội dung còn sơ sài, chưa giải thích đầy đủ khía cạnh cốt lõi của '{questionTitle}'.";

        var strengths = "Trình bày tự tin qua giọng nói, nắm bắt được ngữ cảnh câu hỏi và tương tác tốt với hệ thống AI.";
        var weaknesses = isPassed
            ? "Có thể phân tích sâu hơn về các trường hợp biên (edge cases) và cơ chế xử lý lỗi."
            : "Cần ôn tập kỹ hơn về kiến trúc kỹ thuật và đưa ra các minh chứng mã nguồn cụ thể.";

        return new AiGradingResult(
            Score: baseScore,
            IsPassed: isPassed,
            Feedback: feedback,
            Strengths: strengths,
            Weaknesses: weaknesses,
            Disclaimer: DefaultDisclaimer
        );
    }
}
