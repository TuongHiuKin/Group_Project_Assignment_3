using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;

namespace AIVES.BLL.Services.Ai.Mocks;

/// <summary>
/// Deterministic offline mock implementation of ITextToSpeechService.
/// Returns mock audio URLs and dummy MP3 byte buffers without cloud synthesis.
/// </summary>
public class MockTextToSpeechService : ITextToSpeechService
{
    private readonly ILogger<MockTextToSpeechService>? _logger;
    private const string DefaultMockAudioUrl = "/audio/mock-question.mp3";

    // Minimal valid MP3 header frame bytes (MPEG-1 Layer 3, 128kbps, 44.1kHz stereo)
    private static readonly byte[] MockMp3Bytes = [0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00, 0x00, 0x00];

    public string ConfiguredAudioUrl { get; set; } = DefaultMockAudioUrl;
    public bool ProvideAudioData { get; set; } = true;
    public bool ShouldSucceed { get; set; } = true;
    public string? SimulatedErrorMessage { get; set; }

    public MockTextToSpeechService(ILogger<MockTextToSpeechService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger?.LogWarning("Mock TTS received empty text input.");
            return new TextToSpeechResult(false, null, null, "Text to synthesize cannot be empty.");
        }

        if (!ShouldSucceed)
        {
            var error = SimulatedErrorMessage ?? "Mock TTS simulated failure.";
            _logger?.LogWarning("Mock TTS failing with message: {Error}", error);
            return new TextToSpeechResult(false, null, null, error);
        }

        // Simulate async latency (10ms)
        await Task.Delay(10, ct);

        string audioUrl = ConfiguredAudioUrl;

        _logger?.LogInformation("Mock TTS synthesized {Length} chars with locale {Locale} -> {Url}",
            text.Length, voiceLocale, audioUrl);

        return new TextToSpeechResult(
            IsSuccess: true,
            AudioUrl: audioUrl,
            AudioData: ProvideAudioData ? MockMp3Bytes : null
        );
    }
}
