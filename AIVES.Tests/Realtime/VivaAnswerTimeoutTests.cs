using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using AIVES.DAL.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIVES.Tests.Realtime;

/// <summary>
/// Hết thời gian trả lời (VIVA-12 / VIVA-13 / RPT-05).
///
/// Trước đây khi hết giờ, <c>TickAsync</c> chỉ đặt state = TimedOut rồi dừng timer. Phiên nằm đó
/// vĩnh viễn: không nộp được, không kết thúc được, không vào hàng đợi chấm điểm, và không bao giờ
/// xuất hiện trong báo cáo. <see cref="IVivaSessionOrchestrator.HandleAnswerTimeoutAsync"/> khắc phục:
/// ghi một lượt rỗng 0 điểm rồi chuyển câu tiếp hoặc kết thúc buổi thi.
/// </summary>
public sealed class VivaAnswerTimeoutTests
{
    private readonly RealtimeData _data = new();
    private readonly ManualClock _clock = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly RealtimeAi _ai = new();
    private readonly VivaTimerManager _timer;
    private readonly VivaRealtimeSessionService _service;

    public VivaAnswerTimeoutTests()
    {
        _timer = new(_clock);
        _service = new(_data, _data, _timer, _clock);
    }

    private VivaSessionOrchestrator Orchestrator() => new(
        _data, _data, _data, _ai, _ai, _ai, _publisher,
        NullLogger<VivaSessionOrchestrator>.Instance, _timer);

    /// <summary>Đưa một phiên vào đúng trạng thái mà timer worker gặp phải khi hết giờ.</summary>
    private async Task<Guid> SessionThatJustTimedOut(params string[] extraQuestionIds)
    {
        foreach (var qid in extraQuestionIds)
        {
            _data.ExtraQuestions.Add(new VivaQuestion
            {
                QuestionId = qid,
                ExamId = _data.Question.ExamId,
                Title = $"Question {qid}",
                Content = $"Nội dung câu hỏi {qid}",
                BloomLevel = "Understand",
                TimeLimitSeconds = 3
            });
        }

        _data.Question.TimeLimitSeconds = 3;
        var id = _data.AddSession("student-a", VivaSessionState.QuestionDelivered);
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);

        // Đồng hồ chạy hết thời gian -> TickAsync đặt TimedOut (đây là hành vi sẵn có, không đổi).
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _service.TickAsync(id, _publisher);
        }

        Assert.Equal(VivaSessionState.TimedOut, (VivaSessionState)_data.Sessions[id].State);
        return id;
    }

    [Fact]
    public async Task Timed_out_question_records_an_empty_zero_score_turn()
    {
        var id = await SessionThatJustTimedOut();
        var timedOutQuestionId = _timer.GetDeadline(id)?.QuestionId ?? _data.Question.QuestionId;

        await Orchestrator().HandleAnswerTimeoutAsync(id, timedOutQuestionId);

        var turn = Assert.Single(_data.Turns.Values, t => t.SessionId == id);
        Assert.Equal(timedOutQuestionId, turn.QuestionId);
        Assert.Equal(string.Empty, turn.TranscriptText);
        Assert.Equal(0m, turn.ConfidenceScore);
        Assert.Equal(1, turn.TurnOrdinal);
    }

    [Fact]
    public async Task Session_is_no_longer_stuck_in_timed_out_after_handling()
    {
        var id = await SessionThatJustTimedOut();
        var timedOutQuestionId = _timer.GetDeadline(id)?.QuestionId ?? _data.Question.QuestionId;

        await Orchestrator().HandleAnswerTimeoutAsync(id, timedOutQuestionId);

        // Điểm mấu chốt: phiên phải rời khỏi TimedOut để có thể nộp/chấm/báo cáo.
        Assert.NotEqual((byte)VivaSessionState.TimedOut, _data.Sessions[id].State);
    }

    [Fact]
    public async Task Timing_out_the_last_question_completes_the_session()
    {
        var id = await SessionThatJustTimedOut();
        var timedOutQuestionId = _timer.GetDeadline(id)?.QuestionId ?? _data.Question.QuestionId;

        await Orchestrator().HandleAnswerTimeoutAsync(id, timedOutQuestionId);

        // Đề thi một câu: hết giờ câu duy nhất = hết buổi thi, phải Completed để chấm điểm.
        Assert.Equal((byte)VivaSessionState.Completed, _data.Sessions[id].State);
        Assert.NotNull(_data.Sessions[id].CompletedAtUtc);
    }

    [Fact]
    public async Task Timing_out_a_middle_question_moves_on_to_the_next_question()
    {
        var id = await SessionThatJustTimedOut("Q2");
        var timedOutQuestionId = _timer.GetDeadline(id)?.QuestionId ?? _data.Question.QuestionId;

        await Orchestrator().HandleAnswerTimeoutAsync(id, timedOutQuestionId);

        // Còn câu sau: không được kết thúc sớm, phải chuyển sang câu kế tiếp.
        Assert.NotEqual((byte)VivaSessionState.Completed, _data.Sessions[id].State);
        Assert.NotEqual((byte)VivaSessionState.TimedOut, _data.Sessions[id].State);
        Assert.Single(_data.Turns.Values.Where(t => t.SessionId == id));
    }

    [Fact]
    public async Task Timeout_handling_is_idempotent_when_called_twice()
    {
        var id = await SessionThatJustTimedOut();
        var timedOutQuestionId = _timer.GetDeadline(id)?.QuestionId ?? _data.Question.QuestionId;
        var orchestrator = Orchestrator();

        await orchestrator.HandleAnswerTimeoutAsync(id, timedOutQuestionId);
        var turnsAfterFirst = _data.Turns.Values.Count(t => t.SessionId == id);

        // Worker có thể gọi lại (retry, hoặc tick lần nữa trước khi timer được dọn).
        await orchestrator.HandleAnswerTimeoutAsync(id, timedOutQuestionId);

        Assert.Equal(turnsAfterFirst, _data.Turns.Values.Count(t => t.SessionId == id));
    }

    [Fact]
    public async Task Unknown_session_is_ignored()
    {
        // Không được ném lỗi: worker chạy nền, một sessionId cũ không còn trong DB là chuyện bình thường.
        await Orchestrator().HandleAnswerTimeoutAsync(Guid.NewGuid(), "Q1");
    }

    [Fact]
    public async Task Session_that_is_not_timed_out_is_left_alone()
    {
        // Chống đua: nếu sinh viên nộp bài đúng lúc timer kêu, phiên đã rời TimedOut -> không ghi đè.
        var id = _data.AddSession("student-a", VivaSessionState.Answering);

        await Orchestrator().HandleAnswerTimeoutAsync(id, _data.Question.QuestionId);

        Assert.Empty(_data.Turns.Values.Where(t => t.SessionId == id));
        Assert.Equal((byte)VivaSessionState.Answering, _data.Sessions[id].State);
    }
}
