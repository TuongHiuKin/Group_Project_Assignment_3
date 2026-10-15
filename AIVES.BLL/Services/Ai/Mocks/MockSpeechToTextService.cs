using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;

namespace AIVES.BLL.Services.Ai.Mocks;

/// <summary>
/// Deterministic offline mock implementation of ISpeechToTextService.
/// Allows local testing and CI/CD pipelines to run without calling external cloud APIs.
/// </summary>
public class MockSpeechToTextService : ISpeechToTextService
{
    private readonly ILogger<MockSpeechToTextService>? _logger;
    private const string DefaultMockTranscript = "Scoped DbContext is instantiated once per HTTP request and should not be held in a Singleton service.";

    public string ConfiguredTranscript { get; set; } = DefaultMockTranscript;
    public decimal ConfiguredConfidence { get; set; } = 0.96m;
    public bool ShouldSucceed { get; set; } = true;
    public string? SimulatedErrorMessage { get; set; }

    public MockSpeechToTextService(ILogger<MockSpeechToTextService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (audioStream == null)
        {
            _logger?.LogWarning("Mock STT received a null audio stream.");
            return new SpeechToTextResult(false, string.Empty, 0m, "Audio stream cannot be null.");
        }

        if (!ShouldSucceed)
        {
            var error = SimulatedErrorMessage ?? "Mock STT simulated failure.";
            _logger?.LogWarning("Mock STT failing with message: {Error}", error);
            return new SpeechToTextResult(false, string.Empty, 0m, error);
        }

        // Simulate minimal async processing latency (e.g. 10ms)
        await Task.Delay(10, ct);

        _logger?.LogInformation("Mock STT processed audio ({ContentType}) with confidence {Confidence}", contentType, ConfiguredConfidence);

        return new SpeechToTextResult(
            IsSuccess: true,
            TranscriptText: ConfiguredTranscript,
            ConfidenceScore: ConfiguredConfidence
        );
    }
}
