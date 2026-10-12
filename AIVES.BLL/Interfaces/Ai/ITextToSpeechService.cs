namespace AIVES.BLL.Interfaces.Ai;

public interface ITextToSpeechService
{
    Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default);
}

public sealed record TextToSpeechResult(
    bool IsSuccess,
    string? AudioUrl,
    byte[]? AudioData = null,
    string? ErrorMessage = null
);
