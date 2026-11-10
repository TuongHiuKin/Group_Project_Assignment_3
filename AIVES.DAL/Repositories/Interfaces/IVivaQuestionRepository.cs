using AIVES.DAL.Entities;

namespace AIVES.DAL.Repositories.Interfaces;

public interface IVivaQuestionRepository
{
    Task<List<VivaQuestion>> GetQuestionsByExamAsync(string examId, CancellationToken ct = default);
    Task<List<string>> GetDistinctExamIdsAsync(CancellationToken ct = default);
    Task<VivaQuestion?> GetByIdAsync(string questionId, CancellationToken ct = default);

    /// <summary>
    /// Lưu đường dẫn audio TTS đã sinh cho câu hỏi, để lần sau không phải gọi lại API.
    /// Trả về false nếu không tìm thấy câu hỏi.
    /// </summary>
    Task<bool> SaveAudioUrlAsync(string questionId, string audioUrl, CancellationToken ct = default);
}
