namespace AIVES.BLL.Interfaces.Ai;

public interface IFollowUpGeneratorService
{
    Task<FollowUpResult> GenerateFollowUpQuestionAsync(
        string originalQuestion,
        string studentAnswerTranscript,
        string subjectContext,
        CancellationToken ct = default);
}

public sealed record FollowUpResult(
    bool IsSuccess,
    string FollowUpQuestionText,
    string CognitiveIntent, // e.g. "Clarification", "Deep-Dive", "Edge-Case"
    string? ErrorMessage = null
);
