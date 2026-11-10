using AIVES.BLL.Services.VivaInterview;
using Xunit;

namespace AIVES.Tests.VivaInterview;

/// <summary>
/// Lưới an toàn cho ca "hết giờ mà không đổi câu hỏi".
///
/// Bối cảnh thật: phiên nằm ở QuestionDelivered, đồng hồ 00:00, im lặng mãi không chuyển câu.
/// Nguyên nhân là đồng hồ chỉ được client khởi động qua BeginAnswering, mà lệnh đó phụ thuộc
/// audio.onended — autoplay bị chặn hoặc tab bị đóng là mất luôn tín hiệu, không còn deadline
/// nào trong RAM nên worker không biết gì để mà hết giờ.
///
/// Cách sửa: khi giao câu, đánh dấu "câu đang được đọc" bằng một mốc tạm dừng có dấu thời gian.
/// Quá QuestionReadGraceSeconds mà chưa ai gọi BeginAnswering thì coi client đã chết và tự chạy.
/// </summary>
public class VivaTimerGraceTests
{
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static readonly Guid SessionId = Guid.NewGuid();

    [Fact]
    public void MarkQuestionBeingRead_creates_a_paused_marker_with_full_budget()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);

        var paused = timers.MarkQuestionBeingRead(SessionId, "EXAM01-Q01", 90);

        Assert.Equal(90, paused.SecondsRemaining);
        Assert.Equal("EXAM01-Q01", paused.QuestionId);
        Assert.Equal(ClockStart, paused.PausedAtUtc);
        // Chưa có deadline chạy: học viên còn đang nghe câu hỏi, không bị trừ giờ.
        Assert.Null(timers.GetDeadline(SessionId));
        Assert.NotNull(timers.GetPaused(SessionId));
    }

    [Fact]
    public void Grace_is_not_expired_immediately_after_delivery()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.MarkQuestionBeingRead(SessionId, "EXAM01-Q01", 90);

        Assert.False(timers.IsQuestionReadGraceExpired(SessionId));
    }

    [Fact]
    public void Grace_is_not_expired_while_the_question_is_still_being_read()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.MarkQuestionBeingRead(SessionId, "EXAM01-Q01", 90);

        // Câu hỏi dài đọc mất 45 giây — vẫn trong ngưỡng chờ, không được tự chạy đồng hồ.
        clock.Advance(TimeSpan.FromSeconds(45));

        Assert.False(timers.IsQuestionReadGraceExpired(SessionId));
    }

    [Fact]
    public void Grace_expires_when_the_client_never_called_BeginAnswering()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.MarkQuestionBeingRead(SessionId, "EXAM01-Q01", 90);

        // Client chết hẳn: quá 60 giây không ai gọi BeginAnswering.
        clock.Advance(TimeSpan.FromSeconds(VivaTimerManager.QuestionReadGraceSeconds + 1));

        Assert.True(timers.IsQuestionReadGraceExpired(SessionId));
    }

    [Fact]
    public void Never_started_session_reports_grace_expired_so_worker_can_rescue_it()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);

        // Không có mốc nào (app restart, hoặc phiên cũ trước khi bật cơ chế này): phải trả false
        // để nhánh khác trong EnsureTimerStartedAsync khởi động đồng hồ từ đầu.
        Assert.False(timers.IsQuestionReadGraceExpired(SessionId));
        Assert.Null(timers.GetPaused(SessionId));
    }

    [Fact]
    public void Resuming_after_grace_expiry_keeps_the_full_budget()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.MarkQuestionBeingRead(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(VivaTimerManager.QuestionReadGraceSeconds + 5));
        Assert.True(timers.IsQuestionReadGraceExpired(SessionId));

        var resumed = timers.ResumePaused(SessionId, "EXAM01-Q01");

        // Thời gian chờ đọc KHÔNG bị trừ vào giờ thi: vẫn đủ 90 giây để trả lời.
        Assert.NotNull(resumed);
        Assert.Equal(90, timers.GetRemaining(SessionId)!.SecondsRemaining);
    }

    [Fact]
    public void Pausing_mid_answer_records_when_it_happened()
    {
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var timers = new VivaTimerManager(clock);
        timers.Start(SessionId, "EXAM01-Q01", 90);

        clock.Advance(TimeSpan.FromSeconds(30));
        var paused = timers.Pause(SessionId);

        Assert.NotNull(paused);
        Assert.Equal(60, paused!.SecondsRemaining);
        // PausedAtUtc phải là lúc tạm dừng, không phải lúc bắt đầu — nếu sai thì cửa sổ chờ đọc
        // tính từ mốc sai và phiên bị chuyển câu oan.
        Assert.Equal(ClockStart.AddSeconds(30), paused.PausedAtUtc);
    }

    private static DateTimeOffset ClockStart => DateTimeOffset.UnixEpoch;
}
