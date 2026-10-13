using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using Microsoft.AspNetCore.SignalR;
using StudentNameMVC.Hubs;

namespace StudentNameMVC.Services;

public sealed class VivaRealtimePublisher(
    IHubContext<VivaHub, IVivaClientEvents> hub,
    IVivaRealtimeSessionService recovery) : IVivaRealtimePublisher
{
    private Task SendAsync(Guid id, Func<IVivaClientEvents, Task> send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Typed SignalR methods do not accept transport cancellation tokens; cancel the wait.
        return send(hub.Clients.Group(VivaSessionGroup.Name(id))).WaitAsync(ct);
    }

    public Task PublishStatusChangedAsync(VivaSessionStatusDto status, CancellationToken ct = default) =>
        SendAsync(status.SessionId, client => client.SessionStatusChanged(status), ct);
    public Task PublishTranscriptReadyAsync(VivaTranscriptDto transcript, CancellationToken ct = default) =>
        SendAsync(transcript.SessionId, client => client.TranscriptReady(transcript), ct);
    public Task PublishFollowUpGeneratedAsync(VivaFollowUpDto followUp, CancellationToken ct = default) =>
        SendAsync(followUp.SessionId, client => client.FollowUpGenerated(followUp), ct);
    public Task PublishTimeRemainingAsync(VivaTimeRemainingDto timer, CancellationToken ct = default) =>
        SendAsync(timer.SessionId, client => client.TimeRemainingChanged(timer), ct);
    public Task PublishErrorAsync(VivaErrorDto error, CancellationToken ct = default) =>
        SendAsync(error.SessionId, client => client.VivaError(error), ct);

    public async Task PublishSessionCompletedAsync(Guid sessionId, string examId, string studentId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var status = (await recovery.GetSnapshotAsync(sessionId, ct)).Status;
        if (status.State != VivaSessionState.Completed || status.ExamId != examId || status.StudentId != studentId)
            throw new InvalidOperationException("Completed session must be persisted before publishing.");
        await SendAsync(sessionId, client => client.SessionCompleted(status), ct);
    }
}
