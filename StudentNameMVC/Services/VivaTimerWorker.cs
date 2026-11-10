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
                // Chỉ phiên có deadline trong RAM mới được liệt kê ở đây. Phiên đang chờ TTS đọc
                // (pause) hoặc phiên mất deadline sau restart không có mặt → phải quét thêm để
                // không kẹt vĩnh viễn. Xem EnsureTimerStartedAsync.
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

                await SweepWaitingSessionsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Quét các phiên đang chờ trả lời nhưng KHÔNG có deadline trong RAM — chúng vô hình với vòng
    /// lặp trên. Hai nguồn: (a) câu đang được TTS đọc, client chưa gọi BeginAnswering;
    /// (b) app vừa restart nên mất toàn bộ deadline. Không quét thì phiên nằm im mãi ở
    /// QuestionDelivered/Answering và sinh viên thấy "hết giờ mà không đổi câu hỏi".
    /// </summary>
    private async Task SweepWaitingSessionsAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var orchestrator = scope.ServiceProvider.GetService<IVivaSessionOrchestrator>();
            var realtime = scope.ServiceProvider.GetRequiredService<IVivaRealtimeSessionService>();
            if (orchestrator is null) return;

            var waiting = await orchestrator.GetWaitingSessionIdsAsync(ct);
            foreach (var id in waiting)
            {
                try
                {
                    await realtime.EnsureTimerStartedAsync(id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "Timer safety net failed for {SessionId}.", id); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Không được làm chết worker: timer realtime vẫn phải chạy dù bước quét lỗi.
            logger.LogError(ex, "Waiting-session sweep failed; realtime timer continues.");
        }
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
