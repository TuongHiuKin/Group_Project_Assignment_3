namespace AIVES.DAL.Entities;

public class VivaSessionTurn
{
    public Guid TurnId { get; set; }
    public Guid SessionId { get; set; }
    public string QuestionId { get; set; } = string.Empty;
    public int TurnOrdinal { get; set; }
    public string? AudioUrl { get; set; }
    public string? TranscriptText { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public string? FollowUpQuestionText { get; set; }
    public string? FollowUpAudioUrl { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public VivaSession? Session { get; set; }
    public VivaQuestion? Question { get; set; }
}
