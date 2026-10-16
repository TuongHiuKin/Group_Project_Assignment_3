using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaQuestionRepository
{
    Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default);
    Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default);
    Task<bool> UpdateAsync(string questionId, string title, string content, string audioUrl, int timeLimitSecond, CancellationToken ct = default);
}
