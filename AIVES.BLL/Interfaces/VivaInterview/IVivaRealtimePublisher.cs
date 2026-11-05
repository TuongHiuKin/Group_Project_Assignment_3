using AIVES.BLL.Models.Grading;
using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Interfaces.VivaInterview;

public interface IVivaRealtimePublisher
{
    Task PublishStatusChangedAsync(VivaSessionStatusDto status, CancellationToken ct = default);
    Task PublishTranscriptReadyAsync(VivaTranscriptDto transcript, CancellationToken ct = default);
    Task PublishFollowUpGeneratedAsync(VivaFollowUpDto followUp, CancellationToken ct = default);
    Task PublishTimeRemainingAsync(VivaTimeRemainingDto timer, CancellationToken ct = default);
    Task PublishSessionCompletedAsync(Guid sessionId, string examId, string studentId, CancellationToken ct = default);
    Task PublishErrorAsync(VivaErrorDto error, CancellationToken ct = default);

    // Group 4 — lecturer group grading-{examId} (docs/G4-CONTRACTS.md §5.2)
    Task PublishGradingStartedAsync(string examId, GradingStartedDto started, CancellationToken ct = default);
    Task PublishGradingCompletedAsync(string examId, GradingCompletedDto completed, CancellationToken ct = default);
    Task PublishGradingFailedAsync(string examId, GradingFailedDto failed, CancellationToken ct = default);
}
