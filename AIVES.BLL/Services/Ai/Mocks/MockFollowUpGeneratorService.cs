using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;

namespace AIVES.BLL.Services.Ai.Mocks;

/// <summary>
/// Deterministic offline mock implementation of IFollowUpGeneratorService.
/// Analyzes questions and transcripts using rule-based keyword context to generate realistic viva follow-up questions without LLM costs.
/// </summary>
public class MockFollowUpGeneratorService : IFollowUpGeneratorService
{
    private readonly ILogger<MockFollowUpGeneratorService>? _logger;

    public bool ShouldSucceed { get; set; } = true;
    public string? SimulatedErrorMessage { get; set; }
    public string? OverrideFollowUpQuestion { get; set; }
    public string? OverrideCognitiveIntent { get; set; }

    public MockFollowUpGeneratorService(ILogger<MockFollowUpGeneratorService>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FollowUpResult> GenerateFollowUpQuestionAsync(
        string originalQuestion,
        string studentAnswerTranscript,
        string subjectContext,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(originalQuestion))
        {
            _logger?.LogWarning("Mock Follow-up received empty original question.");
            return new FollowUpResult(false, string.Empty, string.Empty, "Original question cannot be empty.");
        }

        if (!ShouldSucceed)
        {
            var error = SimulatedErrorMessage ?? "Mock Follow-up simulated failure.";
            _logger?.LogWarning("Mock Follow-up failing with message: {Error}", error);
            return new FollowUpResult(false, string.Empty, string.Empty, error);
        }

        // Simulate async latency (15ms)
        await Task.Delay(15, ct);

        if (!string.IsNullOrWhiteSpace(OverrideFollowUpQuestion))
        {
            return new FollowUpResult(
                IsSuccess: true,
                FollowUpQuestionText: OverrideFollowUpQuestion,
                CognitiveIntent: OverrideCognitiveIntent ?? "Clarification"
            );
        }

        var (questionText, intent) = GenerateContextualQuestion(originalQuestion, studentAnswerTranscript, subjectContext);

        _logger?.LogInformation("Mock Follow-up generated question with intent '{Intent}' for subject '{Subject}'",
            intent, subjectContext);

        return new FollowUpResult(
            IsSuccess: true,
            FollowUpQuestionText: questionText,
            CognitiveIntent: intent
        );
    }

    private static (string Question, string Intent) GenerateContextualQuestion(
        string originalQuestion,
        string transcript,
        string subjectContext)
    {
        var text = (transcript ?? string.Empty).ToLowerInvariant();

        // 1. Very short or vague answer -> Clarification
        if (string.IsNullOrWhiteSpace(text) || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4)
        {
            return (
                "Could you please elaborate more specifically on how your approach addresses the core requirement?",
                "Clarification"
            );
        }

        // 2. DbContext / Lifetime / Dependency Injection keywords -> Deep-Dive
        if (text.Contains("dbcontext") || text.Contains("scoped") || text.Contains("singleton") || text.Contains("transient"))
        {
            return (
                "Why would injecting a Scoped DbContext instance into a Singleton service cause concurrency exceptions or memory leaks?",
                "Deep-Dive"
            );
        }

        // 3. Concurrency / Multi-threading / Async / Await keywords -> Edge-Case
        if (text.Contains("async") || text.Contains("await") || text.Contains("thread") || text.Contains("task") || text.Contains("concurrency"))
        {
            return (
                "What happens to the database connection pool and thread execution if an unhandled exception occurs inside the asynchronous pipeline?",
                "Edge-Case"
            );
        }

        // 4. Repository / 3-Layer / Architecture / Separation of concerns -> Deep-Dive
        if (text.Contains("layer") || text.Contains("repository") || text.Contains("bll") || text.Contains("dal") || text.Contains("architecture"))
        {
            return (
                "How does isolating business rules in BLL without referencing EF Core entities protect against architectural erosion over time?",
                "Deep-Dive"
            );
        }

        // 5. SignalR / Realtime / WebSocket keywords -> Edge-Case
        if (text.Contains("signalr") || text.Contains("hub") || text.Contains("websocket") || text.Contains("realtime"))
        {
            return (
                "How do you ensure that viva examination event broadcasts are delivered reliably if a student experiences temporary network disconnection?",
                "Edge-Case"
            );
        }

        // Default contextual follow-up based on question
        return (
            $"Based on your explanation, what potential trade-offs or edge cases should be considered when implementing this in a production {subjectContext} environment?",
            "Deep-Dive"
        );
    }
}
