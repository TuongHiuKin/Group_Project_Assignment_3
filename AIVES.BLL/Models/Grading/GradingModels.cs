namespace AIVES.BLL.Models.Grading;

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

public sealed class QuestionTurnDetailDto
{
    public Guid TurnId { get; set; }
    public int TurnOrdinal { get; set; }
    public string? AudioUrl { get; set; }
    public string? TranscriptText { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public string? FollowUpQuestionText { get; set; }
    public string? FollowUpAudioUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
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

    // Content & Turns (for Review UI)
    public string? QuestionContent { get; set; }
    public string? StudentAnswerTranscript { get; set; }
    public List<QuestionTurnDetailDto> Turns { get; set; } = new();

    // Teacher Finalization
    public decimal? TeacherFinalScore { get; set; }
    public string? TeacherNotes { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }

    // Concurrency Token (Base64)
    public string? RowVersion { get; set; }

    // Audit Change Logs (for UI-04)
    public List<GradeChangeLogDto> ChangeLogs { get; set; } = new();

    // Calculated Property for Effective Score
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

    public List<GradeChangeLogDto> AllChangeLogs => QuestionResults.SelectMany(q => q.ChangeLogs).OrderByDescending(l => l.ChangedAtUtc).ToList();
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

public sealed class GradeChangeLogDto
{
    public long Id { get; set; }
    public Guid GradingResultId { get; set; }
    public string? QuestionId { get; set; }
    public string? QuestionTitle { get; set; }
    public decimal? OldScore { get; set; }
    public decimal NewScore { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime ChangedAtUtc { get; set; }
}

public sealed class GradingInputDto
{
    public Guid SessionId { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public string QuestionText { get; set; } = string.Empty;
    public string? BloomLevel { get; set; }
    public decimal MaxPoints { get; set; } = 10m;
    public string StudentAnswerTranscript { get; set; } = string.Empty;
    public List<string> FollowUpTurns { get; set; } = new();
    public List<RubricCriterionDto> RubricCriteria { get; set; } = new();
    public double AnswerDurationSeconds { get; set; }
}

public sealed class RubricCriterionDto
{
    public string CriterionCode { get; set; } = string.Empty;
    public string CriterionName { get; set; } = string.Empty;
    public decimal MaxPoints { get; set; }
    public decimal Weight { get; set; } = 1.0m;
    public string? Description { get; set; }
    public List<string> Keywords { get; set; } = new();
}
