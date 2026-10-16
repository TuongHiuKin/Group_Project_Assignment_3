using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AIVES.DAL.Repositories.Implementations;

public sealed class VivaTurnRepository(AIVESDbContext context) : IVivaTurnRepository
{
    public async Task<Guid> CreateTurnAsync(VivaSessionTurn turn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.TurnId == Guid.Empty) turn.TurnId = Guid.NewGuid();
        context.VivaSessionTurns.Add(turn);
        await SaveChangesSafeAsync(ct);
        return turn.TurnId;
    }

    public Task<VivaSessionTurn?> GetByIdAsync(Guid turnId, CancellationToken ct = default) =>
        context.VivaSessionTurns.AsNoTracking().SingleOrDefaultAsync(x => x.TurnId == turnId, ct);

    public async Task SaveTranscriptAsync(Guid turnId, string transcript, decimal confidence, CancellationToken ct = default)
    {
        var turn = await context.VivaSessionTurns.SingleOrDefaultAsync(x => x.TurnId == turnId, ct);
        if (turn is null) return;
        turn.TranscriptText = transcript;
        turn.ConfidenceScore = confidence;
        turn.CompletedAtUtc = DateTime.UtcNow;
        await SaveChangesSafeAsync(ct);
    }

    public async Task SaveFollowUpAsync(Guid turnId, string followUpQuestion, string? audioPath, CancellationToken ct = default)
    {
        var turn = await context.VivaSessionTurns.SingleOrDefaultAsync(x => x.TurnId == turnId, ct);
        if (turn is null) return;
        turn.FollowUpQuestionText = followUpQuestion;
        turn.FollowUpAudioUrl = audioPath;
        await SaveChangesSafeAsync(ct);
    }

    public Task<int> GetTurnCountForSessionAsync(Guid sessionId, CancellationToken ct = default) =>
        context.VivaSessionTurns.CountAsync(x => x.SessionId == sessionId, ct);

    private async Task SaveChangesSafeAsync(CancellationToken ct)
    {
        try { await context.SaveChangesAsync(ct); }
        catch { context.ChangeTracker.Clear(); throw; }
    }
}
