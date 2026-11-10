namespace AIVES.BLL.Models.VivaInterview;

/// <summary>
/// Một lượt trả lời đã lưu của một câu hỏi, dùng để hiển thị lại toàn bộ hội thoại
/// (câu hỏi chính → câu trả lời → câu hỏi xoáy → câu trả lời xoáy) trên trang thi và báo cáo.
/// </summary>
/// <param name="TurnId">Mã lượt.</param>
/// <param name="QuestionId">Câu hỏi chính mà lượt này thuộc về (follow-up dùng chung QuestionId của câu chính).</param>
/// <param name="TurnOrdinal">Thứ tự lượt trong toàn phiên, dùng để sắp xếp.</param>
/// <param name="TranscriptText">Câu trả lời đã nhận diện giọng nói của lượt này.</param>
/// <param name="ConfidenceScore">Độ tin cậy STT (0..1).</param>
/// <param name="FollowUpQuestionText">Câu hỏi xoáy được sinh ra sau lượt này (null nếu không có).</param>
/// <param name="FollowUpAudioUrl">Audio của câu hỏi xoáy (null nếu TTS lỗi).</param>
public sealed record VivaTurnDto(
    Guid TurnId,
    string QuestionId,
    int TurnOrdinal,
    string? TranscriptText,
    decimal? ConfidenceScore,
    string? FollowUpQuestionText,
    string? FollowUpAudioUrl);

/// <summary>Read-only recovery data returned by JoinSession; no new client event is introduced.</summary>
public sealed record VivaSessionSnapshotDto(
    VivaSessionStatusDto Status,
    VivaQuestionDto? CurrentQuestion,
    VivaTranscriptDto? LatestTranscript,
    VivaFollowUpDto? CurrentFollowUp,
    VivaTimeRemainingDto? Timer)
{
    /// <summary>
    /// Toàn bộ lượt đã trả lời, sắp theo TurnOrdinal. Trang thi dùng để dựng lại lịch sử hội thoại
    /// khi sinh viên quay lại phiên đã hoàn thành; báo cáo dùng cùng dữ liệu để hiển thị câu xoáy.
    /// </summary>
    public IReadOnlyList<VivaTurnDto> Turns { get; init; } = Array.Empty<VivaTurnDto>();
}
