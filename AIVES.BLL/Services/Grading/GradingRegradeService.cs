using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Repositories.Interfaces;

namespace AIVES.BLL.Services.Grading;

/// <summary>
/// G4-RT-03: validates and queues a lecturer "Chấm lại bằng AI" request. Returns immediately;
/// the background worker re-grades and broadcasts GradingStarted/GradingCompleted.
/// Guards: no duplicate while queued/running, never touches Finalized scores.
/// </summary>
public sealed class GradingRegradeService(
    IGradingQueue queue,
    IVivaSessionRepository sessions,
    IGradingOrchestrator? orchestrator = null) : IGradingRegradeService
{
    public async Task<RegradeRequestResult> RequestRegradeAsync(
        Guid sessionId, string? questionId, string requestedBy, CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(requestedBy)) throw new ArgumentException("Requester is required.", nameof(requestedBy));
        questionId = string.IsNullOrWhiteSpace(questionId) ? null : questionId.Trim();

        var session = await sessions.GetByIdAsync(sessionId, ct);
        if (session is null) return RegradeRequestResult.SessionNotFound;
        if (session.State != (byte)VivaSessionState.Completed) return RegradeRequestResult.SessionNotCompleted;
        if (orchestrator is null) return RegradeRequestResult.QueueUnavailable;
        if (queue.IsInFlight(sessionId, questionId)) return RegradeRequestResult.AlreadyInProgress;

        var summary = await orchestrator.GetSessionGradingAsync(sessionId, ct);
        var results = summary?.QuestionResults ?? new List<QuestionGradingResultDto>();
        if (questionId is not null && results.Any(r => r.QuestionId == questionId && r.State == GradingState.Finalized))
            return RegradeRequestResult.AlreadyFinalized;
        if (questionId is null && results.Count > 0 && results.All(r => r.State == GradingState.Finalized))
            return RegradeRequestResult.AlreadyFinalized;

        var job = new GradingJob(sessionId, questionId, GradingJobKind.Regrade, session.ExamId, requestedBy);
        if (queue.TryEnqueue(job)) return RegradeRequestResult.Queued;
        return queue.IsInFlight(sessionId, questionId)
            ? RegradeRequestResult.AlreadyInProgress
            : RegradeRequestResult.QueueUnavailable;
    }
}
