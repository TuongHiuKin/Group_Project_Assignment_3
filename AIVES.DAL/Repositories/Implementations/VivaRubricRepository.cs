using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AIVES.DAL.Repositories.Implementations;

public sealed class VivaRubricRepository(AIVESDbContext context) : IVivaRubricRepository
{
    public Task<List<VivaRubricCriterion>> GetCriteriaByQuestionAsync(string questionId, CancellationToken ct = default) =>
        context.VivaRubricCriteria.AsNoTracking()
            .Where(x => x.QuestionId == questionId)
            .OrderBy(x => x.CriterionCode)
            .ToListAsync(ct);

    public Task<List<VivaRubricCriterion>> GetCriteriaByExamAsync(string examId, CancellationToken ct = default)
    {
        var questionIds = context.VivaQuestions.AsNoTracking()
            .Where(q => q.ExamId == examId)
            .Select(q => q.QuestionId);

        return context.VivaRubricCriteria.AsNoTracking()
            .Where(x => questionIds.Contains(x.QuestionId))
            .OrderBy(x => x.QuestionId)
            .ThenBy(x => x.CriterionCode)
            .ToListAsync(ct);
    }
}
