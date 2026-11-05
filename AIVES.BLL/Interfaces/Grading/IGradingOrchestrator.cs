using AIVES.BLL.Models.Grading;

namespace AIVES.BLL.Interfaces.Grading;

public interface IGradingOrchestrator
{
    Task<SessionGradingSummaryDto> GradeSessionAsync(
        Guid sessionId, 
        CancellationToken ct = default);

    Task<QuestionGradingResultDto> GradeQuestionAsync(
        Guid sessionId, 
        string questionId, 
        CancellationToken ct = default);

    Task<QuestionGradingResultDto> RegradeQuestionAsync(
        Guid sessionId, 
        string questionId, 
        string requestedBy, 
        CancellationToken ct = default);

    Task<QuestionGradingResultDto> FinalizeScoreAsync(
        FinalizeScoreRequestDto request, 
        string teacherId, 
        CancellationToken ct = default);

    Task<SessionGradingSummaryDto?> GetSessionGradingAsync(
        Guid sessionId, 
        CancellationToken ct = default);

    Task<List<SessionGradingSummaryDto>> GetGradingListByExamAsync(
        string examId, 
        string? classId = null, 
        GradingState? filterState = null, 
        CancellationToken ct = default);
}
