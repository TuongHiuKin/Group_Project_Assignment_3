using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Models.VivaInterview;
using AIVES.DAL.Entities;
using AIVES.DAL.Repositories.Interfaces;

namespace AIVES.BLL.Services.VivaInterview;

/// <summary>BLL recovery and timer operations; presentation never accesses repositories.</summary>
public sealed class VivaRealtimeSessionService(
    IVivaSessionRepository sessions,
    IVivaQuestionRepository questions,
    VivaTimerManager timers,
    TimeProvider clock) : IVivaRealtimeSessionService
{
    public async Task<VivaSessionSnapshotDto> GetSnapshotAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await sessions.GetWithDetailsAsync(sessionId, ct)
            ?? throw new KeyNotFoundException("Examination session was not found.");
        var state = (VivaSessionState)session.State;
        var latest = session.Turns.OrderByDescending(t => t.TurnOrdinal).ThenByDescending(t => t.CreatedAtUtc).FirstOrDefault();
        var count = session.Turns.Count;
        // Lịch sử hội thoại đầy đủ: mỗi lượt kèm câu hỏi xoáy đã sinh (nếu có). UI dùng để dựng lại
        // "câu hỏi chính → câu trả lời → câu hỏi xoáy → câu trả lời" thay vì chỉ hiện lượt cuối.
        var turnHistory = session.Turns
            .OrderBy(t => t.TurnOrdinal)
            .Select(t => new VivaTurnDto(
                t.TurnId,
                t.QuestionId,
                t.TurnOrdinal,
                t.TranscriptText,
                t.ConfidenceScore,
                t.FollowUpQuestionText,
                t.FollowUpAudioUrl))
            .ToList();
        var ordered = (await questions.GetQuestionsByExamAsync(session.ExamId, ct) ?? new())
            .OrderBy(q => q.QuestionId, StringComparer.Ordinal).ToList();
        var question = latest is null ? ordered.FirstOrDefault() : await questions.GetByIdAsync(latest.QuestionId, ct);
        // A new question is open when the latest turn has no pending follow-up (its question is finished).
        var nextQuestion = latest is not null && string.IsNullOrEmpty(latest.FollowUpQuestionText) &&
            state is VivaSessionState.QuestionDelivered or VivaSessionState.Answering
            ? ordered.FirstOrDefault(q => string.CompareOrdinal(q.QuestionId, latest.QuestionId) > 0)
            : null;
        if (nextQuestion is not null)
        {
            var nextCurrent = new VivaQuestionDto(nextQuestion.QuestionId, nextQuestion.ExamId, nextQuestion.Title,
                nextQuestion.Content, nextQuestion.TimeLimitSeconds, nextQuestion.AudioUrl);
            var nextStatus = new VivaSessionStatusDto(sessionId, session.ExamId, session.StudentId, state, count,
                VivaSessionOrchestrator.SessionMaxTurns(ordered.Count), latest!.CompletedAtUtc ?? latest.CreatedAtUtc);
            return new(nextStatus, nextCurrent, null, null,
                state == VivaSessionState.Answering ? timers.GetRemaining(sessionId) : null)
            {
                Turns = turnHistory
            };
        }
        VivaFollowUpDto? followUp = null;
        if (latest?.FollowUpQuestionText is { Length: > 0 } text)
            followUp = new(sessionId, latest.TurnId, $"{latest.QuestionId}-FU{latest.TurnOrdinal}", text,
                latest.TurnOrdinal, latest.FollowUpAudioUrl);
        var current = question is null ? null : new VivaQuestionDto(question.QuestionId, question.ExamId,
            question.Title, question.Content, question.TimeLimitSeconds, question.AudioUrl);
        if (followUp is not null && state is VivaSessionState.FollowUpDelivered or VivaSessionState.Answering)
            current = new(followUp.FollowUpQuestionId, session.ExamId, "Follow-up question", followUp.FollowUpContent,
                question?.TimeLimitSeconds ?? throw new InvalidOperationException("Question time limit is unavailable."), followUp.AudioUrl);
        VivaTranscriptDto? transcript = latest?.TranscriptText is { } answer
            ? new(sessionId, latest.TurnId, latest.QuestionId, answer, latest.ConfidenceScore ?? 0,
                latest.CompletedAtUtc ?? latest.CreatedAtUtc) : null;
        var updated = session.CompletedAtUtc ?? latest?.CompletedAtUtc ?? latest?.CreatedAtUtc ?? session.StartedAtUtc;
        var status = new VivaSessionStatusDto(sessionId, session.ExamId, session.StudentId, state,
            Math.Max(count, state is VivaSessionState.QuestionDelivered or VivaSessionState.Answering ? 1 : 0),
            VivaSessionOrchestrator.SessionMaxTurns(ordered.Count), updated);
        return new(status, current, transcript, followUp,
            state == VivaSessionState.Answering ? timers.GetRemaining(sessionId) : null)
        {
            Turns = turnHistory
        };
    }

    public async Task<VivaSessionSnapshotDto> PrepareJoinAsync(Guid sessionId, string userId, CancellationToken ct = default)
    {
        using var lease = await timers.LockSessionAsync(sessionId, ct);
        var snapshot = await GetOwnedSnapshotAsync(sessionId, userId, ct);
        if (snapshot.Status.State == VivaSessionState.Created)
        {
            await sessions.UpdateStateAsync(sessionId, (byte)VivaSessionState.Ready, ct);
            snapshot = snapshot with { Status = snapshot.Status with { State = VivaSessionState.Ready, UpdatedAtUtc = clock.GetUtcNow().UtcDateTime } };
        }
        return snapshot;
    }

    public async Task<VivaTimeRemainingDto> BeginAnsweringAsync(Guid sessionId, string userId,
        IVivaRealtimePublisher publisher, CancellationToken ct = default)
    {
        using var lease = await timers.LockSessionAsync(sessionId, ct);
        var snapshot = await GetOwnedSnapshotAsync(sessionId, userId, ct);
        if (snapshot.Status.State == VivaSessionState.Answering)
            return snapshot.Timer ?? throw new InvalidOperationException("Answering deadline is unavailable. Contact the examiner.");
        if (snapshot.Status.State is not (VivaSessionState.QuestionDelivered or VivaSessionState.FollowUpDelivered))
            throw new InvalidOperationException("This session cannot begin answering in its current state.");
        var question = snapshot.CurrentQuestion ?? throw new InvalidOperationException("Current question is unavailable.");
        if (question.TimeLimitSeconds <= 0) throw new InvalidOperationException("Question time limit is invalid.");
        // Never replace an existing deadline or extend it on a repeated request.
        if (timers.GetDeadline(sessionId) is not null) throw new InvalidOperationException("An answering timer already exists.");
        await sessions.UpdateStateAsync(sessionId, (byte)VivaSessionState.Answering, ct);
        timers.Start(sessionId, question.QuestionId, question.TimeLimitSeconds);
        var remaining = timers.GetRemaining(sessionId)!;
        await publisher.PublishStatusChangedAsync(snapshot.Status with { State = VivaSessionState.Answering, UpdatedAtUtc = clock.GetUtcNow().UtcDateTime }, ct);
        await publisher.PublishTimeRemainingAsync(remaining, ct);
        return remaining;
    }

    public async Task<string?> TickAsync(Guid sessionId, IVivaRealtimePublisher publisher, CancellationToken ct = default)
    {
        using var lease = await timers.LockSessionAsync(sessionId, ct);
        var remaining = timers.GetRemaining(sessionId);
        if (remaining is null) return null;
        var session = await sessions.GetByIdAsync(sessionId, ct);
        if (session is null || session.State != (byte)VivaSessionState.Answering)
        {
            timers.Stop(sessionId);
            return null;
        }
        if (remaining.SecondsRemaining > 0)
        {
            await publisher.PublishTimeRemainingAsync(remaining, ct);
            return null;
        }
        var deadline = timers.GetDeadline(sessionId);
        var timedOutQuestionId = deadline?.QuestionId ?? string.Empty;
        // A failed save leaves the deadline registered for a later retry; no false timeout event.
        await sessions.UpdateStateAsync(sessionId, (byte)VivaSessionState.TimedOut, ct);
        timers.Stop(sessionId);
        await publisher.PublishTimeRemainingAsync(remaining, ct);
        var snapshot = await GetSnapshotAsync(sessionId, ct);
        await publisher.PublishStatusChangedAsync(snapshot.Status with { UpdatedAtUtc = clock.GetUtcNow().UtcDateTime }, ct);
        return timedOutQuestionId;
    }

    private async Task<VivaSessionSnapshotDto> GetOwnedSnapshotAsync(Guid id, string userId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var snapshot = await GetSnapshotAsync(id, ct);
        if (!string.Equals(snapshot.Status.StudentId, userId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Unauthorized to join this examination session.");
        return snapshot;
    }
}
