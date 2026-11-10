using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Interfaces.VivaInterview;

public interface IVivaSessionOrchestrator
{
    Task<VivaSessionStatusDto> InitializeSessionAsync(string examId, string studentId, CancellationToken ct = default);

    /// <summary>
    /// True when the exam has at least one question. Callers must check this before
    /// <see cref="InitializeSessionAsync"/> so a mistyped exam id is rejected instead of
    /// persisting a phantom session that can never be started.
    /// </summary>
    Task<bool> ExamExistsAsync(string examId, CancellationToken ct = default);

    Task<bool> AuthorizeUserForSessionAsync(Guid sessionId, string userId, CancellationToken ct = default);

    /// <summary>
    /// Đếm số lượt thi sinh viên đã tạo cho đề này và cho biết còn được tạo lượt mới hay không.
    /// Trước đây không có chốt chặn nào: bấm nút là tạo được lượt mới vô hạn dù báo cáo ghi giới hạn 3.
    /// </summary>
    Task<(int Used, int Max, bool CanStart)> GetAttemptAllowanceAsync(
        string examId, string studentId, CancellationToken ct = default);

    /// <summary>
    /// Id các phiên đang chờ trả lời (QuestionDelivered / Answering / FollowUpDelivered) nhưng
    /// CHƯA hoàn tất. Worker dùng danh sách này để bắt các phiên không còn deadline trong RAM —
    /// sau restart hoặc khi client không gọi BeginAnswering — tránh treo vĩnh viễn.
    /// Chỉ đọc dữ liệu, không đổi trạng thái.
    /// </summary>
    Task<List<Guid>> GetWaitingSessionIdsAsync(CancellationToken ct = default);

    Task<VivaQuestionDto> StartSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<VivaTranscriptDto> SubmitAnswerAudioAsync(VivaAnswerSubmissionDto submission, CancellationToken ct = default);
    Task<VivaFollowUpDto?> GenerateFollowUpAsync(Guid sessionId, Guid turnId, CancellationToken ct = default);

    /// <summary>
    /// Bảo đảm câu hỏi xoáy đã có file audio do AI đọc, để nó được đọc bằng đúng giọng
    /// với câu hỏi chính. Trả về URL nếu đã có sẵn hoặc vừa sinh xong; <c>null</c> nếu
    /// lượt đó không có câu hỏi xoáy hoặc TTS thất bại (client sẽ phải fallback).
    /// </summary>
    Task<string?> EnsureFollowUpAudioAsync(Guid sessionId, Guid turnId, CancellationToken ct = default);
    Task<VivaSessionStatusDto> CompleteSessionAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Called by the timer worker when the answering window expires. Records an empty turn (scored 0),
    /// then advances to the next question or completes the session — so a timeout no longer strands the
    /// session in <c>TimedOut</c> where it can never be submitted, finished, or graded.
    /// </summary>
    Task HandleAnswerTimeoutAsync(Guid sessionId, string questionId, CancellationToken ct = default);

    /// <summary>
    /// Quét một lần lúc khởi động: tìm các phiên còn kẹt ở <c>TimedOut</c> do app dừng đúng lúc
    /// timer kêu (deadline chỉ nằm trong bộ nhớ) và đưa chúng đi tiếp. Không có bước này, những
    /// phiên đó vĩnh viễn không được chấm điểm và không lên báo cáo.
    ///
    /// Đặt ở BLL để Presentation không phải inject Repository — giữ đúng ranh giới 3 lớp.
    /// </summary>
    Task<int> RecoverInterruptedTimeoutsAsync(CancellationToken ct = default);
}
