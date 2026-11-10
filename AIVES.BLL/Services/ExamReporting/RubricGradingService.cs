using System.Globalization;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.ExamReporting;
using AIVES.BLL.Models.ExamReporting;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.Extensions.Logging;

namespace AIVES.BLL.Services.ExamReporting;

/// <summary>
/// Chấm điểm theo Rubric từ khóa: chuẩn hóa transcript (chữ thường, bỏ dấu, bỏ dấu câu),
/// tiêu chí đạt khi khớp ít nhất một từ khóa, điểm câu = tổng trọng số đạt x thang điểm / tổng trọng số.
/// Rubric được đọc từ SQL Server (<c>VivaRubricCriteria</c>) khi repository khả dụng (G4-DB-07);
/// <see cref="RubricCatalog"/> chỉ còn là dự phòng khi bảng chưa được seed.
/// </summary>
public sealed class RubricGradingService : IRubricGradingService
{
    /// <summary>Số từ tối thiểu không có trong câu hỏi để coi là một câu trả lời thật.</summary>
    public const int MinNovelTokens = 3;

    public const string NoRelevantAnswerFeedback = "Chưa có câu trả lời phù hợp (câu trả lời rỗng hoặc chỉ lặp lại câu hỏi).";

    /// <summary>Nhận xét dự phòng khi AI lỗi: mô phỏng theo Rubric của giảng viên. Điểm và nhận xét chính thức do GradingOrchestrator (Nhóm 4) tạo.</summary>
    public const string FeedbackPrefix = "AI chấm gợi ý (mô phỏng theo Rubric của giảng viên): ";

    private static readonly JsonSerializerOptions KeywordsJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IVivaRubricRepository? _rubricRepo;
    private readonly IReadOnlyList<QuestionRubric> _rubrics;
    private readonly ILogger<RubricGradingService>? _logger;

    public RubricGradingService() : this(RubricCatalog.All)
    {
    }

    public RubricGradingService(IReadOnlyList<QuestionRubric> rubrics)
    {
        _rubrics = rubrics ?? throw new ArgumentNullException(nameof(rubrics));
    }

    /// <summary>Rubric DB-first: đọc <c>VivaRubricCriteria</c>, chỉ dùng <see cref="RubricCatalog"/> khi DB trống.</summary>
    public RubricGradingService(IVivaRubricRepository rubricRepo) : this(rubricRepo, RubricCatalog.All)
    {
    }

    public RubricGradingService(IVivaRubricRepository rubricRepo, IReadOnlyList<QuestionRubric> fallbacks)
        : this(rubricRepo, fallbacks, logger: null)
    {
    }

    public RubricGradingService(
        IVivaRubricRepository rubricRepo,
        IReadOnlyList<QuestionRubric> fallbacks,
        ILogger<RubricGradingService>? logger)
    {
        _rubricRepo = rubricRepo ?? throw new ArgumentNullException(nameof(rubricRepo));
        _rubrics = fallbacks ?? throw new ArgumentNullException(nameof(fallbacks));
        _logger = logger;
    }

    public IReadOnlyList<QuestionRubric> GetRubrics(string examId) =>
        _rubrics.Where(r => string.Equals(r.ExamId, examId, StringComparison.OrdinalIgnoreCase)).ToList();

    public QuestionRubric? GetRubric(string questionId) =>
        _rubrics.FirstOrDefault(r => string.Equals(r.QuestionId, questionId, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<QuestionRubric>> GetRubricsAsync(string examId, CancellationToken ct = default)
    {
        if (_rubricRepo is null) return GetRubrics(examId);

        try
        {
            var criteria = await _rubricRepo.GetCriteriaByExamAsync(examId, ct);
            var mapped = MapCriteria(criteria, examId);
            if (mapped.Count > 0)
            {
                return mapped;
            }

            LogFallback(examId);
            return GetRubrics(examId);
        }
        catch (Exception) when (_rubrics.Count > 0)
        {
            // A database that has not run 006 yet must not break grading or reporting.
            LogFallback(examId);
            return GetRubrics(examId);
        }
    }

    public async Task<QuestionRubric?> GetRubricAsync(string questionId, CancellationToken ct = default)
    {
        if (_rubricRepo is null) return GetRubric(questionId);

        try
        {
            var criteria = await _rubricRepo.GetCriteriaByQuestionAsync(questionId, ct);
            if (criteria.Count > 0)
            {
                return MapCriteria(criteria, examId: null).FirstOrDefault();
            }
        }
        catch (Exception) when (_rubrics.Count > 0)
        {
            // Fall through to the catalog fallback below.
        }

        LogFallback(questionId);
        return GetRubric(questionId);
    }

    /// <summary>Ghi cảnh báo khi rubric phải lấy từ <see cref="RubricCatalog"/> thay vì DB.</summary>
    private void LogFallback(string key) =>
        _logger?.LogWarning(
            "Rubric cho {Key} không có trong VivaRubricCriteria — dùng RubricCatalog dự phòng. " +
            "Chạy database/006-viva-rubric-criteria.sql để nạp rubric thật vào DB.",
            key);

    /// <summary>Chấm theo một rubric đã resolve sẵn (từ DB hoặc catalog) thay vì tra cứu lại catalog.</summary>
    public RubricGradingResult? Grade(
        QuestionRubric rubric,
        string? questionText,
        string? answerTranscript,
        decimal passThresholdScore)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        return GradeCore(rubric, questionText, answerTranscript, passThresholdScore);
    }

    public RubricGradingResult? Grade(string questionId, string? questionText, string? answerTranscript, decimal passThresholdScore)
    {
        var rubric = GetRubric(questionId);
        if (rubric == null)
        {
            return null;
        }

        return GradeCore(rubric, questionText, answerTranscript, passThresholdScore);
    }

    private static RubricGradingResult? GradeCore(
        QuestionRubric rubric,
        string? questionText,
        string? answerTranscript,
        decimal passThresholdScore)
    {
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
    public static string Normalize(string? text)    {
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

    /// <summary>
    /// Maps persisted <c>VivaRubricCriteria</c> rows (G4-DB-06/DB-07) to the BLL rubric model.
    /// One <see cref="QuestionRubric"/> per question; criteria keep their seeded order.
    /// </summary>
    private static List<QuestionRubric> MapCriteria(IReadOnlyList<VivaRubricCriterion> criteria, string? examId)
    {
        return criteria
            .Where(c => !string.IsNullOrWhiteSpace(c.QuestionId))
            .GroupBy(c => c.QuestionId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group
                    .OrderBy(c => c.CriterionCode, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                // 006 seeds MaxPoints as each criterion's share of the question scale (3.5 + 3.5 + 3.0 = 10),
                // so the question's maximum is the sum of its criteria points.
                var criteriaPoints = rows.Sum(r => r.MaxPoints);
                var maxScore = criteriaPoints > 0 ? criteriaPoints : 10m;
                var question = rows[0].Question;

                return new QuestionRubric
                {
                    QuestionId = group.Key,
                    ExamId = examId ?? question?.ExamId ?? string.Empty,
                    Title = question?.Title ?? string.Empty,
                    BloomLevel = question?.BloomLevel ?? string.Empty,
                    MaxScore = maxScore,
                    Criteria = rows.Select(row => new RubricCriterion
                    {
                        Id = string.IsNullOrWhiteSpace(row.CriterionCode) ? row.Id : row.CriterionCode,
                        Name = row.CriterionName,
                        Description = row.Description,
                        ImprovementHint = row.Description,
                        Weight = row.Weight,
                        Keywords = ParseKeywords(row.KeywordsJson)
                    }).ToList()
                };
            })
            .OrderBy(r => r.QuestionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ParseKeywords(string? keywordsJson)
    {
        if (string.IsNullOrWhiteSpace(keywordsJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            var keywords = JsonSerializer.Deserialize<List<string>>(keywordsJson, KeywordsJsonOptions);
            return keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
