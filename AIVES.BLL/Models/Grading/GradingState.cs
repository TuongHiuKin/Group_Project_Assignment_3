namespace AIVES.BLL.Models.Grading;

/// <summary>
/// Trạng thái chấm điểm của một câu hỏi hoặc toàn bộ phiên thi vấn đáp.
/// </summary>
public enum GradingState : byte
{
    Pending = 0,    // Queued or awaiting grading
    AiGraded = 1,   // Graded by AI/fallback, awaiting instructor review
    Finalized = 2,  // Confirmed or overridden by teacher (Official)
    Failed = 3      // AI evaluation failed and pending manual review/regrade
}

