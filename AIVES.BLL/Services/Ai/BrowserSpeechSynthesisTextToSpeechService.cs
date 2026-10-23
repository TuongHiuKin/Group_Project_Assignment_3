using AIVES.BLL.Interfaces.Ai;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// TTS used when <c>AiServices:UseBrowserSpeechSynthesis</c> is on: the exam page reads questions with
/// the browser speechSynthesis API, so no cloud call is made and no audio file is produced.
/// </summary>
public sealed class BrowserSpeechSynthesisTextToSpeechService : ITextToSpeechService
{
    public Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new TextToSpeechResult(true, null));
    }
}
