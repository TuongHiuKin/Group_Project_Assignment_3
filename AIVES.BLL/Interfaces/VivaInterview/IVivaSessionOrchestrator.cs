using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Interfaces.VivaInterview;

public interface IVivaSessionOrchestrator
{
    Task<VivaSessionStatusDto> InitializeSessionAsync(string examId, string studentId, CancellationToken ct = default);
    Task<bool> AuthorizeUserForSessionAsync(Guid sessionId, string userId, CancellationToken ct = default);
    Task<VivaQuestionDto> StartSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<VivaTranscriptDto> SubmitAnswerAudioAsync(VivaAnswerSubmissionDto submission, CancellationToken ct = default);
    Task<VivaFollowUpDto?> GenerateFollowUpAsync(Guid sessionId, Guid turnId, CancellationToken ct = default);
    Task<VivaSessionStatusDto> CompleteSessionAsync(Guid sessionId, CancellationToken ct = default);
}
