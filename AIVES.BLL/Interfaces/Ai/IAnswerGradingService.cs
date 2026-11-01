using AIVES.BLL.Models.Grading;

namespace AIVES.BLL.Interfaces.Ai;

/// <summary>
/// Service abstraction for AI-assisted scoring and grading of student viva exam answers.
/// </summary>
public interface IAnswerGradingService
{
    Task<QuestionGradingResultDto> GradeAnswerAsync(
        GradingInputDto input, 
        CancellationToken ct = default);
}

