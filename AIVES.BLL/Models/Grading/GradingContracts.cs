namespace AIVES.BLL.Models.Grading;

// Shared Group 4 contracts copied verbatim from docs/G4-CONTRACTS.md §3.2–3.3.
// Owned by the Tech Lead; added here so the background queue and realtime events (RT-01..RT-06) compile
// before LEAD-03 lands. If the Lead's version differs, the Lead's version wins.

public enum GradingState : byte
{
    Pending = 0,    // Queued or awaiting grading
    AiGraded = 1,   // Graded by AI/fallback, awaiting instructor review
    Finalized = 2,  // Confirmed or overridden by teacher (Official)
    Failed = 3      // AI evaluation failed and pending manual review/regrade
}

public sealed class CriterionOutcomeDto
{
    public string CriterionCode { get; set; } = string.Empty;
    public string CriterionName { get; set; } = string.Empty;
    public decimal MaxPoints { get; set; }
    public decimal AwardedPoints { get; set; }
    public bool IsMet { get; set; }
    public string Explanation { get; set; } = string.Empty;
}

public sealed class SpeechMetricsDto
{
    public double AnswerDurationSeconds { get; set; }
    public double SpeakingSpeedWpm { get; set; }
    public int FillerWordsCount { get; set; }
    public double FillerWordsRatio { get; set; }
    public List<string> IdentifiedFillerWords { get; set; } = new();
}

public sealed class QuestionGradingResultDto
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public string QuestionTitle { get; set; } = string.Empty;
    public string? BloomLevel { get; set; }
    public decimal MaxPoints { get; set; } = 10m;

    // AI Assessment
    public decimal? AiScore { get; set; }
    public string? AiFeedback { get; set; }
    public string? Strengths { get; set; }
    public string? Weaknesses { get; set; }
    public List<CriterionOutcomeDto> CriteriaOutcomes { get; set; } = new();
    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public GradingState State { get; set; } = GradingState.Pending;

    // Supplementary Signal
    public SpeechMetricsDto? SpeechMetrics { get; set; }

    // Teacher Finalization
    public decimal? TeacherFinalScore { get; set; }
    public string? TeacherNotes { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }

    // Concurrency Token (Base64)
    public string? RowVersion { get; set; }

    public decimal? EffectiveScore => TeacherFinalScore ?? AiScore;
    public bool IsFinalized => State == GradingState.Finalized;
}

public sealed class SessionGradingSummaryDto
{
    public Guid SessionId { get; set; }
    public string ExamId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public string StudentName { get; set; } = string.Empty;
    public DateTime SessionCompletedAtUtc { get; set; }

    public decimal TotalMaxScore { get; set; }
    public decimal? TotalAiScore { get; set; }
    public decimal? TotalFinalScore { get; set; }
    public decimal? EffectiveTotalScore => TotalFinalScore ?? TotalAiScore;

    public GradingState OverallState { get; set; }
    public bool IsFullyFinalized { get; set; }
    public List<QuestionGradingResultDto> QuestionResults { get; set; } = new();
}

public sealed class FinalizeScoreRequestDto
{
    public Guid SessionId { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public decimal FinalScore { get; set; }
    public string? TeacherNotes { get; set; }
    public string? ChangeReason { get; set; }
    public string? ExpectedRowVersion { get; set; }
}
