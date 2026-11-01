namespace AIVES.DAL.Entities;

public class VivaGradeChangeLog
{
    public long Id { get; set; }
    public Guid GradingResultId { get; set; }
    public decimal? OldScore { get; set; }
    public decimal NewScore { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime ChangedAtUtc { get; set; }

    public VivaGradingResult? GradingResult { get; set; }
}
