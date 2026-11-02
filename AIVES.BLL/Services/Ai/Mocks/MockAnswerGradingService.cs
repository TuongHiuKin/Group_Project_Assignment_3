using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.ExamReporting;

namespace AIVES.BLL.Services.Ai.Mocks;

/// <summary>
/// Deterministic mock answer grading service for development, CI/CD, and offline testing.
/// Evaluates answers against keyword rubrics using <see cref="IRubricGradingService"/>
/// and constructs a fully detailed <see cref="QuestionGradingResultDto"/>.
/// </summary>
public sealed class MockAnswerGradingService : IAnswerGradingService
{
    private readonly IRubricGradingService _rubricService;

    public MockAnswerGradingService(IRubricGradingService? rubricService = null)
    {
        _rubricService = rubricService ?? new RubricGradingService();
    }

    public Task<QuestionGradingResultDto> GradeAnswerAsync(
        GradingInputDto input, 
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var rubric = _rubricService.GetRubric(input.QuestionId);
        var rubricResult = _rubricService.Grade(
            input.QuestionId,
            input.QuestionText,
            input.StudentAnswerTranscript,
            6.0m);

        if (rubricResult != null)
        {
            var outcomes = rubricResult.Outcomes.Select(o =>
            {
                var crit = rubric?.Criteria.FirstOrDefault(c => string.Equals(c.Id, o.CriterionId, StringComparison.OrdinalIgnoreCase));
                var points = (crit != null && rubric != null)
                    ? rubric.PointsFor(crit)
                    : Math.Round(input.MaxPoints / Math.Max(1, rubricResult.Outcomes.Count), 2);

                return new CriterionOutcomeDto
                {
                    CriterionCode = o.CriterionId,
                    CriterionName = o.Name,
                    MaxPoints = points,
                    AwardedPoints = o.IsMet ? points : 0m,
                    IsMet = o.IsMet,
                    Explanation = o.IsMet
                        ? (o.MatchedKeywords.Count > 0 ? $"Khớp từ khóa: {string.Join(", ", o.MatchedKeywords)}" : "Đạt yêu cầu tiêu chí")
                        : "Chưa phát hiện từ khóa phù hợp trong câu trả lời"
                };
            }).ToList();

            var strengths = !string.IsNullOrWhiteSpace(rubricResult.Strengths)
                ? rubricResult.Strengths
                : "Sinh viên nắm được một số từ khóa cơ bản của câu hỏi.";

            var weaknesses = !string.IsNullOrWhiteSpace(rubricResult.Weaknesses)
                ? rubricResult.Weaknesses
                : "Cần diễn giải sâu hơn về bản chất kỹ thuật và cơ chế hoạt động.";

            return Task.FromResult(new QuestionGradingResultDto
            {
                SessionId = input.SessionId,
                QuestionId = input.QuestionId,
                QuestionTitle = input.QuestionText,
                BloomLevel = input.BloomLevel ?? "Vận dụng",
                MaxPoints = input.MaxPoints > 0 ? input.MaxPoints : 10.0m,
                AiScore = rubricResult.EarnedScore,
                AiFeedback = rubricResult.Feedback,
                Strengths = strengths,
                Weaknesses = weaknesses,
                CriteriaOutcomes = outcomes,
                AiProvider = "Mock",
                AiModel = "mock-rubric-grading-v1",
                State = GradingState.AiGraded
            });
        }

        // Generic fallback when rubric is not in catalog
        var defaultOutcomes = new List<CriterionOutcomeDto>
        {
            new()
            {
                CriterionCode = "C1",
                CriterionName = "Mức độ hiểu bài",
                MaxPoints = 5.0m,
                AwardedPoints = 4.0m,
                IsMet = true,
                Explanation = "Trả lời đúng trọng tâm câu hỏi vấn đáp."
            },
            new()
            {
                CriterionCode = "C2",
                CriterionName = "Vận dụng thực tế",
                MaxPoints = 5.0m,
                AwardedPoints = 3.5m,
                IsMet = true,
                Explanation = "Có dẫn chứng minh họa cụ thể."
            }
        };

        return Task.FromResult(new QuestionGradingResultDto
        {
            SessionId = input.SessionId,
            QuestionId = input.QuestionId,
            QuestionTitle = input.QuestionText,
            BloomLevel = input.BloomLevel ?? "Vận dụng",
            MaxPoints = input.MaxPoints > 0 ? input.MaxPoints : 10.0m,
            AiScore = 7.5m,
            AiFeedback = "Câu trả lời tương đối đầy đủ và mạch lạc, đáp ứng yêu cầu cơ bản của đề thi.",
            Strengths = "Trình bày rõ ràng, nắm được kiến thức nền tảng.",
            Weaknesses = "Cần phân tích thêm các trường hợp biên và tối ưu hiệu năng.",
            CriteriaOutcomes = defaultOutcomes,
            AiProvider = "Mock",
            AiModel = "mock-rubric-grading-v1",
            State = GradingState.AiGraded
        });
    }
}
