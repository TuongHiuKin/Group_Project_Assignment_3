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

        public async Task CompleteSessionAsync(Guid sessionId, DateTime completedAtUtc, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(sessionId, ct) ?? throw new NullReferenceException("Cannot find the session to update");
            entity.CompletedAtUtc = completedAtUtc;
            await SaveChangesSafeAsync(ct);
        }

        public async Task<Guid> CreateSessionAsync(VivaSession session, CancellationToken ct = default)
        {
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

        public async Task UpdateStateAsync(Guid sessionId, byte state, CancellationToken ct = default)
        {
            var entity = await GetByIdAsync(sessionId, ct) ?? throw new NullReferenceException("Cannot find the session to update");
            entity.State = state;
            await SaveChangesSafeAsync(ct);
        }
    }
}
