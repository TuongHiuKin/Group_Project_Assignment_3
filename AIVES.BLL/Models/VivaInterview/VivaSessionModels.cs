namespace AIVES.BLL.Models.VivaInterview;

public enum VivaSessionState : byte
{
    Created = 0,             // Session record created, awaiting student entry
    Ready = 1,               // Student connected to room and verified
    QuestionDelivered = 2,   // Main question displayed and TTS played
    Answering = 3,           // Microphone recording active / Timer ticking
    Transcribing = 4,        // Audio uploaded, STT processing in progress
    TranscriptReady = 5,     // Transcript parsed and persisted
    FollowUpGenerating = 6,  // LLM analyzing response to synthesize follow-up
    FollowUpDelivered = 7,   // Follow-up question displayed and TTS played
    Completed = 8,           // All turns completed, results persisted
    Failed = 9,              // Fatal error encountered
    TimedOut = 10            // Session or turn timed out
}

public sealed record VivaQuestionDto(
    string QuestionId,
    string ExamId,
    string Title,
    string Content,
    int TimeLimitSeconds,
    string? AudioUrl = null
);

public sealed record VivaAnswerSubmissionDto(
    Guid SessionId,
    string QuestionId,
    byte[] AudioData,
    string ContentType, // e.g. "audio/webm" or "audio/wav"
    int DurationSeconds
);

public sealed record VivaTranscriptDto(
    Guid SessionId,
    Guid TurnId,
    string QuestionId,
    string TranscriptText,
    decimal ConfidenceScore,
    DateTime CompletedAtUtc
);

public sealed record VivaFollowUpDto(
    Guid SessionId,
    Guid TurnId,
    string FollowUpQuestionId,
    string FollowUpContent,
    int FollowUpOrdinal, // 1 for first follow-up, 2 for second, etc.
    string? AudioUrl = null
);

public sealed record VivaSessionStatusDto(
    Guid SessionId,
    string ExamId,
    string StudentId,
    VivaSessionState State,
    int CurrentTurnOrdinal,
    int MaxTurnsAllowed,
    DateTime UpdatedAtUtc,
    string? Message = null
);

public sealed record VivaTimeRemainingDto(
    Guid SessionId,
    int SecondsRemaining,
    int TotalSecondsAllowed
);

public sealed record VivaErrorDto(
    Guid SessionId,
    string ErrorCode,
    string ErrorMessage,
    bool CanRetry
);
