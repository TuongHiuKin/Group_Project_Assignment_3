using AIVES.BLL.Models.Grading;

namespace AIVES.BLL.Interfaces.Ai;

/// <summary>
/// Giao diện phân tích các tín hiệu phụ trong giọng nói sinh viên (thời gian trả lời, tốc độ nói WPM, từ đệm ngập ngừng).
/// Các chỉ số này mang tính chất tham khảo cho giảng viên và không dùng để trực tiếp tính điểm trừ.
/// </summary>
public interface ISpeechMetricsAnalyzer
{
    /// <summary>
    /// Phân tích các chỉ số giọng nói từ transcript và thời lượng trả lời.
    /// </summary>
    /// <param name="mainTranscript">Nội dung câu trả lời chính của sinh viên.</param>
    /// <param name="durationSeconds">Thời lượng sinh viên ghi âm (giây). Nếu &lt;= 0, hệ thống sẽ ước tính dựa trên WPM chuẩn.</param>
    /// <param name="followUpTurns">Danh sách các lượt trả lời bổ sung (nếu có).</param>
    /// <returns>Đối tượng chứa các chỉ số âm thanh và phát âm <see cref="SpeechMetricsDto"/>.</returns>
    SpeechMetricsDto Analyze(
        string? mainTranscript, 
        double durationSeconds = 0, 
        IEnumerable<string>? followUpTurns = null);
}

