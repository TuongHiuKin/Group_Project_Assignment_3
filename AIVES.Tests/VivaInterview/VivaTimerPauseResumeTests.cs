using AIVES.BLL.Services.VivaInterview;
using Xunit;

namespace AIVES.Tests.VivaInterview;

/// <summary>
/// Đồng hồ phải tạm dừng trong lúc server xử lý (STT, sinh câu xoáy, TTS đọc câu hỏi) và
/// chạy tiếp với đúng số giây còn dở — không cấp lại nguyên TimeLimitSeconds, cũng không
/// trừ thời gian xử lý của server vào giờ thi của sinh viên.
///
/// Nghiệp vụ đã chốt: quỹ thời gian trả lời là 1p30s cho mỗi câu, tính từ lúc AI đọc xong
/// câu hỏi. Khi AI đang đọc câu xoáy hoặc đang chấm STT thì đồng hồ đứng yên.
/// </summary>
public class VivaTimerPauseResumeTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public void Pause_freezes_the_clock_and_records_remaining_seconds()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(30));
        var paused = timers.Pause(SessionId);

        Assert.NotNull(paused);
        Assert.Equal(60, paused!.SecondsRemaining);
        // Đang tạm dừng thì không còn deadline hoạt động, nên TickAsync sẽ không bắn timeout.
        Assert.Null(timers.GetDeadline(SessionId));
        Assert.NotNull(timers.GetPaused(SessionId));
    }

    [Fact]
    public void Paused_time_does_not_leak_while_the_clock_keeps_walking()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(20));
        timers.Pause(SessionId);

        // AI đọc câu hỏi xoáy mất 40 giây. Những giây này KHÔNG được trừ vào giờ thi.
        clock.Advance(TimeSpan.FromSeconds(40));

        var resumed = timers.ResumePaused(SessionId);
        Assert.NotNull(resumed);
        Assert.Equal(70, timers.GetRemaining(SessionId)!.SecondsRemaining);
    }

    [Fact]
    public void ResumePaused_returns_null_when_nothing_was_paused()
    {
        var timers = new VivaTimerManager(new ManualClock(DateTimeOffset.UnixEpoch));

        Assert.Null(timers.ResumePaused(SessionId));
    }

    [Fact]
    public void ResumePaused_rebinds_the_deadline_to_the_follow_up_question_id()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(25));
        timers.Pause(SessionId);

        // Câu xoáy có QuestionId riêng nhưng dùng chung quỹ thời gian của câu chính.
        var resumed = timers.ResumePaused(SessionId, "EXAM01-Q01-FU1");

        Assert.Equal("EXAM01-Q01-FU1", resumed!.QuestionId);
        Assert.Equal(65, timers.GetRemaining(SessionId)!.SecondsRemaining);
    }

    [Fact]
    public void Rebind_changes_the_question_id_without_touching_remaining_time()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(18));
        var rebound = timers.Rebind(SessionId, "EXAM01-Q01-FU1");

        Assert.Equal("EXAM01-Q01-FU1", rebound!.QuestionId);
        Assert.Equal(72, timers.GetRemaining(SessionId)!.SecondsRemaining);
        Assert.Equal(90, rebound.TotalSecondsAllowed);
    }

    [Fact]
    public void Rebind_returns_null_when_no_timer_is_running()
    {
        var timers = new VivaTimerManager(new ManualClock(DateTimeOffset.UnixEpoch));

        Assert.Null(timers.Rebind(SessionId, "EXAM01-Q01"));
    }

    [Fact]
    public void Stop_clears_both_the_running_and_the_paused_state()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);
        clock.Advance(TimeSpan.FromSeconds(10));
        timers.Pause(SessionId);

        timers.Stop(SessionId);

        Assert.Null(timers.GetDeadline(SessionId));
        Assert.Null(timers.GetPaused(SessionId));
        Assert.Null(timers.ResumePaused(SessionId));
        // Sau Stop thì Start lại được với thời gian đầy đủ — đây là đường đi khi chuyển câu mới.
        Assert.Equal(90, timers.Start(SessionId, "EXAM01-Q02", 90).TotalSecondsAllowed);
    }

    [Fact]
    public void ResumePaused_returns_null_when_the_paused_budget_was_already_zero()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(120)); // vượt quá 90 giây
        var paused = timers.Pause(SessionId);

        Assert.Equal(0, paused!.SecondsRemaining);
        Assert.Null(timers.ResumePaused(SessionId));
    }
}
