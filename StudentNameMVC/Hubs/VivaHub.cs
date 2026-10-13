using System.Security.Claims;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace StudentNameMVC.Hubs;

[Authorize]
public sealed class VivaHub(
    IVivaSessionOrchestrator orchestrator,
    IVivaRealtimeSessionService recovery,
    IVivaRealtimePublisher publisher,
    ILogger<VivaHub> logger) : Hub<IVivaClientEvents>
{
    public async Task<VivaSessionSnapshotDto> JoinSession(Guid sessionId)
    {
        var userId = await AuthorizeAsync(sessionId);
        try
        {
            var snapshot = await recovery.PrepareJoinAsync(sessionId, userId, Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, VivaSessionGroup.Name(sessionId), Context.ConnectionAborted);
            try
            {
                await Clients.Caller.SessionStatusChanged(snapshot.Status).WaitAsync(Context.ConnectionAborted);
                if (snapshot.LatestTranscript is not null)
                    await Clients.Caller.TranscriptReady(snapshot.LatestTranscript).WaitAsync(Context.ConnectionAborted);
                if (snapshot.CurrentFollowUp is not null && snapshot.Status.State is VivaSessionState.FollowUpDelivered or VivaSessionState.Answering)
                    await Clients.Caller.FollowUpGenerated(snapshot.CurrentFollowUp).WaitAsync(Context.ConnectionAborted);
                if (snapshot.Timer is not null)
                    await Clients.Caller.TimeRemainingChanged(snapshot.Timer).WaitAsync(Context.ConnectionAborted);
                else if (snapshot.Status.State == VivaSessionState.Answering)
                    await Clients.Caller.VivaError(new(sessionId, "ANSWERING_DEADLINE_UNAVAILABLE",
                        "The answering deadline is unavailable. Please contact the examiner.", false)).WaitAsync(Context.ConnectionAborted);
                if (snapshot.Status.State == VivaSessionState.Completed)
                    await Clients.Caller.SessionCompleted(snapshot.Status).WaitAsync(Context.ConnectionAborted);
                return snapshot;
            }
            catch
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, VivaSessionGroup.Name(sessionId), CancellationToken.None);
                throw;
            }
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not recover viva session {SessionId}.", sessionId);
            throw new HubException("Unable to restore this examination session. Please retry.");
        }
    }

    public Task LeaveSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty) throw new HubException("A valid session ID is required.");
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, VivaSessionGroup.Name(sessionId), Context.ConnectionAborted);
    }

    // Called after question playback finishes. The client cannot supply a duration or deadline.
    public async Task<VivaTimeRemainingDto> BeginAnswering(Guid sessionId)
    {
        var userId = await AuthorizeAsync(sessionId);
        try
        {
            return await recovery.BeginAnsweringAsync(sessionId, userId, publisher, Context.ConnectionAborted);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not begin answering for viva session {SessionId}.", sessionId);
            throw new HubException("Unable to begin answering. Please reload the session or contact the examiner.");
        }
    }

    private async Task<string> AuthorizeAsync(Guid sessionId)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sessionId == Guid.Empty || Context.User?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(userId))
            throw new HubException("Unauthorized to join this examination session.");
        try
        {
            if (!await orchestrator.AuthorizeUserForSessionAsync(sessionId, userId, Context.ConnectionAborted))
                throw new HubException("Unauthorized to join this examination session.");
        }
        catch (HubException) { throw; }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not authorize viva session {SessionId}.", sessionId);
            throw new HubException("Unable to verify examination access. Please retry.");
        }
        return userId;
    }
}
