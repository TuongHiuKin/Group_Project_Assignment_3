namespace AIVES.DAL.Entities;

public class VivaQuestion
{
    public string QuestionId { get; set; } = string.Empty;
    public string ExamId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int TimeLimitSeconds { get; set; } = 90;
    public string? AudioUrl { get; set; }

    public ICollection<VivaSessionTurn> Turns { get; set; } = new List<VivaSessionTurn>();
}
