using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Interfaces.VivaInterview;

public interface IVivaExamRoomService
{
    Task<string> CreateExamRoomAsync(CreateExamRoomDto request, CancellationToken ct = default);
    Task<bool> ValidateExamRoomCodeAsync(string examId, CancellationToken ct = default);
    Task<List<ExamRoomSummaryDto>> GetAvailableExamRoomsAsync(CancellationToken ct = default);
    Task<ExamRoomDetailDto?> GetExamRoomDetailsAsync(string examId, CancellationToken ct = default);
}
