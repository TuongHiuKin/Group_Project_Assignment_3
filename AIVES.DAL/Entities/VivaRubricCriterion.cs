namespace AIVES.DAL.Entities;

public class VivaRubricCriterion
{
    public string Id { get; set; } = string.Empty;
    public string QuestionId { get; set; } = string.Empty;
    public string CriterionCode { get; set; } = string.Empty;
    public string CriterionName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Weight { get; set; }
    public decimal MaxPoints { get; set; }
    public string? KeywordsJson { get; set; }

    public VivaQuestion? Question { get; set; }
}
