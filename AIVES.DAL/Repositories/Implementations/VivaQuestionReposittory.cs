using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIVES.DAL.Repositories.Implementations
{
    internal class VivaQuestionReposittory(AIVESDbContext context) : IVivaQuestionRepository
    {
        private async Task SaveChangesSafeAsync(CancellationToken ct)
        {
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

        public async Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default)
        {
            return await context.VivaQuestions.AsNoTracking().SingleOrDefaultAsync(x => x.QuestionId == questionId, ct);
        }

        public async Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default)
        {
            var query = context.VivaQuestions.AsNoTracking();
            query = query.Where(x => x.ExamId == examId);
            return await query.OrderBy(x => x.QuestionId).ToListAsync(ct);
        }

        public async Task<bool> UpdateAsync(string questionId, string title, string content, string audioUrl, int timeLimitSecond, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(questionId, ct);
            if (entity is null) return false;
            entity.Title = title;
            entity.Content = content;
            entity.TimeLimitSeconds = timeLimitSecond;
            entity.AudioUrl = audioUrl;
            await SaveChangesSafeAsync(ct);
            return true;
        }
    }
}
