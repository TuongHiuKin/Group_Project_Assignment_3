using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;
using AIVES.BLL.Services.VivaInterview;
using Microsoft.AspNetCore.SignalR;
using StudentNameMVC.Hubs;
using StudentNameMVC.Services;

namespace AIVES.Tests.Realtime;

public sealed class VivaRealtimePublisherTests
{
    private sealed class Client : IVivaClientEvents
    {
        public List<object> Events { get; } = new();
        public Task Pending { get; set; } = Task.CompletedTask;
        private Task Record(object dto) { Events.Add(dto); return Pending; }
        public Task SessionStatusChanged(VivaSessionStatusDto status) => Record(status);
        public Task TranscriptReady(VivaTranscriptDto transcript) => Record(transcript);
        public Task FollowUpGenerated(VivaFollowUpDto followUp) => Record(followUp);
        public Task TimeRemainingChanged(VivaTimeRemainingDto timer) => Record(timer);
        public Task SessionCompleted(VivaSessionStatusDto status) => Record(status);
        public Task VivaError(VivaErrorDto error) => Record(error);
        public Task GradingStarted(GradingStartedDto started) => Record(started);
        public Task GradingCompleted(GradingCompletedDto completed) => Record(completed);
        public Task GradingFailed(GradingFailedDto failed) => Record(failed);
    }
    // Every non-group send fails, so the test also detects accidental broadcasting.
    private sealed class GroupClients(Client client) : IHubClients<IVivaClientEvents>
    {
        public List<string> GroupsSent { get; } = new();
        public IVivaClientEvents Group(string groupName) { GroupsSent.Add(groupName); return client; }
        public IVivaClientEvents All => throw new InvalidOperationException("Global broadcast forbidden.");
        public IVivaClientEvents AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IVivaClientEvents Client(string connectionId) => throw new NotSupportedException();
        public IVivaClientEvents Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IVivaClientEvents GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IVivaClientEvents Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IVivaClientEvents User(string userId) => throw new NotSupportedException();
        public IVivaClientEvents Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }
    private sealed class Context(GroupClients clients) : IHubContext<VivaHub, IVivaClientEvents>
    {
        public IHubClients<IVivaClientEvents> Clients => clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }
    private readonly RealtimeData _data = new();
    private readonly Client _client = new();
    private readonly GroupClients _clients;
    private readonly VivaRealtimePublisher _publisher;
    public VivaRealtimePublisherTests()
    {
        _clients = new(_client);
        var clock = new ManualClock();
        _publisher = new(new Context(_clients), new VivaRealtimeSessionService(_data, _data, new(clock), clock));
    }

    [Fact]
    public async Task Every_event_targets_exactly_the_session_group_and_completion_uses_saved_status()
    {
        var id = _data.AddSession("student-a");
        var completed = DateTime.UtcNow.AddMinutes(-1);
        await _data.CompleteSessionAsync(id, completed);
        await _publisher.PublishStatusChangedAsync(new(id, "EXAM01", "student-a", VivaSessionState.Completed, 0, 2, completed));
        await _publisher.PublishTranscriptReadyAsync(new(id, Guid.NewGuid(), "Q1", "Answer", .9m, completed));
        await _publisher.PublishFollowUpGeneratedAsync(new(id, Guid.NewGuid(), "Q1-FU1", "Question", 1));
        await _publisher.PublishTimeRemainingAsync(new(id, 1, 60));
        await _publisher.PublishErrorAsync(new(id, "ERROR", "Safe message", true));
        await _publisher.PublishSessionCompletedAsync(id, "EXAM01", "student-a");
        Assert.Equal(6, _clients.GroupsSent.Count);
        Assert.All(_clients.GroupsSent, group => Assert.Equal($"viva-session-{id:D}", group));
        var status = Assert.IsType<VivaSessionStatusDto>(_client.Events.Last());
        Assert.Equal(completed, status.UpdatedAtUtc);
        Assert.Equal(VivaSessionState.Completed, status.State);
    }

    [Fact]
    public async Task Completion_before_persistence_or_with_wrong_identity_is_never_sent()
    {
        var id = _data.AddSession("student-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _publisher.PublishSessionCompletedAsync(id, "EXAM01", "student-a"));
        await _data.CompleteSessionAsync(id, DateTime.UtcNow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _publisher.PublishSessionCompletedAsync(id, "OTHER", "student-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _publisher.PublishSessionCompletedAsync(id, "EXAM01", "student-b"));
        Assert.Empty(_clients.GroupsSent);
    }

    [Fact]
    public async Task Already_cancelled_publish_does_not_start_send()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _publisher.PublishTimeRemainingAsync(new(Guid.NewGuid(), 1, 60), source.Token));
        Assert.Empty(_clients.GroupsSent);
    }

    [Fact]
    public async Task Cancellation_during_a_pending_typed_send_releases_the_callers_wait()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Pending = pending.Task;
        using var source = new CancellationTokenSource();
        var send = _publisher.PublishTimeRemainingAsync(new(Guid.NewGuid(), 1, 60), source.Token);
        Assert.False(send.IsCompleted);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        // WaitAsync cannot retract a send already initiated by SignalR.
        pending.SetResult();
        Assert.Single(_clients.GroupsSent);
    }
}
