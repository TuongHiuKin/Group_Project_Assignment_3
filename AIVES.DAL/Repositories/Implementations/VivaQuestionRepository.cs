using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AIVES.DAL.Repositories.Implementations;

public sealed class VivaQuestionRepository(AIVESDbContext context) : IVivaQuestionRepository
{
    public Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default) =>
        context.VivaQuestions.AsNoTracking().SingleOrDefaultAsync(x => x.QuestionId == questionId, ct);

    public Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default) =>
        context.VivaQuestions.AsNoTracking()
            .Where(x => x.ExamId == examId)
            .OrderBy(x => x.QuestionId)
            .ToListAsync(ct);

    public Task<bool> ExamExistsAsync(string examId, CancellationToken ct = default) =>
        context.VivaQuestions.AsNoTracking().AnyAsync(x => x.ExamId == examId, ct);

    public async Task AddQuestionsAsync(IEnumerable<VivaQuestion> questions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(questions);
        await context.VivaQuestions.AddRangeAsync(questions, ct);
        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch
        {
            context.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<List<string>> GetAllExamIdsAsync(CancellationToken ct = default) =>
        context.VivaQuestions.AsNoTracking()
            .Select(x => x.ExamId)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);

    public Task<List<VivaQuestion>> GetAllQuestionsAsync(CancellationToken ct = default) =>
        context.VivaQuestions.AsNoTracking()
            .OrderBy(x => x.ExamId)
            .ThenBy(x => x.QuestionId)
            .ToListAsync(ct);
}
