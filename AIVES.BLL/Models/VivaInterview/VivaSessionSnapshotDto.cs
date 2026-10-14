namespace AIVES.BLL.Models.VivaInterview;

/// <summary>Read-only recovery data returned by JoinSession; no new client event is introduced.</summary>
public sealed record VivaSessionSnapshotDto(
    VivaSessionStatusDto Status,
    VivaQuestionDto? CurrentQuestion,
    VivaTranscriptDto? LatestTranscript,
    VivaFollowUpDto? CurrentFollowUp,
    VivaTimeRemainingDto? Timer);
