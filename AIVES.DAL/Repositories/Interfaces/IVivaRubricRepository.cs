using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaRubricRepository
{
    Task<List<VivaRubricCriterion>> GetCriteriaByQuestionAsync(string questionId, CancellationToken ct = default);
    Task<List<VivaRubricCriterion>> GetCriteriaByExamAsync(string examId, CancellationToken ct = default);
}
