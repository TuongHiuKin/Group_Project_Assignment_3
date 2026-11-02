using System.Text.RegularExpressions;
using AIVES.BLL.Models.Grading;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Trình kiểm định và chuẩn hóa kết quả chấm điểm của mô hình AI:
/// 1. Phát hiện và vô hiệu hóa nỗ lực Prompt Injection hoặc "xin điểm" từ sinh viên.
/// 2. Khống chế giới hạn điểm số (clamping) trong [0, MaxPoints] cho cả tổng điểm và từng tiêu chí.
/// 3. Đối soát tính nhất quán giữa tổng điểm và tổng điểm các tiêu chí Rubric.
/// 4. Đảm bảo đầy đủ độ phủ của tiêu chí Rubric.
/// 5. Phục hồi và chuẩn hóa JSON kết quả khi mô hình trả về markdown hoặc văn bản thừa.
/// </summary>
public sealed class GradingOutputValidator
{
    private readonly ILogger<GradingOutputValidator>? _logger;

    // Các mẫu regex phát hiện Prompt Injection (cố tình ghi đè system prompt / ép AI cho điểm)
    private static readonly Regex[] InjectionPatterns =
    [
        new(@"\b(ignore|disregard|forget)\b.*?\b(instructions|prompts|rules|rubrics?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(bỏ qua|quên|hủy bỏ)\b.*?\b(hướng dẫn|chỉ dẫn|prompt|rubric|quy tắc)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(you are now|act as|pretend to be|hãy đóng vai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(system prompt override|override instructions|system command)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(give|award)\b.*?\b(full|max|maximum|10|ten)\s+(points?|score|marks?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(say|output|return)\s+(only|that)?\s*(10/10|full marks?|perfect score)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(cho|hãy cho)\s+(em|tôi)\s*(xin)?\s*(full|tối đa|10|mười)\s*điểm\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    // Các mẫu regex phát hiện hành vi "xin điểm" (begging)
    private static readonly Regex[] BeggingPatterns =
    [
        new(@"\b(xin điểm|cho em xin|xin châm chước|cứu em|xin thầy|xin cô|xin ai)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(em rớt môn|bị rớt|bị đuổi học|tội nghiệp em|cứu vớt cuộc đời)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(cho em qua môn|qua môn này|được qua môn|đừng đánh rớt em)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(thầy ơi cho em qua|cô ơi cho em qua|ai ơi cho em qua)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(please pass me|help me pass|begging you for points?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    public GradingOutputValidator(ILogger<GradingOutputValidator>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Kiểm tra toàn diện kết quả chấm điểm AI, áp dụng chính sách chống gian lận/xin điểm
    /// và chuẩn hóa điểm số theo thang Rubric.
    /// </summary>
    public QuestionGradingResultDto Validate(
        QuestionGradingResultDto result, 
        GradingInputDto input)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(input);

        // 1. Phân tích nội dung câu trả lời của sinh viên để phát hiện gian lận / xin điểm
        var transcriptInspection = InspectTranscript(input.StudentAnswerTranscript, input.FollowUpTurns);

        if (transcriptInspection.IsSevereViolation)
        {
            _logger?.LogWarning(
                "Phát hiện vi phạm nghiêm trọng (Prompt Injection / Begging độc quyền) trong câu hỏi {QuestionId} của phiên {SessionId}: {Reason}",
                input.QuestionId, input.SessionId, transcriptInspection.Reason);

            result.AiScore = 0m;
            result.AiFeedback = $"[Cảnh báo vi phạm]: {transcriptInspection.Reason} Không công nhận điểm số và gán 0 điểm.";
            result.Strengths = string.Empty;
            result.Weaknesses = "Câu trả lời không có nội dung chuyên môn hợp lệ, vi phạm tính trung thực học thuật.";

            foreach (var outcome in result.CriteriaOutcomes)
            {
                outcome.AwardedPoints = 0m;
                outcome.IsMet = false;
                outcome.Explanation = "Không đạt do câu trả lời chứa lệnh can thiệp AI hoặc xin điểm không hợp lệ.";
            }

            return result;
        }

        // 2. Chuẩn hóa và làm sạch tiêu chí Rubric
        var rubricMap = (input.RubricCriteria ?? new List<RubricCriterionDto>())
            .ToDictionary(r => r.CriterionCode, StringComparer.OrdinalIgnoreCase);

        var reconciledOutcomes = new List<CriterionOutcomeDto>();

        foreach (var outcome in result.CriteriaOutcomes)
        {
            rubricMap.TryGetValue(outcome.CriterionCode, out var matchedRubric);
            var maxPts = matchedRubric != null && matchedRubric.MaxPoints > 0 
                ? matchedRubric.MaxPoints 
                : (outcome.MaxPoints > 0 ? outcome.MaxPoints : 10m);

            // Giới hạn điểm thành phần chặt chẽ trong [0, MaxPoints]
            var clampedAwarded = Math.Clamp(outcome.AwardedPoints, 0m, maxPts);

            // Kiểm tra tính nhất quán giữa IsMet và AwardedPoints
            var isMet = outcome.IsMet;
            if (clampedAwarded <= 0m && isMet)
            {
                isMet = false;
            }
            else if (clampedAwarded >= (maxPts * 0.5m) && !isMet)
            {
                isMet = true;
            }

            reconciledOutcomes.Add(new CriterionOutcomeDto
            {
                CriterionCode = outcome.CriterionCode,
                CriterionName = !string.IsNullOrWhiteSpace(outcome.CriterionName) 
                    ? outcome.CriterionName 
                    : (matchedRubric?.CriterionName ?? outcome.CriterionCode),
                MaxPoints = maxPts,
                AwardedPoints = clampedAwarded,
                IsMet = isMet,
                Explanation = outcome.Explanation ?? string.Empty
            });
        }

        // Đảm bảo không bị thiếu tiêu chí nào được định nghĩa trong Rubric
        foreach (var rubric in input.RubricCriteria ?? Enumerable.Empty<RubricCriterionDto>())
        {
            if (!reconciledOutcomes.Any(o => string.Equals(o.CriterionCode, rubric.CriterionCode, StringComparison.OrdinalIgnoreCase)))
            {
                reconciledOutcomes.Add(new CriterionOutcomeDto
                {
                    CriterionCode = rubric.CriterionCode,
                    CriterionName = rubric.CriterionName,
                    MaxPoints = rubric.MaxPoints,
                    AwardedPoints = 0m,
                    IsMet = false,
                    Explanation = "Tiêu chí không xuất hiện trong phản hồi đánh giá của mô hình AI."
                });
            }
        }

        result.CriteriaOutcomes = reconciledOutcomes;

        // 3. Khống chế và đối soát tổng điểm (Total Score Reconcile)
        decimal calculatedScore;
        if (reconciledOutcomes.Count > 0)
        {
            calculatedScore = Math.Round(reconciledOutcomes.Sum(o => o.AwardedPoints), 2);
        }
        else
        {
            calculatedScore = result.AiScore.GetValueOrDefault(0m);
        }

        result.AiScore = Math.Clamp(calculatedScore, 0m, input.MaxPoints);

        // 4. Bổ sung cảnh báo nếu có phát hiện xin điểm kèm theo phần trả lời kỹ thuật
        if (transcriptInspection.HasAdvisoryWarning)
        {
            _logger?.LogInformation(
                "Phát hiện cảnh báo xin điểm phụ trong câu hỏi {QuestionId}: {Reason}",
                input.QuestionId, transcriptInspection.Reason);

            result.AiFeedback = $"[Lưu ý học thuật]: {transcriptInspection.Reason}\n{result.AiFeedback}".Trim();
            if (string.IsNullOrWhiteSpace(result.Weaknesses))
            {
                result.Weaknesses = "Cần tránh đưa nội dung xin điểm hoặc tác động tâm lý vào câu trả lời thi vấn đáp.";
            }
        }

        // 5. Làm sạch văn bản tránh script/tag độc hại
        result.AiFeedback = SanitizePlainText(result.AiFeedback);
        result.Strengths = SanitizePlainText(result.Strengths);
        result.Weaknesses = SanitizePlainText(result.Weaknesses);

        return result;
    }

    /// <summary>
    /// Phân tích transcript để phát hiện dấu hiệu can thiệp prompt hoặc xin điểm.
    /// </summary>
    public TranscriptInspectionResult InspectTranscript(
        string? mainTranscript, 
        IEnumerable<string>? followUpTurns = null)
    {
        var combined = $"{mainTranscript ?? string.Empty} {string.Join(" ", followUpTurns ?? Enumerable.Empty<string>())}".Trim();

        if (string.IsNullOrWhiteSpace(combined))
        {
            return new TranscriptInspectionResult(false, false, string.Empty);
        }

        var injectionMatches = InjectionPatterns
            .Where(p => p.IsMatch(combined))
            .ToList();

        var beggingMatches = BeggingPatterns
            .Where(p => p.IsMatch(combined))
            .ToList();

        var hasInjection = injectionMatches.Count > 0;
        var hasBegging = beggingMatches.Count > 0;

        if (!hasInjection && !hasBegging)
        {
            return new TranscriptInspectionResult(false, false, string.Empty);
        }

        // Kiểm tra xem câu trả lời có nội dung kỹ thuật thực sự hay chỉ toàn lời xin điểm/injection
        var normalizedWords = combined
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Nếu câu trả lời quá ngắn (< 25 từ) hoặc có injection rõ rệt -> coi là vi phạm nghiêm trọng
        if (hasInjection || (hasBegging && normalizedWords.Count <= 30))
        {
            var reason = hasInjection
                ? "Phát hiện chỉ thị thao túng prompt hoặc ép điểm (Prompt Injection)."
                : "Phát hiện hành vi xin điểm không có nội dung học thuật hợp lệ (Point Begging).";

            return new TranscriptInspectionResult(true, false, reason);
        }

        // Nếu câu trả lời dài và có nội dung kỹ thuật nhưng kèm theo xin điểm ở cuối
        return new TranscriptInspectionResult(
            false, 
            true, 
            "Phát hiện nội dung xin điểm đính kèm. Điểm số chỉ được đánh giá dựa trên phần chuyên môn kỹ thuật thực tế.");
    }

    /// <summary>
    /// Phục hồi chuỗi JSON từ phản hồi thô của LLM khi bị bọc markdown hoặc lẫn văn bản phụ.
    /// </summary>
    public static bool TryCleanAndExtractJson(string? raw, out string cleanedJson)
    {
        cleanedJson = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();

        // Bỏ bọc code block markdown ```json ... ```
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```json".Length..];
        }
        else if (trimmed.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["```".Length..];
        }

        if (trimmed.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3];
        }

        trimmed = trimmed.Trim();

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            cleanedJson = trimmed.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
            return true;
        }

        return false;
    }

    private static string? SanitizePlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        // Loại bỏ thẻ HTML/script nguy hiểm để bảo đảm an toàn khi render UI
        return Regex.Replace(text, @"<[^>]*>", string.Empty).Trim();
    }
}

public sealed record TranscriptInspectionResult(
    bool IsSevereViolation, 
    bool HasAdvisoryWarning, 
    string Reason);
