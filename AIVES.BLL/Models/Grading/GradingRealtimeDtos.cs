namespace AIVES.BLL.Models.Grading;

// SignalR payloads for the lecturer group grading-{examId} (docs/G4-CONTRACTS.md §5.2).

/// <summary>AI evaluation of one question has started.</summary>
public sealed record GradingStartedDto(Guid SessionId, string QuestionId, DateTime StartedAtUtc);

/// <summary>Grading of one question finished and was persisted. <c>State</c> is the numeric <see cref="GradingState"/>.</summary>
public sealed record GradingCompletedDto(
    Guid SessionId, string QuestionId, byte State, decimal EarnedScore, decimal MaxScore, bool IsAiGraded);

/// <summary>Grading of one question failed. <c>ErrorMessage</c> is always a safe, user-facing message.</summary>
public sealed record GradingFailedDto(Guid SessionId, string QuestionId, string ErrorMessage, bool FallbackTriggered);
