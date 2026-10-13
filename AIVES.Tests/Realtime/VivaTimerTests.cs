using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIVES.Tests.Realtime;

public sealed class VivaTimerTests
{
    private readonly RealtimeData _data = new();
    private readonly ManualClock _clock = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly RealtimeAi _ai = new();
    private readonly VivaTimerManager _timer;
    private readonly VivaRealtimeSessionService _service;

    public VivaTimerTests()
    {
        _timer = new(_clock);
        _service = new(_data, _data, _timer, _clock);
    }
    private VivaSessionOrchestrator Orchestrator() => new(_data, _data, _data, _ai, _ai, _ai, _publisher,
        NullLogger<VivaSessionOrchestrator>.Instance, _timer);
    private static VivaAnswerSubmissionDto Answer(Guid id, string question = "Q1") => new(id, question, new byte[] { 1, 2 }, "audio/webm", 1);

    [Fact]
    public async Task Countdown_uses_server_deadline_and_persists_timeout_once()
    {
        _data.Question.TimeLimitSeconds = 3;
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        _publisher.Events.Clear();
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _service.TickAsync(id, _publisher);
        }
        Assert.Equal(new[] { 2, 1, 0 }, _publisher.Events.OfType<VivaTimeRemainingDto>().Select(t => t.SecondsRemaining));
        Assert.Equal(VivaSessionState.TimedOut, (VivaSessionState)_data.Sessions[id].State);
        Assert.Equal(1, _data.TimeoutSaves);
        Assert.Null(_timer.GetRemaining(id));
        await _service.TickAsync(id, _publisher);
        Assert.Equal(1, _data.TimeoutSaves);
    }

    [Fact]
    public async Task Duplicate_begin_and_rejoin_do_not_extend_deadline()
    {
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        var deadline = _timer.GetDeadline(id);
        _clock.Advance(TimeSpan.FromSeconds(10));
        var repeat = await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        var snapshot = await _service.PrepareJoinAsync(id, "student-a");
        Assert.Equal(50, repeat.SecondsRemaining);
        Assert.Equal(50, snapshot.Timer!.SecondsRemaining);
        Assert.Equal(deadline, _timer.GetDeadline(id));
        Assert.Equal(1, _data.StateSaves);
    }

    [Fact]
    public async Task Early_submission_stops_timer_after_persistence_and_keeps_turn_transcript()
    {
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        var transcript = await Orchestrator().SubmitAnswerAudioAsync(Answer(id));
        Assert.Null(_timer.GetDeadline(id));
        Assert.Equal("Scoped per request.", transcript.TranscriptText);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await _service.TickAsync(id, _publisher);
        Assert.Equal(0, _data.TimeoutSaves);
        Assert.Equal((byte)VivaSessionState.TranscriptReady, _data.Sessions[id].State);
    }

    [Fact]
    public async Task Late_submission_and_wrong_question_never_call_STT()
    {
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        await Assert.ThrowsAsync<ArgumentException>(() => Orchestrator().SubmitAnswerAudioAsync(Answer(id, "Q-OTHER")));
        _clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator().SubmitAnswerAudioAsync(Answer(id)));
        Assert.Equal(0, _ai.Transcriptions);
        Assert.Empty(_data.Turns);
    }

    [Fact]
    public async Task Simultaneous_expiry_and_submission_cannot_accept_late_answer_or_timeout_twice()
    {
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        _clock.Advance(TimeSpan.FromSeconds(60));
        await Task.WhenAll(_service.TickAsync(id, _publisher), _service.TickAsync(id, _publisher),
            Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator().SubmitAnswerAudioAsync(Answer(id))));
        Assert.Equal(1, _data.TimeoutSaves);
        Assert.Equal(0, _ai.Transcriptions);
    }

    [Fact]
    public async Task Failed_answer_persistence_keeps_timer_and_does_not_publish_or_transcribe()
    {
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        _publisher.Events.Clear();
        _data.FailSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator().SubmitAnswerAudioAsync(Answer(id)));
        Assert.NotNull(_timer.GetDeadline(id));
        Assert.Empty(_publisher.Events);
        Assert.Equal(0, _ai.Transcriptions);
    }

    [Fact]
    public async Task Failed_timeout_persistence_emits_nothing_and_is_retried()
    {
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        _clock.Advance(TimeSpan.FromSeconds(60));
        _publisher.Events.Clear();
        _data.FailSave = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.TickAsync(id, _publisher));
        Assert.Empty(_publisher.Events);
        Assert.NotNull(_timer.GetDeadline(id));
        _data.FailSave = false;
        await _service.TickAsync(id, _publisher);
        Assert.Equal(1, _data.TimeoutSaves);
    }

    [Fact]
    public async Task Answering_after_process_restart_fails_closed_instead_of_giving_more_time()
    {
        var id = _data.AddSession("student-a", VivaSessionState.Answering);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BeginAnsweringAsync(id, "student-a", _publisher));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator().SubmitAnswerAudioAsync(Answer(id)));
        Assert.Null(_timer.GetDeadline(id));
    }

    [Fact]
    public async Task Followup_submission_preserves_primary_question_FK()
    {
        var id = _data.AddSession("student-a", VivaSessionState.FollowUpDelivered);
        var turnId = Guid.NewGuid();
        _data.Turns[turnId] = new() { SessionId = id, TurnId = turnId, QuestionId = "Q1", TurnOrdinal = 1,
            TranscriptText = "First answer", FollowUpQuestionText = "Why not singleton?", FollowUpAudioUrl = "/audio/followup.mp3" };
        var snapshot = await _service.GetSnapshotAsync(id);
        Assert.Equal("Q1-FU1", snapshot.CurrentQuestion!.QuestionId);
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        await Orchestrator().SubmitAnswerAudioAsync(Answer(id, "Q1-FU1"));
        Assert.Equal(2, _data.Turns.Count);
        Assert.All(_data.Turns.Values, t => Assert.Equal("Q1", t.QuestionId));
    }

    [Fact]
    public async Task Other_student_and_invalid_state_cannot_start_timer()
    {
        var id = _data.AddSession("student-a", VivaSessionState.Ready);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.BeginAnsweringAsync(id, "student-b", _publisher));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.BeginAnsweringAsync(id, "student-a", _publisher));
        Assert.Empty(_timer.GetActiveSessionIds());
        Assert.Empty(_publisher.Events);
    }

    [Fact]
    public async Task STT_failure_restores_original_deadline_for_retry_without_extending_time()
    {
        var id = _data.AddSession("student-a");
        await _service.BeginAnsweringAsync(id, "student-a", _publisher);
        var original = _timer.GetDeadline(id);
        _clock.Advance(TimeSpan.FromSeconds(10));
        _ai.TranscriptionSucceeds = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator().SubmitAnswerAudioAsync(Answer(id)));
        Assert.Equal(original, _timer.GetDeadline(id));
        Assert.Equal(50, _timer.GetRemaining(id)!.SecondsRemaining);
        Assert.True(_publisher.Events.OfType<VivaErrorDto>().Last().CanRetry);
        _ai.TranscriptionSucceeds = true;
        await Orchestrator().SubmitAnswerAudioAsync(Answer(id));
        Assert.Null(_timer.GetDeadline(id));
    }

    [Fact]
    public async Task Cancellation_before_begin_never_changes_persisted_state()
    {
        var id = _data.AddSession("student-a");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.BeginAnsweringAsync(id, "student-a", _publisher, cancellation.Token));
        Assert.Equal((byte)VivaSessionState.QuestionDelivered, _data.Sessions[id].State);
        Assert.Empty(_timer.GetActiveSessionIds());
    }
}
