using System.Text.Json.Serialization;

namespace AIVES.BLL.Models.Grading;

/// <summary>
/// Cấu trúc DTO trung gian ánh xạ trực tiếp từ phản hồi JSON của các mô hình LLM (Gemini / OpenAI).
/// </summary>
public sealed class AnswerGradingLlmResponse
{
    [JsonPropertyName("totalScore")]
    public decimal TotalScore { get; set; }

    [JsonPropertyName("feedback")]
    public string? Feedback { get; set; }

    [JsonPropertyName("strengths")]
    public string? Strengths { get; set; }

    [JsonPropertyName("weaknesses")]
    public string? Weaknesses { get; set; }

    [JsonPropertyName("criteriaOutcomes")]
    public List<CriterionOutcomeLlmDto> CriteriaOutcomes { get; set; } = new();
}

public sealed class CriterionOutcomeLlmDto
{
    [JsonPropertyName("criterionCode")]
    public string CriterionCode { get; set; } = string.Empty;

    [JsonPropertyName("criterionName")]
    public string? CriterionName { get; set; }

    [JsonPropertyName("maxPoints")]
    public decimal MaxPoints { get; set; }

    [JsonPropertyName("awardedPoints")]
    public decimal AwardedPoints { get; set; }

    [JsonPropertyName("isMet")]
    public bool IsMet { get; set; }

    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
}

