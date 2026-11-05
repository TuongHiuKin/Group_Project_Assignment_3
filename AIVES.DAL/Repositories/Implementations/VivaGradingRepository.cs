using AIVES.DAL.Context;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace AIVES.DAL.Repositories.Implementations;

public sealed class VivaGradingRepository(AIVESDbContext context) : IVivaGradingRepository
{
    private static readonly byte PendingGradingState = 0;
    private static readonly byte FinalizedGradingState = 2;
    private static readonly byte FailedGradingState = 3;
    private static readonly byte CompletedSessionState = 8; // VivaSessionState.Completed

    public Task<List<VivaGradingResult>> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default) =>
        context.VivaGradingResults.AsNoTracking()
            .Include(x => x.Question)
            .Where(x => x.SessionId == sessionId)
            .OrderBy(x => x.QuestionId)
            .ToListAsync(ct);

    public Task<VivaGradingResult?> GetByQuestionAsync(Guid sessionId, string questionId, CancellationToken ct = default) =>
        context.VivaGradingResults.AsNoTracking()
            .Include(x => x.Question)
            .SingleOrDefaultAsync(x => x.SessionId == sessionId && x.QuestionId == questionId, ct);

    public Task<List<VivaGradingResult>> GetByExamIdAsync(string examId, CancellationToken ct = default) =>
        context.VivaGradingResults.AsNoTracking()
            .Include(x => x.Session)
            .Include(x => x.Question)
            .Where(x => x.Session != null && x.Session.ExamId == examId)
            .OrderBy(x => x.SessionId)
            .ThenBy(x => x.QuestionId)
            .ToListAsync(ct);

    public async Task<List<Guid>> GetPendingOrFailedSessionIdsAsync(int limit = 50, CancellationToken ct = default)
    {
        if (limit <= 0) limit = 50;

        // Sessions that are Completed but either have no grading results or have results with Pending/Failed state
        var completedSessionsQuery = context.VivaSessions.AsNoTracking()
            .Where(s => s.State == CompletedSessionState);

        var pendingOrFailedSessionIds = await completedSessionsQuery
            .Where(s => !context.VivaGradingResults.Any(g => g.SessionId == s.SessionId)
                     || context.VivaGradingResults.Any(g => g.SessionId == s.SessionId &&
                            (g.GradingState == PendingGradingState || g.GradingState == FailedGradingState)))
            .OrderByDescending(s => s.CompletedAtUtc ?? s.StartedAtUtc)
            .Select(s => s.SessionId)
            .Take(limit)
            .ToListAsync(ct);

        return pendingOrFailedSessionIds;
    }

    public async Task<VivaGradingResult> UpsertAiResultAsync(VivaGradingResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var existing = await context.VivaGradingResults
            .SingleOrDefaultAsync(x => x.SessionId == result.SessionId && x.QuestionId == result.QuestionId, ct);

        if (existing is null)
        {
            if (result.Id == Guid.Empty)
            {
                result.Id = Guid.NewGuid();
            }

            context.VivaGradingResults.Add(result);
            await SaveChangesSafeAsync(ct);
            return result;
        }

        // Do not overwrite finalized question
        if (existing.GradingState == FinalizedGradingState)
        {
            return existing;
        }

        existing.AiScore = result.AiScore;
        existing.AiFeedback = result.AiFeedback;
        existing.Strengths = result.Strengths;
        existing.Weaknesses = result.Weaknesses;
        existing.CriteriaOutcomesJson = result.CriteriaOutcomesJson;
        existing.AiProvider = result.AiProvider;
        existing.AiModel = result.AiModel;
        existing.GradingState = result.GradingState;

        await SaveChangesSafeAsync(ct);
        return existing;
    }

    public async Task<VivaGradingResult> FinalizeScoreAsync(
        Guid sessionId,
        string questionId,
        decimal finalScore,
        string teacherNotes,
        string teacherId,
        byte[]? expectedRowVersion,
        string? changeReason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(teacherId);

        var existing = await context.VivaGradingResults
            .SingleOrDefaultAsync(x => x.SessionId == sessionId && x.QuestionId == questionId, ct)
            ?? throw new InvalidOperationException($"Grading result not found for session {sessionId} and question {questionId}.");

        // Optimistic concurrency check
        if (expectedRowVersion is not null && expectedRowVersion.Length > 0)
        {
            if (existing.RowVersion is not null && !existing.RowVersion.SequenceEqual(expectedRowVersion))
            {
                throw new DbUpdateConcurrencyException(
                    "The grading result was modified by another instructor. Please refresh the page and review the updated score.");
            }

            context.Entry(existing).OriginalValues[nameof(VivaGradingResult.RowVersion)] = expectedRowVersion;
        }

        // Mandatory reason when modifying an already finalized score
        if (existing.GradingState == FinalizedGradingState && string.IsNullOrWhiteSpace(changeReason))
        {
            throw new ArgumentException("A non-empty reason is mandatory when modifying an already finalized score.", nameof(changeReason));
        }

        // Record audit change log
        var log = new VivaGradeChangeLog
        {
            GradingResultId = existing.Id,
            OldScore = existing.TeacherFinalScore ?? existing.AiScore,
            NewScore = finalScore,
            ChangedBy = teacherId,
            Reason = string.IsNullOrWhiteSpace(changeReason) ? "Xác nhận điểm ban đầu" : changeReason.Trim(),
            ChangedAtUtc = DateTime.UtcNow
        };
        context.VivaGradeChangeLogs.Add(log);

        existing.TeacherFinalScore = finalScore;
        existing.TeacherNotes = teacherNotes;
        existing.FinalizedBy = teacherId;
        existing.FinalizedAtUtc = DateTime.UtcNow;
        existing.GradingState = FinalizedGradingState;

        await SaveChangesSafeAsync(ct);
        return existing;
    }

    public Task<List<VivaGradeChangeLog>> GetChangeLogsAsync(Guid gradingResultId, CancellationToken ct = default) =>
        context.VivaGradeChangeLogs.AsNoTracking()
            .Where(x => x.GradingResultId == gradingResultId)
            .OrderByDescending(x => x.ChangedAtUtc)
            .ToListAsync(ct);

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
}
