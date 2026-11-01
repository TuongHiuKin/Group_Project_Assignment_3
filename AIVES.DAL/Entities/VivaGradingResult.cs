namespace AIVES.DAL.Entities;

public class VivaGradingResult
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public string QuestionId { get; set; } = string.Empty;

    public decimal? AiScore { get; set; }
    public string? AiFeedback { get; set; }
    public string? Strengths { get; set; }
    public string? Weaknesses { get; set; }
    public string? CriteriaOutcomesJson { get; set; }

    public string? AiProvider { get; set; }
    public string? AiModel { get; set; }
    public byte GradingState { get; set; }

    public decimal? TeacherFinalScore { get; set; }
    public string? TeacherNotes { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedAtUtc { get; set; }

    public byte[]? RowVersion { get; set; }

    public VivaSession? Session { get; set; }
    public VivaQuestion? Question { get; set; }
}
