using AIVES.BLL.Models.Grading;

namespace AIVES.BLL.Interfaces.Ai;

public interface IAnswerGradingService
{
    Task<QuestionGradingResultDto> GradeAnswerAsync(
        GradingInputDto input, 
        CancellationToken ct = default);
}
