using AIVES.BLL.Models.ExamReporting;

namespace AIVES.BLL.Interfaces.ExamReporting;

/// <summary>
/// Chấm điểm tự động theo Rubric từ khóa (rule-based, không gọi mạng hay AI).
/// Chấm điểm AI nâng cao (Nhóm 4) nằm ngoài phạm vi ASM2.
/// </summary>
public interface IRubricGradingService
{
    /// <summary>Rubric của một đề thi, theo thứ tự câu hỏi. Dùng chung cho LecturerDashboard và chấm điểm.</summary>
    IReadOnlyList<QuestionRubric> GetRubrics(string examId);

    QuestionRubric? GetRubric(string questionId);

    /// <summary>
    /// Chấm một câu. Trả về null khi câu hỏi không có Rubric (giữ trạng thái "Chưa chấm").
    /// </summary>
    /// <param name="questionId">Mã câu hỏi, ví dụ EXAM01-Q01.</param>
    /// <param name="questionText">Nội dung câu hỏi (và câu hỏi phụ) để phát hiện câu trả lời chỉ đọc lại câu hỏi.</param>
    /// <param name="answerTranscript">Toàn bộ transcript trả lời của sinh viên cho câu hỏi.</param>
    /// <param name="passThresholdScore">Ngưỡng đạt (ExamSettings.PassThresholdScore).</param>
    RubricGradingResult? Grade(string questionId, string? questionText, string? answerTranscript, decimal passThresholdScore);
}
