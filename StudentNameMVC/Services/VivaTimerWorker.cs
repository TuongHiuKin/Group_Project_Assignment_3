using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Services.VivaInterview;

namespace StudentNameMVC.Services;

public sealed class VivaTimerWorker(
    IServiceScopeFactory scopes,
    VivaTimerManager timers,
    TimeProvider clock,
    ILogger<VivaTimerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var ticks = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
        try
        {
            while (await ticks.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var id in timers.GetActiveSessionIds())
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<IVivaRealtimeSessionService>().TickAsync(id,
                            scope.ServiceProvider.GetRequiredService<IVivaRealtimePublisher>(), stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogError(ex, "Viva timer update failed for {SessionId}.", id); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
