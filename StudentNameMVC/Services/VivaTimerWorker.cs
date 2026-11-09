using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
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
        await RecoverInterruptedTimeoutsAsync(stoppingToken);

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
                        var timedOutQuestionId = await scope.ServiceProvider.GetRequiredService<IVivaRealtimeSessionService>().TickAsync(id,
                            scope.ServiceProvider.GetRequiredService<IVivaRealtimePublisher>(), stoppingToken);
                        if (!string.IsNullOrEmpty(timedOutQuestionId))
                        {
                            var orchestrator = scope.ServiceProvider.GetService<IVivaSessionOrchestrator>();
                            if (orchestrator != null)
                            {
                                await orchestrator.HandleAnswerTimeoutAsync(id, timedOutQuestionId, stoppingToken);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogError(ex, "Viva timer update failed for {SessionId}.", id); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Cứu các phiên đã hết giờ nhưng chưa được xử lý vì app dừng đúng lúc timer kêu.
    ///
    /// <see cref="VivaTimerManager"/> chỉ giữ deadline trong bộ nhớ, nên sau khi restart không còn
    /// ai gọi <see cref="IVivaSessionOrchestrator.HandleAnswerTimeoutAsync"/> cho những phiên đã ở
    /// trạng thái <see cref="VivaSessionState.TimedOut"/>. Chúng sẽ nằm đó vĩnh viễn: không chấm điểm,
    /// không lên báo cáo. Quét một lần lúc khởi động để đưa chúng về Completed.
    ///
    /// Việc đọc dữ liệu nằm trong BLL (<see cref="IVivaSessionOrchestrator.RecoverInterruptedTimeoutsAsync"/>);
    /// worker chỉ gọi một method — Presentation không được inject Repository.
    /// </summary>
    private async Task RecoverInterruptedTimeoutsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetService<IVivaSessionOrchestrator>();
            if (orchestrator is null) return;

            await orchestrator.RecoverInterruptedTimeoutsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Không được làm chết worker: timer realtime vẫn phải chạy dù bước dọn dẹp lỗi.
            logger.LogError(ex, "Timed-out session recovery failed; realtime timer continues.");
        }
    }
}
