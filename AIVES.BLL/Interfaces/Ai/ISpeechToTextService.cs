namespace AIVES.BLL.Interfaces.Ai;

public interface ISpeechToTextService
{
    Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default);
}

public sealed record SpeechToTextResult(
    bool IsSuccess,
    string TranscriptText,
    decimal ConfidenceScore,
    string? ErrorMessage = null
);
