using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AIVES.DAL.Repositories.Implementations
{
    internal class VivaTurnRepository(AIVESDbContext context) : IVivaTurnRepository
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
        public async Task<Guid> CreateTurnAsync(VivaSessionTurn turn, CancellationToken ct = default)
        {
            context.VivaSessionTurns.Add(turn);
            await SaveChangesSafeAsync(ct);
            return turn.TurnId;
        }

        public async Task<VivaSessionTurn?> GetByIdAsync(Guid turnId, CancellationToken ct = default)
        {
            return await context.VivaSessionTurns.AsNoTracking().SingleOrDefaultAsync(x => x.TurnId == turnId, ct);
        }

        private async Task<VivaSessionTurn?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
        {
            return await context.VivaSessionTurns.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        }

        public async Task<int> GetTurnCountForSessionAsync(Guid sessionId, CancellationToken ct = default)
        {
            var entity = await GetBySessionIdAsync(sessionId, ct);
            if (entity == null) return 0;
            return entity.TurnOrdinal;
        }

        public async Task SaveFollowUpAsync(Guid turnId, string followUpQuestion, string? audioPath, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(turnId, ct) ?? throw new NullReferenceException("Cannot find the session turn to update");
            entity.FollowUpQuestionText = followUpQuestion.Trim();
            if (!audioPath.IsNullOrEmpty())
            {
                entity.AudioUrl = audioPath.Trim();
            }
            await SaveChangesSafeAsync(ct);
        }

        public async Task SaveTranscriptAsync(Guid turnId, string transcript, decimal confidence, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(turnId, ct) ?? throw new NullReferenceException("Cannot find the session turn to update");
            entity.TranscriptText = transcript.Trim();
            entity.ConfidenceScore = confidence;
            await SaveChangesSafeAsync(ct);
        }
    }
}
