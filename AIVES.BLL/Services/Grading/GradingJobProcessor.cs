using System.Net;
using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Repositories.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Grading;

/// <summary>
/// Processes one <see cref="GradingJob"/> inside its own DI scope (G4-RT-01..RT-04):
/// per question → <c>GradingStarted</c> → grade via <see cref="IGradingOrchestrator"/> (persists) →
/// <c>GradingCompleted</c> / <c>GradingFailed</c>. One failing question never blocks the others.
/// </summary>
public sealed class GradingJobProcessor(
    IServiceScopeFactory scopes,
    IOptions<GradingWorkerOptions> options,
    TimeProvider clock,
    ILogger<GradingJobProcessor> logger)
{
    public const string FailedMessage = "Chấm điểm tự động thất bại. Giảng viên có thể bấm \"Chấm lại bằng AI\".";
    public const string KeywordFallbackProvider = "KeywordFallback";

    private readonly GradingWorkerOptions _options = options.Value;

    public async Task ProcessAsync(GradingJob job, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        var orchestrator = services.GetService<IGradingOrchestrator>();
        if (orchestrator is null)
        {
            // LEAD-03 not registered yet: leave the session Pending so startup recovery retries it later.
            logger.LogWarning("IGradingOrchestrator is not registered; grading job for session {SessionId} skipped.", job.SessionId);
            return;
        }
        var publisher = services.GetService<IVivaRealtimePublisher>();

        var session = await services.GetRequiredService<IVivaSessionRepository>().GetWithDetailsAsync(job.SessionId, ct);
        if (session is null)
        {
            logger.LogWarning("Grading job skipped: session {SessionId} not found.", job.SessionId);
            return;
        }
        if (session.State != (byte)VivaSessionState.Completed)
        {
            logger.LogWarning("Grading job skipped: session {SessionId} is not completed (state {State}).", job.SessionId, session.State);
            return;
        }

        var examId = string.IsNullOrWhiteSpace(job.ExamId) ? session.ExamId : job.ExamId;
        var questionIds = job.QuestionId is { } single
            ? new List<string> { single }
            : await ResolveQuestionIdsAsync(services, session, ct);

        var existing = await orchestrator.GetSessionGradingAsync(job.SessionId, ct);
        var states = existing?.QuestionResults
            .GroupBy(r => r.QuestionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().State, StringComparer.Ordinal)
            ?? new Dictionary<string, GradingState>(StringComparer.Ordinal);

        foreach (var questionId in questionIds)
        {
            ct.ThrowIfCancellationRequested();
            states.TryGetValue(questionId, out var state);
            var known = states.ContainsKey(questionId);

            // RT-03: never overwrite a lecturer-finalized score.
            if (known && state == GradingState.Finalized)
            {
                logger.LogInformation("Skip {SessionId}/{QuestionId}: score already finalized.", job.SessionId, questionId);
                continue;
            }
            // Initial grading / recovery is idempotent: already graded or failed questions are left for a manual regrade.
            if (job.Kind == GradingJobKind.Initial && known && state is GradingState.AiGraded or GradingState.Failed)
                continue;

            await GradeQuestionAsync(orchestrator, publisher, job, examId, questionId, ct);
        }
    }

    private async Task GradeQuestionAsync(IGradingOrchestrator orchestrator, IVivaRealtimePublisher? publisher,
        GradingJob job, string examId, string questionId, CancellationToken ct)
    {
        await SafePublishAsync(publisher, p => p.PublishGradingStartedAsync(examId,
            new GradingStartedDto(job.SessionId, questionId, clock.GetUtcNow().UtcDateTime), ct));

        QuestionGradingResultDto result;
        try
        {
            result = await ExecuteWithRetryAsync(() => job.Kind == GradingJobKind.Regrade
                ? orchestrator.RegradeQuestionAsync(job.SessionId, questionId, job.RequestedBy ?? "system", ct)
                : orchestrator.GradeQuestionAsync(job.SessionId, questionId, ct), job, questionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsConcurrencyConflict(ex))
        {
            // RT-03: a lecturer saved a score while AI was regrading. Keep the lecturer's score; nothing to broadcast.
            logger.LogInformation("Skip {SessionId}/{QuestionId}: concurrent change detected, AI result discarded.", job.SessionId, questionId);
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grading failed for {SessionId}/{QuestionId}.", job.SessionId, questionId);
            await SafePublishAsync(publisher, p => p.PublishGradingFailedAsync(examId,
                new GradingFailedDto(job.SessionId, questionId, FailedMessage, false), ct));
            return;
        }

        // Persistence-first: GradeQuestionAsync/RegradeQuestionAsync return only after SaveChangesAsync committed.
        switch (result.State)
        {
            case GradingState.AiGraded or GradingState.Finalized:
                await SafePublishAsync(publisher, p => p.PublishGradingCompletedAsync(examId, new GradingCompletedDto(
                    job.SessionId, questionId, (byte)result.State, result.EffectiveScore ?? 0m, result.MaxPoints,
                    result.State == GradingState.AiGraded), ct));
                break;
            case GradingState.Failed:
                await SafePublishAsync(publisher, p => p.PublishGradingFailedAsync(examId, new GradingFailedDto(
                    job.SessionId, questionId, FailedMessage,
                    string.Equals(result.AiProvider, KeywordFallbackProvider, StringComparison.Ordinal)), ct));
                break;
            default:
                logger.LogWarning("Grading of {SessionId}/{QuestionId} returned state {State}; no event published.",
                    job.SessionId, questionId, result.State);
                break;
        }
    }

    private async Task<QuestionGradingResultDto> ExecuteWithRetryAsync(
        Func<Task<QuestionGradingResultDto>> action, GradingJob job, string questionId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < _options.MaxRetries && IsTransient(ex, ct))
            {
                var delay = BackoffDelay(attempt);
                logger.LogWarning(ex, "Transient grading error for {SessionId}/{QuestionId}; retry {Retry}/{Max} in {Delay}.",
                    job.SessionId, questionId, attempt + 1, _options.MaxRetries, delay);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, ct);
            }
        }
    }

    public TimeSpan BackoffDelay(int attempt)
    {
        var baseDelay = _options.RetryBaseDelay;
        if (baseDelay <= TimeSpan.Zero) return TimeSpan.Zero;
        var exponential = baseDelay * Math.Pow(2, attempt); // 2s, 4s, 8s
        var jitter = exponential * (Random.Shared.NextDouble() * 0.2);
        return exponential + jitter;
    }

    public static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case GradingConflictException:
                    return false;
                case TimeoutException:
                case TaskCanceledException: // HttpClient timeout (the caller's token is not cancelled).
                case IOException:
                    return true;
                case HttpRequestException http:
                    return http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                        || (int)http.StatusCode >= 500;
            }
            // Avoid an EF Core / SqlClient reference in BLL: match transient provider exceptions by name.
            if (e.GetType().Name is "SqlException" or "RetryLimitExceededException") return true;
        }
        return false;
    }

    public static bool IsConcurrencyConflict(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is GradingConflictException || e.GetType().Name == "DbUpdateConcurrencyException") return true;
        return false;
    }

    private static async Task<List<string>> ResolveQuestionIdsAsync(IServiceProvider services,
        AIVES.DAL.Entities.VivaSession session, CancellationToken ct)
    {
        // Grade the questions the student actually answered, in exam order.
        var answered = session.Turns
            .Where(t => !string.IsNullOrWhiteSpace(t.QuestionId))
            .OrderBy(t => t.TurnOrdinal)
            .Select(t => t.QuestionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (answered.Count > 0) return answered;

        var questions = await services.GetRequiredService<IVivaQuestionRepository>().GetQuestionsByExamAsync(session.ExamId, ct);
        return (questions ?? new()).Select(q => q.QuestionId).OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private async Task SafePublishAsync(IVivaRealtimePublisher? publisher, Func<IVivaRealtimePublisher, Task> publish)
    {
        if (publisher is null) return;
        try
        {
            await publish(publisher);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Realtime is a projection of persisted state; a broadcast failure must not fail grading.
            logger.LogWarning(ex, "Could not broadcast a grading event.");
        }
    }
}
