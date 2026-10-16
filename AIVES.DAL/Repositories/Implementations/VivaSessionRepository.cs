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
    internal class VivaSessionRepository(AIVESDbContext context) : IVivaSessionRepository
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

        public async Task<bool> CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(sessionId, ct);
            if (entity is null) return false;
            entity.CompletedAtUtc = completedAtUtc;
            await SaveChangesSafeAsync(ct);
            return true;
        }

        public async Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(session.SessionId, ct);
            if (entity is not null) return session.SessionId;
            context.VivaSessions.Add(session);
            await SaveChangesSafeAsync(ct);
            return session.SessionId;
        }

        public async Task<VivaSession?> GetByIdAsync(Guid sessionId, CancellationToken ct = default)
        {
            return await context.VivaSessions.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        }

        public async Task<VivaSession?> GetWithDetailsAsync(Guid sessionId, CancellationToken ct = default)
        {
            return await context.VivaSessions.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == sessionId, ct);
        }

        public async Task<bool> UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(sessionId, ct);
            if (entity is null) return false;
            entity.State = state;
            await SaveChangesSafeAsync(ct);
            return true;
        }
    }
}
