using System.Globalization;
using System.Text;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.ExamReporting;

namespace AIVES.BLL.Services.Ai.Mocks;

/// <summary>
/// Triển khai mock cho IAnswerGradingService: đánh giá câu trả lời offline dựa trên từ khóa rubric,
/// tính toán điểm và nhận xét chi tiết mà không gọi API cloud (tiết kiệm chi phí và phục vụ test).
/// </summary>
public sealed class MockAnswerGradingService : IAnswerGradingService
{
    private readonly IRubricGradingService? _rubricGradingService;

    public MockAnswerGradingService(IRubricGradingService? rubricGradingService = null)
    {
        _rubricGradingService = rubricGradingService;
    }

    public Task<QuestionGradingResultDto> GradeAnswerAsync(
        GradingInputDto input, 
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var fullTranscript = CombineTranscripts(input.StudentAnswerTranscript, input.FollowUpTurns);
        var normalizedAnswer = RubricGradingService.Normalize(fullTranscript);
        var normalizedQuestion = RubricGradingService.Normalize(input.QuestionText);

        var paddedAnswer = $" {normalizedAnswer} ";
        var hasRelevantAnswer = CountNovelTokens(normalizedAnswer, normalizedQuestion) >= RubricGradingService.MinNovelTokens;

        List<CriterionOutcomeDto> outcomes = new();
        decimal totalScore = 0m;
        string feedback;
        string strengths;
        string weaknesses;

        if (input.RubricCriteria != null && input.RubricCriteria.Count > 0)
        {
            // Trường hợp có tiêu chí Rubric truyền vào (từ Database hoặc cấu hình)
            foreach (var criterion in input.RubricCriteria)
            {
                var isMet = false;
                if (hasRelevantAnswer)
                {
                    if (criterion.Keywords != null && criterion.Keywords.Count > 0)
                    {
                        isMet = criterion.Keywords.Any(k =>
                        {
                            var normKey = RubricGradingService.Normalize(k);
                            return normKey.Length > 0 && paddedAnswer.Contains($" {normKey} ", StringComparison.Ordinal);
                        });
                    }
                    else
                    {
                        // Nếu tiêu chí không định nghĩa keyword, kiểm tra tên tiêu chí trong transcript
                        var normName = RubricGradingService.Normalize(criterion.CriterionName);
                        isMet = normName.Length > 0 && paddedAnswer.Contains($" {normName} ", StringComparison.Ordinal);
                    }
                }

                var awarded = isMet ? criterion.MaxPoints : 0m;
                outcomes.Add(new CriterionOutcomeDto
                {
                    CriterionCode = criterion.CriterionCode,
                    CriterionName = criterion.CriterionName,
                    MaxPoints = criterion.MaxPoints,
                    AwardedPoints = awarded,
                    IsMet = isMet,
                    Explanation = isMet
                        ? $"Đã thể hiện đúng nội dung yêu cầu của tiêu chí '{criterion.CriterionName}'."
                        : $"Chưa đề cập hoặc giải thích chưa rõ về '{criterion.CriterionName}'."
                });
            }

            var sumAwarded = outcomes.Sum(o => o.AwardedPoints);
            totalScore = Math.Clamp(Math.Round(sumAwarded, 2), 0m, input.MaxPoints);

            var metList = outcomes.Where(o => o.IsMet).ToList();
            var unmetList = outcomes.Where(o => !o.IsMet).ToList();

            if (!hasRelevantAnswer)
            {
                strengths = string.Empty;
                weaknesses = "Chưa có câu trả lời từ sinh viên hoặc câu trả lời chỉ lặp lại câu hỏi.";
                feedback = RubricGradingService.NoRelevantAnswerFeedback;
            }
            else
            {
                strengths = metList.Count > 0
                    ? string.Join(" ", metList.Select(m => $"Đã nêu: {m.CriterionName}."))
                    : "Chưa thể hiện rõ các luận điểm chính.";

                weaknesses = unmetList.Count > 0
                    ? string.Join(" ", unmetList.Select(u => $"Cần bổ sung: {u.CriterionName}."))
                    : "Đã bao quát đầy đủ các tiêu chí.";

                var verdict = unmetList.Count == 0
                    ? "Câu trả lời đầy đủ, đáp ứng trọn vẹn yêu cầu câu hỏi."
                    : totalScore >= (input.MaxPoints * 0.5m)
                        ? $"Câu trả lời khá tốt nhưng còn thiếu: {string.Join("; ", unmetList.Select(u => u.CriterionName))}."
                        : $"Câu trả lời chưa đạt, cần ôn tập thêm: {string.Join("; ", unmetList.Select(u => u.CriterionName))}.";

                feedback = $"AI chấm gợi ý (mô phỏng Rubric offline): Đạt {metList.Count}/{outcomes.Count} tiêu chí, {totalScore:0.##}/{input.MaxPoints:0.##} điểm. {verdict}";
            }
        }
        else if (_rubricGradingService != null)
        {
            // Fallback sang RubricCatalog thông qua IRubricGradingService nếu input.RubricCriteria rỗng
            var legacyResult = _rubricGradingService.Grade(
                input.QuestionId, 
                input.QuestionText, 
                fullTranscript, 
                input.MaxPoints * 0.5m);

            if (legacyResult != null)
            {
                totalScore = Math.Clamp(legacyResult.EarnedScore, 0m, input.MaxPoints);
                strengths = legacyResult.Strengths;
                weaknesses = legacyResult.Weaknesses;
                feedback = legacyResult.Feedback;

                outcomes = legacyResult.Outcomes.Select(o => new CriterionOutcomeDto
                {
                    CriterionCode = o.CriterionId,
                    CriterionName = o.Name,
                    MaxPoints = Math.Round(input.MaxPoints / Math.Max(1, legacyResult.Outcomes.Count), 2),
                    AwardedPoints = o.IsMet ? Math.Round(input.MaxPoints / Math.Max(1, legacyResult.Outcomes.Count), 2) : 0m,
                    IsMet = o.IsMet,
                    Explanation = o.IsMet
                        ? $"Khớp từ khóa: {string.Join(", ", o.MatchedKeywords)}"
                        : "Chưa nêu đúng từ khóa trọng tâm."
                }).ToList();
            }
            else
            {
                feedback = "Không tìm thấy rubric cho câu hỏi này trong hệ thống.";
                strengths = string.Empty;
                weaknesses = string.Empty;
            }
        }
        else
        {
            feedback = "Không có tiêu chí rubric được cung cấp để đánh giá câu trả lời.";
            strengths = string.Empty;
            weaknesses = string.Empty;
        }

        var result = new QuestionGradingResultDto
        {
            Id = Guid.NewGuid(),
            SessionId = input.SessionId,
            QuestionId = input.QuestionId,
            QuestionTitle = input.QuestionText,
            BloomLevel = input.BloomLevel,
            MaxPoints = input.MaxPoints,
            AiScore = totalScore,
            AiFeedback = feedback,
            Strengths = strengths,
            Weaknesses = weaknesses,
            CriteriaOutcomes = outcomes,
            AiProvider = "Mock",
            AiModel = "mock-rubric-v1",
            State = GradingState.AiGraded
        };

        return Task.FromResult(result);
    }

    private static string CombineTranscripts(string? mainAnswer, IEnumerable<string>? followUpTurns)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(mainAnswer))
        {
            sb.Append(mainAnswer);
        }

        if (followUpTurns != null)
        {
            foreach (var turn in followUpTurns)
            {
                if (!string.IsNullOrWhiteSpace(turn))
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(". ");
                    }
                    sb.Append(turn);
                }
            }
        }

        return sb.ToString();
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
}

