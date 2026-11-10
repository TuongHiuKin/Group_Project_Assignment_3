using System.Collections.Concurrent;
using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Services.VivaInterview;

/// <summary>
/// Single-process countdown registry. No scoped services or connection cancellation tokens
/// are retained here. Durable deadlines must be supplied by the DAL before multi-node hosting.
/// </summary>
public sealed class VivaTimerManager(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, Deadline> _deadlines = new();
    private readonly ConcurrentDictionary<Guid, PausedDeadline> _paused = new();
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public sealed record Deadline(Guid SessionId, string QuestionId, DateTimeOffset EndsAtUtc, int TotalSecondsAllowed);

    /// <summary>Mốc tạm dừng: chỉ giữ số giây còn lại, không có EndsAtUtc vì đồng hồ đang đứng yên.</summary>
    public sealed record PausedDeadline(Guid SessionId, string QuestionId, int SecondsRemaining, int TotalSecondsAllowed);

    public async Task<IDisposable> LockSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var gate = _gates[(uint)sessionId.GetHashCode() % (uint)_gates.Length];
        await gate.WaitAsync(ct);
        return new Lease(gate);
    }

    // Caller holds the session lock across persistence and timer changes.
    public Deadline Start(Guid sessionId, string questionId, int seconds)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var deadline = new Deadline(sessionId, questionId, clock.GetUtcNow().AddSeconds(seconds), seconds);
        if (!_deadlines.TryAdd(sessionId, deadline))
            throw new InvalidOperationException("An answering timer already exists for this session.");
        return deadline;
    }

    /// <summary>
    /// Bắt đầu lại đồng hồ cho câu hỏi mới, ghi đè deadline cũ nếu có.
    /// Dùng khi chuyển câu hoặc khi câu hỏi xoáy được giao: mỗi câu hỏi có thời gian riêng của nó,
    /// không cộng dồn thời gian còn lại của câu trước. <see cref="Start"/> vẫn giữ nguyên hành vi
    /// ném lỗi khi đã có deadline, vì đó là chốt chặn chống client gọi BeginAnswering hai lần.
    /// </summary>
    public Deadline Restart(Guid sessionId, string questionId, int seconds)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        if (seconds <= 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var deadline = new Deadline(sessionId, questionId, clock.GetUtcNow().AddSeconds(seconds), seconds);
        _deadlines[sessionId] = deadline;
        return deadline;
    }

    public Deadline? GetDeadline(Guid sessionId) => _deadlines.GetValueOrDefault(sessionId);
    public int SecondsRemaining(Deadline deadline) => Math.Clamp(
        (int)Math.Ceiling((deadline.EndsAtUtc - clock.GetUtcNow()).TotalSeconds), 0, deadline.TotalSecondsAllowed);
    public void Restore(Deadline deadline)
    {
        if (!_deadlines.TryAdd(deadline.SessionId, deadline))
            throw new InvalidOperationException("An answering timer already exists for this session.");
    }

    /// <summary>
    /// Restarts a paused deadline with the seconds that were left when it was paused (e.g. while STT was running),
    /// so server-side processing time is not charged to the student. Never adds time beyond what was left.
    /// </summary>
    public Deadline Resume(Deadline paused, int secondsRemaining)
    {
        var seconds = Math.Clamp(secondsRemaining, 0, paused.TotalSecondsAllowed);
        var resumed = paused with { EndsAtUtc = clock.GetUtcNow().AddSeconds(seconds) };
        Restore(resumed);
        return resumed;
    }
    /// <summary>
    /// Tạm dừng đồng hồ và GHI NHỚ số giây còn lại. Dùng khi sinh viên nộp bài (STT đang chạy)
    /// và khi AI đang sinh/đọc câu hỏi xoáy — thời gian xử lý của server không bị trừ vào giờ thi.
    /// Khác <see cref="Stop"/>: Stop xoá hẳn, còn Pause giữ lại để <see cref="ResumePaused"/> trả
    /// đúng phần thời gian còn dở thay vì cấp lại nguyên TimeLimitSeconds.
    /// </summary>
    public PausedDeadline? Pause(Guid sessionId)
    {
        if (!_deadlines.TryRemove(sessionId, out var deadline)) return null;
        var paused = new PausedDeadline(deadline.SessionId, deadline.QuestionId,
            SecondsRemaining(deadline), deadline.TotalSecondsAllowed);
        _paused[sessionId] = paused;
        return paused;
    }

    /// <summary>
    /// Chạy tiếp đồng hồ đã tạm dừng với đúng số giây còn lại. Trả null khi không có mốc tạm dừng
    /// (câu mới hoàn toàn) hoặc đã hết giờ — lúc đó tầng gọi dùng <see cref="Start"/> với thời gian đầy đủ.
    /// <paramref name="questionId"/> cho phép gắn lại mốc vào câu đang hỏi: câu hỏi xoáy có
    /// QuestionId riêng (dạng ...-FU1) nhưng vẫn dùng chung quỹ thời gian của câu chính.
    /// </summary>
    public Deadline? ResumePaused(Guid sessionId, string? questionId = null)
    {
        if (!_paused.TryRemove(sessionId, out var paused)) return null;
        if (paused.SecondsRemaining <= 0) return null;
        var deadline = new Deadline(paused.SessionId, questionId ?? paused.QuestionId,
            clock.GetUtcNow().AddSeconds(paused.SecondsRemaining), paused.TotalSecondsAllowed);
        _deadlines[sessionId] = deadline;
        return deadline;
    }

    /// <summary>
    /// Gắn lại deadline đang chạy sang câu hỏi khác mà KHÔNG đụng tới thời gian còn lại.
    /// Dùng khi chuyển từ câu chính sang câu hỏi xoáy: hai câu có QuestionId khác nhau nhưng
    /// chung một quỹ 1p30s, nên chỉ đổi nhãn câu chứ không reset đồng hồ.
    /// </summary>
    public Deadline? Rebind(Guid sessionId, string questionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(questionId);
        if (!_deadlines.TryGetValue(sessionId, out var deadline)) return null;
        var rebound = deadline with { QuestionId = questionId };
        _deadlines[sessionId] = rebound;
        return rebound;
    }

    public PausedDeadline? GetPaused(Guid sessionId) => _paused.GetValueOrDefault(sessionId);

    public Guid[] GetActiveSessionIds() => _deadlines.Keys.ToArray();

    /// <summary>Xoá cả mốc đang chạy lẫn mốc tạm dừng — dùng khi câu/phiên đã kết thúc hẳn.</summary>
    public void Stop(Guid sessionId)
    {
        _deadlines.TryRemove(sessionId, out _);
        _paused.TryRemove(sessionId, out _);
    }

    public VivaTimeRemainingDto? GetRemaining(Guid sessionId)
    {
        var deadline = GetDeadline(sessionId);
        return deadline is null ? null : new(sessionId,
            SecondsRemaining(deadline),
            deadline.TotalSecondsAllowed);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
