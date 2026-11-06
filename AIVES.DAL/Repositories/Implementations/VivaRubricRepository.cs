using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace AIVES.DAL.Repositories.Implementations;

public sealed class VivaRubricRepository(AIVESDbContext context) : IVivaRubricRepository
{
    public async Task<List<VivaRubricCriterion>> GetByCriterionAsync(int criterionId, CancellationToken ct = default)
    {
        var query = context.VivaRubricCriterions.AsNoTracking();
        query = query.Where(x => x.CriterionCode == criterionId);
        return await query.OrderBy(x => x.QuestionId).ToListAsync(ct);
    }

    public async Task<VivaRubricCriterion?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        return await context.VivaRubricCriterions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    }

    public async Task<int> GetCriteriaByQuestionIdAsync(string questionId, CancellationToken ct = default)
    {
        var query = context.VivaRubricCriterions.AsNoTracking();
        query = query.Where(x => x.QuestionId == questionId);
        return query.First().CriterionCode;
    }
}
