namespace AIVES.BLL.Models.ExamReporting;

/// <summary>
/// Một tiêu chí Rubric: đạt khi transcript khớp ít nhất một từ khóa trong nhóm.
/// </summary>
public sealed class RubricCriterion
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string ImprovementHint { get; init; } = string.Empty;
    public decimal Weight { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
}

public sealed class QuestionRubric
{
    public string QuestionId { get; init; } = string.Empty;
    public string ExamId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;

    /// <summary>Mức nhận thức theo thang Bloom do giảng viên gắn cho câu hỏi (Nhớ, Hiểu, Vận dụng, Phân tích).</summary>
    public string BloomLevel { get; init; } = string.Empty;

    public decimal MaxScore { get; init; } = 10.0m;
    public IReadOnlyList<RubricCriterion> Criteria { get; init; } = Array.Empty<RubricCriterion>();

    public decimal TotalWeight => Criteria.Sum(c => c.Weight);

    /// <summary>Điểm tối đa của một tiêu chí khi quy về thang <see cref="MaxScore"/>.</summary>
    public decimal PointsFor(RubricCriterion criterion) =>
        TotalWeight > 0 ? Math.Round(criterion.Weight * MaxScore / TotalWeight, 2) : 0m;
}

public sealed class RubricCriterionOutcome
{
    public string CriterionId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsMet { get; init; }
    public IReadOnlyList<string> MatchedKeywords { get; init; } = Array.Empty<string>();
}

public sealed class RubricGradingResult
{
    public string QuestionId { get; init; } = string.Empty;
    public decimal EarnedScore { get; init; }
    public decimal MaxScore { get; init; } = 10.0m;
    public decimal PassThresholdScore { get; init; }
    public bool IsPassed { get; init; }

    /// <summary>False khi transcript rỗng hoặc chỉ lặp lại câu hỏi.</summary>
    public bool HasRelevantAnswer { get; init; }

    public IReadOnlyList<RubricCriterionOutcome> Outcomes { get; init; } = Array.Empty<RubricCriterionOutcome>();
    public string Strengths { get; init; } = string.Empty;
    public string Weaknesses { get; init; } = string.Empty;
    public string Feedback { get; init; } = string.Empty;
    public string RubricDescription { get; init; } = string.Empty;
}
