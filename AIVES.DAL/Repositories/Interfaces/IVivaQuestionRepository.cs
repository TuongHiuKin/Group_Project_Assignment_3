using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaQuestionRepository
{
    Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default);
    Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default);
    Task<bool> ExamExistsAsync(string examId, CancellationToken ct = default);
    Task AddQuestionsAsync(IEnumerable<VivaQuestion> questions, CancellationToken ct = default);
    Task<List<string>> GetAllExamIdsAsync(CancellationToken ct = default);
    Task<List<VivaQuestion>> GetAllQuestionsAsync(CancellationToken ct = default);
}
