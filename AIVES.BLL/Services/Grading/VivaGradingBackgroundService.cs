using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.Grading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIVES.BLL.Services.Grading;

/// <summary>
/// Single-reader background worker for the grading queue (G4-RT-01) with startup recovery of
/// unfinished jobs (G4-RT-04). Exceptions are contained per job so the loop never dies.
/// </summary>
public sealed class VivaGradingBackgroundService(
    IGradingQueue queue,
    GradingJobProcessor processor,
    IServiceScopeFactory scopes,
    IOptions<GradingWorkerOptions> options,
    ILogger<VivaGradingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Do not delay host startup.
        var consumer = ConsumeAsync(stoppingToken);
        if (options.Value.RecoverOnStartup) await RecoverAsync(stoppingToken);
        await consumer;
    }

    /// <summary>Re-queues completed sessions whose grading never finished (e.g. app stopped mid-grading).</summary>
    public async Task<int> RecoverAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var scanner = scope.ServiceProvider.GetService<IGradingRecoveryScanner>();
            if (scanner is null) return 0;

            var jobs = await scanner.FindUnfinishedJobsAsync(ct);
            var queued = jobs.Count(queue.TryEnqueue);
            if (jobs.Count > 0)
                logger.LogInformation("Grading recovery: {Queued}/{Found} unfinished session(s) re-queued.", queued, jobs.Count);
            return queued;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Grading recovery scan failed; unfinished sessions will be retried on next start.");
            return 0;
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in queue.ReadAllAsync(ct))
            {
                try
                {
                    await processor.ProcessAsync(job, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Shutdown: the job stays Pending in SQL Server and is recovered on next start.
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Grading job for session {SessionId} crashed.", job.SessionId);
                }
                finally
                {
                    queue.MarkCompleted(job);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
