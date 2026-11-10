using AIVES.BLL.Services.VivaInterview;
using Xunit;

namespace AIVES.Tests.VivaInterview;

/// <summary>
/// Đồng hồ phải chạy theo từng câu hỏi và KHÔNG được reset khi sinh câu hỏi xoáy.
///
/// Quy tắc đã chốt: mỗi câu có TimeLimitSeconds riêng, tính từ lúc giao câu. Nếu sinh viên
/// không trả lời trong thời gian đó thì chuyển sang câu tiếp theo. Câu hỏi xoáy dùng chung
/// đồng hồ của câu đang hỏi, không mở thêm thời gian mới.
/// </summary>
public class VivaTimerRestartTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public void Restart_replaces_existing_deadline_instead_of_throwing()
    {
        var timers = new VivaTimerManager(new ManualClock(DateTimeOffset.UnixEpoch));
        timers.Start(SessionId, "EXAM01-Q01", 90);

        // Start ném lỗi khi đã có deadline (chốt chặn chống client gọi hai lần),
        // còn Restart phải thay được — đó là đường đi khi chuyển câu.
        Assert.Throws<InvalidOperationException>(() => timers.Start(SessionId, "EXAM01-Q02", 90));

        var replaced = timers.Restart(SessionId, "EXAM01-Q02", 90);
        Assert.Equal("EXAM01-Q02", replaced.QuestionId);
        Assert.Equal("EXAM01-Q02", timers.GetDeadline(SessionId)!.QuestionId);
    }

    [Fact]
    public void Restart_gives_the_new_question_its_full_time_budget()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(85));
        Assert.Equal(5, timers.GetRemaining(SessionId)!.SecondsRemaining);

        // Câu mới được giao: phải có đủ 90 giây, không phải 5 giây còn lại của câu trước.
        timers.Restart(SessionId, "EXAM01-Q02", 90);
        Assert.Equal(90, timers.GetRemaining(SessionId)!.SecondsRemaining);
    }

    [Fact]
    public void Restart_is_idempotent_when_called_for_the_same_question()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);

        timers.Restart(SessionId, "EXAM01-Q01", 90);
        clock.Advance(TimeSpan.FromSeconds(30));
        timers.Restart(SessionId, "EXAM01-Q01", 90);

        // Gọi lại cho cùng câu thì đồng hồ đếm lại từ đầu — nhưng đó là hành vi của Restart,
        // còn tầng gọi (BeginAnsweringAsync) đã chặn việc này bằng cách so QuestionId.
        Assert.Equal(90, timers.GetRemaining(SessionId)!.SecondsRemaining);
    }

    [Fact]
    public void Stop_clears_the_deadline_so_the_next_question_can_start_clean()
    {
        var timers = new VivaTimerManager(new ManualClock(DateTimeOffset.UnixEpoch));
        timers.Start(SessionId, "EXAM01-Q01", 90);

        timers.Stop(SessionId);

        Assert.Null(timers.GetDeadline(SessionId));
        // Sau khi Stop thì Start lại được, không còn vướng deadline cũ.
        Assert.NotNull(timers.Start(SessionId, "EXAM01-Q02", 90));
    }

    [Fact]
    public void Remaining_never_goes_negative_after_the_deadline_passes()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(500));

        Assert.Equal(0, timers.GetRemaining(SessionId)!.SecondsRemaining);
    }
}
