using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Repositories.Interfaces;

namespace AIVES.BLL.Services.Grading;

/// <summary>
/// G4-RT-04: a completed session needs (re)queuing when it has no grading rows yet, a Pending row,
/// or an answered question without a row (app stopped between commit and enqueue, or mid-grading).
/// Failed rows are NOT auto-requeued (avoids endless loops); they wait for a lecturer regrade.
/// </summary>
public sealed class GradingRecoveryScanner(
    IVivaSessionRepository sessions,
    IGradingOrchestrator? orchestrator = null) : IGradingRecoveryScanner
{
    public async Task<IReadOnlyList<GradingJob>> FindUnfinishedJobsAsync(CancellationToken ct = default)
    {
        if (orchestrator is null) return Array.Empty<GradingJob>();

        var completed = (await sessions.GetSessionsWithDetailsAsync(ct: ct))
            .Where(s => s.State == (byte)VivaSessionState.Completed)
            .OrderBy(s => s.CompletedAtUtc ?? s.StartedAtUtc);

        var jobs = new List<GradingJob>();
        foreach (var session in completed)
        {
            ct.ThrowIfCancellationRequested();
            var summary = await orchestrator.GetSessionGradingAsync(session.SessionId, ct);
            var graded = summary?.QuestionResults
                .Where(r => r.State != GradingState.Pending)
                .Select(r => r.QuestionId)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

            var answered = session.Turns.Select(t => t.QuestionId).Where(q => !string.IsNullOrWhiteSpace(q)).Distinct();
            var unfinished = summary is null || summary.QuestionResults.Count == 0
                || summary.QuestionResults.Any(r => r.State == GradingState.Pending)
                || answered.Any(q => !graded.Contains(q));

            if (unfinished) jobs.Add(new GradingJob(session.SessionId, ExamId: session.ExamId));
        }
        return jobs;
    }
}
