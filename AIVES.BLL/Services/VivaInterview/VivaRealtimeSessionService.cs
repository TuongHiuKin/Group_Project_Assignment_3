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
        await sessions.UpdateStateAsync(sessionId, (byte)VivaSessionState.Answering, ct);

        // Đồng hồ chạy từ lúc AI đọc xong câu hỏi: client gọi BeginAnswering sau khi TTS kết thúc,
        // nên thời gian đọc câu hỏi không bị trừ vào giờ thi.
        //
        // Ba trường hợp:
        //  1. Câu mới hoàn toàn  -> Start với TimeLimitSeconds đầy đủ.
        //  2. Đang trả lời, AI sinh + đọc câu xoáy -> đồng hồ đã được Pause, giờ ResumePaused
        //     trả lại đúng số giây còn dở và gắn sang QuestionId của câu xoáy, để lượt nộp
        //     theo followUpQuestionId khớp mốc đang chạy. Quỹ thời gian vẫn là 1p30s liên tục.
        //  3. BeginAnswering lặp lại khi đồng hồ đang chạy -> giữ nguyên, không reset.
        if (timers.GetDeadline(sessionId) is null && timers.ResumePaused(sessionId, question.QuestionId) is null)
        {
            timers.Start(sessionId, question.QuestionId, question.TimeLimitSeconds);
        }
        else
        {
            // Đang có deadline (câu chính hoặc câu xoáy vừa resume) nhưng QuestionId có thể lệch
            // với câu đang hiển thị; rebind để lượt nộp khớp đúng câu, không đổi thời gian.
            timers.Rebind(sessionId, question.QuestionId);
        }
        var remaining = timers.GetRemaining(sessionId)!;
        await publisher.PublishStatusChangedAsync(snapshot.Status with { State = VivaSessionState.Answering, UpdatedAtUtc = clock.GetUtcNow().UtcDateTime }, ct);
        await publisher.PublishTimeRemainingAsync(remaining, ct);
        return remaining;
    }

    /// <summary>
    /// Bảo hiểm cho trường hợp client không bao giờ gọi <c>BeginAnswering</c> — ví dụ trình duyệt
    /// chặn autoplay nên <c>audio.onended</c> không chạy, hoặc sinh viên đóng tab rồi mở lại.
    /// Khi đó không có deadline nào trong bộ nhớ, <see cref="TickAsync"/> trả về ngay và phiên
    /// nằm ở <c>QuestionDelivered</c> vĩnh viễn — đúng lỗi "hết giờ mà không đổi câu hỏi".
    ///
    /// Hàm này khởi động đồng hồ cho câu đang hỏi nếu chưa có, tính từ thời điểm gọi. Gọi từ
    /// worker mỗi giây nên nó cũng là lưới an toàn sau khi app restart (deadline chỉ nằm trong RAM).
    /// Trả về true nếu vừa khởi động đồng hồ mới.
    /// </summary>
    /// <inheritdoc />
    public async Task<bool> EnsureTimerStartedAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Đang chạy hoặc đã pause hợp lệ (đang chờ TTS đọc) thì không can thiệp, trừ khi
        // quá thời gian chờ đọc — lúc đó client coi như chết, phải tự chạy đồng hồ.
        if (timers.GetDeadline(sessionId) is not null) return false;
        var paused = timers.GetPaused(sessionId);
        if (paused is not null && !timers.IsQuestionReadGraceExpired(sessionId)) return false;

        var session = await sessions.GetByIdAsync(sessionId, ct);
        if (session is null) return false;
        if (session.State is not ((byte)VivaSessionState.QuestionDelivered
                or (byte)VivaSessionState.Answering or (byte)VivaSessionState.FollowUpDelivered))
        {
            return false;
        }

        var snapshot = await GetSnapshotAsync(sessionId, ct);
        var question = snapshot.CurrentQuestion;
        if (question is null || question.TimeLimitSeconds <= 0) return false;

        if (paused is not null)
        {
            // Hết thời gian chờ đọc: chạy tiếp phần còn lại, không cấp lại nguyên TimeLimitSeconds.
            timers.ResumePaused(sessionId, question.QuestionId);
        }
        else
        {
            // App restart làm mất deadline trong RAM: phiên đang Answering/FU mà không có mốc nào.
            timers.Start(sessionId, question.QuestionId, question.TimeLimitSeconds);
        }
        return true;
    }

    public async Task<string?> TickAsync(Guid sessionId, IVivaRealtimePublisher publisher, CancellationToken ct = default)
    {
        using var lease = await timers.LockSessionAsync(sessionId, ct);
        var remaining = timers.GetRemaining(sessionId);
        if (remaining is null) return null;
        var session = await sessions.GetByIdAsync(sessionId, ct);
        // Đồng hồ chạy từ lúc GIAO câu hỏi, nên phải tick cả khi sinh viên chưa bấm ghi âm.
        // Chỉ dừng khi phiên đã rời khỏi hai trạng thái "đang chờ trả lời" này.
        if (session is null || session.State is not ((byte)VivaSessionState.Answering
                or (byte)VivaSessionState.QuestionDelivered or (byte)VivaSessionState.FollowUpDelivered))
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
