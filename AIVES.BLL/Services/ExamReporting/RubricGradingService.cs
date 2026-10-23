using System.Globalization;
using System.Text;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;

namespace AIVES.BLL.Services.ExamReporting;

/// <summary>
/// Chấm điểm theo Rubric từ khóa: chuẩn hóa transcript (chữ thường, bỏ dấu, bỏ dấu câu),
/// tiêu chí đạt khi khớp ít nhất một từ khóa, điểm câu = tổng trọng số đạt x thang điểm / tổng trọng số.
/// </summary>
public sealed class RubricGradingService : IRubricGradingService
{
    /// <summary>Số từ tối thiểu không có trong câu hỏi để coi là một câu trả lời thật.</summary>
    public const int MinNovelTokens = 3;

    public const string NoRelevantAnswerFeedback = "Chưa có câu trả lời phù hợp (câu trả lời rỗng hoặc chỉ lặp lại câu hỏi).";

    /// <summary>Nhóm 4 (AI chấm điểm) chưa triển khai: nhận xét là mô phỏng theo Rubric của giảng viên.</summary>
    public const string FeedbackPrefix = "AI chấm gợi ý (mô phỏng theo Rubric của giảng viên): ";

    private readonly IReadOnlyList<QuestionRubric> _rubrics;

    public RubricGradingService() : this(RubricCatalog.All)
    {
    }

    public RubricGradingService(IReadOnlyList<QuestionRubric> rubrics)
    {
        _rubrics = rubrics ?? throw new ArgumentNullException(nameof(rubrics));
    }

    public IReadOnlyList<QuestionRubric> GetRubrics(string examId) =>
        _rubrics.Where(r => string.Equals(r.ExamId, examId, StringComparison.OrdinalIgnoreCase)).ToList();

    public QuestionRubric? GetRubric(string questionId) =>
        _rubrics.FirstOrDefault(r => string.Equals(r.QuestionId, questionId, StringComparison.OrdinalIgnoreCase));

    public RubricGradingResult? Grade(string questionId, string? questionText, string? answerTranscript, decimal passThresholdScore)
    {
        var rubric = GetRubric(questionId);
        if (rubric == null)
        {
            return null;
        }

        var description = BuildRubricDescription(rubric);
        var answer = Normalize(answerTranscript);
        var hasRelevantAnswer = CountNovelTokens(answer, Normalize(questionText)) >= MinNovelTokens;

        var paddedAnswer = $" {answer} ";
        var outcomes = rubric.Criteria
            .Select(c =>
            {
                var matched = hasRelevantAnswer
                    ? c.Keywords.Where(k => ContainsPhrase(paddedAnswer, k)).ToList()
                    : new List<string>();
                return new RubricCriterionOutcome
                {
                    CriterionId = c.Id,
                    Name = c.Name,
                    IsMet = matched.Count > 0,
                    MatchedKeywords = matched
                };
            })
            .ToList();

        var metWeight = rubric.Criteria.Where(c => outcomes.First(o => o.CriterionId == c.Id).IsMet).Sum(c => c.Weight);
        var earned = rubric.TotalWeight > 0 ? Math.Round(metWeight * rubric.MaxScore / rubric.TotalWeight, 2) : 0m;
        var metCount = outcomes.Count(o => o.IsMet);

        string strengths;
        string weaknesses;
        string feedback;
        if (!hasRelevantAnswer)
        {
            strengths = string.Empty;
            weaknesses = string.Join(" ", rubric.Criteria.Select(c => $"Cần bổ sung: {c.ImprovementHint}"));
            feedback = NoRelevantAnswerFeedback;
        }
        else
        {
            strengths = string.Join(" ", rubric.Criteria
                .Where(c => outcomes.First(o => o.CriterionId == c.Id).IsMet)
                .Select(c => $"Đã nêu: {c.Name}."));
            weaknesses = string.Join(" ", rubric.Criteria
                .Where(c => !outcomes.First(o => o.CriterionId == c.Id).IsMet)
                .Select(c => $"Cần bổ sung: {c.ImprovementHint}"));
            var missing = rubric.Criteria
                .Where(c => !outcomes.First(o => o.CriterionId == c.Id).IsMet)
                .Select(c => c.Name)
                .ToList();
            var verdict = missing.Count == 0
                ? "Câu trả lời đầy đủ, bao quát hết các ý trọng tâm của giảng viên."
                : earned >= passThresholdScore
                    ? $"Câu trả lời khá tốt nhưng còn thiếu ý: {string.Join("; ", missing)}."
                    : $"Câu trả lời chưa đạt, cần ôn lại các ý: {string.Join("; ", missing)}.";
            feedback = $"{FeedbackPrefix}đạt {metCount}/{rubric.Criteria.Count} tiêu chí, {earned:0.##}/{rubric.MaxScore:0.##} điểm. {verdict}";
        }

        return new RubricGradingResult
        {
            QuestionId = rubric.QuestionId,
            EarnedScore = earned,
            MaxScore = rubric.MaxScore,
            PassThresholdScore = passThresholdScore,
            IsPassed = earned >= passThresholdScore,
            HasRelevantAnswer = hasRelevantAnswer,
            Outcomes = outcomes,
            Strengths = strengths,
            Weaknesses = weaknesses,
            Feedback = feedback,
            RubricDescription = description
        };
    }

    /// <summary>Chữ thường, bỏ dấu tiếng Việt (kể cả đ), thay dấu câu bằng khoảng trắng, gộp khoảng trắng.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                sb.Append(' ');
                lastWasSpace = true;
            }
        }

        return sb.ToString().Trim();
    }

    private static bool ContainsPhrase(string paddedNormalizedAnswer, string keyword)
    {
        var normalizedKeyword = Normalize(keyword);
        return normalizedKeyword.Length > 0 && paddedNormalizedAnswer.Contains($" {normalizedKeyword} ", StringComparison.Ordinal);
    }

    private static int CountNovelTokens(string normalizedAnswer, string normalizedQuestion)
    {
        if (normalizedAnswer.Length == 0)
        {
            return 0;
        }

        var questionTokens = new HashSet<string>(normalizedQuestion.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        return normalizedAnswer
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !questionTokens.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .Count();
    }

    private static string BuildRubricDescription(QuestionRubric rubric) =>
        string.Join(" ", rubric.Criteria.Select((c, i) => $"{i + 1}. {c.Name} ({rubric.PointsFor(c):0.##}đ): {c.Description}"));
}
