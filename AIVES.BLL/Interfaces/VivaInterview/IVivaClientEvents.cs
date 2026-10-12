using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Interfaces.VivaInterview;

public interface IVivaClientEvents
{
    Task SessionStatusChanged(VivaSessionStatusDto status);
    Task TranscriptReady(VivaTranscriptDto transcript);
    Task FollowUpGenerated(VivaFollowUpDto followUp);
    Task TimeRemainingChanged(VivaTimeRemainingDto timer);
    Task SessionCompleted(VivaSessionStatusDto status);
    Task VivaError(VivaErrorDto error);
}
