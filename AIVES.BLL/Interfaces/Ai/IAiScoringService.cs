using System.Threading;
using System.Threading.Tasks;

namespace AIVES.BLL.Interfaces.Ai;

public sealed record AiGradingResult(
    decimal Score,
    bool IsPassed,
    string Feedback,
    string Strengths,
    string Weaknesses,
    string Disclaimer
);

public interface IAiScoringService
{
    Task<AiGradingResult> GradeAnswerAsync(
        string questionTitle,
        string questionContent,
        string initialAnswerTranscript,
        string? followUpQuestion,
        string? followUpAnswerTranscript,
        CancellationToken ct = default);
}
