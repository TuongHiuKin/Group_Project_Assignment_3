namespace AIVES.DAL.Entities;

public class VivaSession
{
    public Guid SessionId { get; set; }
    public string ExamId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public byte State { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public ICollection<VivaSessionTurn> Turns { get; set; } = new List<VivaSessionTurn>();
    public ICollection<VivaGradingResult> GradingResults { get; set; } = new List<VivaGradingResult>();
}
