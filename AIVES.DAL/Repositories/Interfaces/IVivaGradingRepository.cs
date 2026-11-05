using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaGradingRepository
{
    Task<List<VivaGradingResult>> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default);
    Task<VivaGradingResult?> GetByQuestionAsync(Guid sessionId, string questionId, CancellationToken ct = default);
    Task<List<VivaGradingResult>> GetByExamIdAsync(string examId, CancellationToken ct = default);
    Task<List<Guid>> GetPendingOrFailedSessionIdsAsync(int limit = 50, CancellationToken ct = default);

    Task<VivaGradingResult> UpsertAiResultAsync(VivaGradingResult result, CancellationToken ct = default);
    Task<VivaGradingResult> FinalizeScoreAsync(
        Guid sessionId, 
        string questionId, 
        decimal finalScore, 
        string teacherNotes, 
        string teacherId, 
        byte[]? expectedRowVersion, 
        string? changeReason, 
        CancellationToken ct = default);

    Task<List<VivaGradeChangeLog>> GetChangeLogsAsync(Guid gradingResultId, CancellationToken ct = default);
}
