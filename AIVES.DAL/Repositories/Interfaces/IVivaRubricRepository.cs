using AIVES.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIVES.DAL.Repositories.Interfaces;
public interface IVivaRubricRepository
{
    Task<VivaRubricCriterion?> GetByIdAsync(string id, CancellationToken ct = default);
    Task<List<VivaRubricCriterion>> GetByCriterionAsync(int criterionId, CancellationToken ct = default);
    Task<int> GetCriteriaByQuestionIdAsync(string questionId, CancellationToken ct = default);
}
