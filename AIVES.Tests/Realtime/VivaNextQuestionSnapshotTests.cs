using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using AIVES.DAL.Entities;

namespace AIVES.Tests.Realtime;

public sealed class VivaNextQuestionSnapshotTests
{
    private readonly RealtimeData _data = new();
    private readonly ManualClock _clock = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly VivaRealtimeSessionService _service;

    public VivaNextQuestionSnapshotTests()
    {
        _data.ExtraQuestions.Add(new VivaQuestion
        {
            QuestionId = "Q2", ExamId = "EXAM01", Title = "Hub", Content = "Explain session groups.", TimeLimitSeconds = 45
        });
        _service = new VivaRealtimeSessionService(_data, _data, new VivaTimerManager(_clock), _clock);
    }

    private Guid ArrangeFinishedFirstQuestion(VivaSessionState state)
    {
        var id = _data.AddSession("student-a", state);
        _data.Turns[Guid.NewGuid()] = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(), SessionId = id, QuestionId = "Q1", TurnOrdinal = 1,
            TranscriptText = "Scoped per request.", FollowUpQuestionText = "Why not Singleton?", CreatedAtUtc = DateTime.UtcNow
        };
        _data.Turns[Guid.NewGuid()] = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(), SessionId = id, QuestionId = "Q1", TurnOrdinal = 2,
            TranscriptText = "Captive dependency.", CreatedAtUtc = DateTime.UtcNow
        };
        return id;
    }

    [Fact]
    public async Task Snapshot_after_the_follow_up_answer_shows_the_next_question_without_old_transcript()
    {
        var id = ArrangeFinishedFirstQuestion(VivaSessionState.QuestionDelivered);

        var snapshot = await _service.GetSnapshotAsync(id);

        Assert.Equal("Q2", snapshot.CurrentQuestion?.QuestionId);
        Assert.Null(snapshot.LatestTranscript);
        Assert.Null(snapshot.CurrentFollowUp);
        Assert.Equal(4, snapshot.Status.MaxTurnsAllowed);
    }

    [Fact]
    public async Task BeginAnswering_on_the_next_question_starts_its_own_deadline()
    {
        var id = ArrangeFinishedFirstQuestion(VivaSessionState.QuestionDelivered);

        var timer = await _service.BeginAnsweringAsync(id, "student-a", _publisher);

        Assert.Equal(45, timer.TotalSecondsAllowed);
        Assert.Equal((byte)VivaSessionState.Answering, _data.Sessions[id].State);
    }

    [Fact]
    public async Task Snapshot_while_a_follow_up_is_pending_keeps_the_current_question()
    {
        var id = _data.AddSession("student-a", VivaSessionState.FollowUpDelivered);
        _data.Turns[Guid.NewGuid()] = new VivaSessionTurn
        {
            TurnId = Guid.NewGuid(), SessionId = id, QuestionId = "Q1", TurnOrdinal = 1,
            TranscriptText = "Scoped per request.", FollowUpQuestionText = "Why not Singleton?", CreatedAtUtc = DateTime.UtcNow
        };

        var snapshot = await _service.GetSnapshotAsync(id);

        Assert.Equal("Q1-FU1", snapshot.CurrentQuestion?.QuestionId);
        Assert.NotNull(snapshot.CurrentFollowUp);
    }
}
