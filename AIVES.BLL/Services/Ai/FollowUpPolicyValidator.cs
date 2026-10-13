using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using AIVES.BLL.Interfaces.Ai;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Validates and sanitizes AI-generated follow-up questions before they are sent to students.
/// Filters offensive/hallucinated/empty content and substitutes curated domain fallback questions
/// when the LLM output is unsafe or unparseable. Does NOT score or grade student answers.
/// </summary>
public class FollowUpPolicyValidator
{
    private readonly ILogger<FollowUpPolicyValidator>? _logger;

    // Max follow-up question length (chars). Anything above is likely hallucination.
    private const int MaxQuestionLength = 500;
    // Min meaningful length — anything shorter is too vague.
    private const int MinQuestionLength = 10;

    // Offensive / prohibited keyword patterns (case-insensitive). Extend as needed.
    private static readonly string[] ProhibitedPatterns =
    [
        @"\bscore\b",
        @"\bgrade\b",
        @"\bpass(ed|ing)?\b",
        @"\bfail(ed|ing)?\b",
        @"\bcorrect\b",
        @"\bwrong\b",
        @"\bpoints?\b",
        @"\b(very\s+)?(good|bad|poor|excellent|terrible)\s+(answer|response)\b",
        @"\byou\s+said\s+nothing\b",
        @"\bI\s+am\s+an?\s+AI\b",
        @"\bopenai\b",
        @"\bgpt\b",
        @"\blanguage\s+model\b",
    ];

    // Curated domain-specific fallback question banks grouped by topic.
    private static readonly Dictionary<string, List<string>> FallbackBankByTopic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dbcontext"] =
        [
            "Why is it unsafe to inject a Scoped DbContext into a Singleton service in ASP.NET Core?",
            "How does DbContext's change tracker differentiate between Added, Modified, and Deleted entity states?",
            "What is the consequence of sharing a single DbContext instance across multiple concurrent HTTP requests?",
        ],
        ["lifetime"] =
        [
            "How does the difference between Scoped, Singleton, and Transient lifetimes affect memory usage in a web application?",
            "In which scenario would registering a service as Transient cause more performance overhead than Scoped?",
        ],
        ["repository"] =
        [
            "How does the Repository pattern isolate business logic from the underlying data access technology?",
            "What happens to the Unit of Work scope if a repository throws an exception mid-transaction?",
        ],
        ["signalr"] =
        [
            "How do SignalR group channels enforce session isolation so one student cannot intercept another's exam events?",
            "What reconnection strategy should the client adopt if the WebSocket transport is unavailable in the student's network environment?",
        ],
        ["async"] =
        [
            "What is the risk of calling a synchronous blocking method inside an asynchronous pipeline?",
            "How can an unobserved Task exception in a fire-and-forget operation cause process crashes in .NET 8?",
        ],
        ["architecture"] =
        [
            "How does enforcing zero direct database access in the Presentation layer prevent architectural erosion over time?",
            "Why should SignalR Hubs delegate all business logic to a BLL service rather than implementing it directly?",
        ],
    };

    // Universal fallback questions when no topic-specific match is found.
    private static readonly List<string> UniversalFallbacks =
    [
        "Can you elaborate on how your approach handles edge cases or unexpected inputs in a production environment?",
        "What potential issues might arise if this implementation is deployed at scale with hundreds of concurrent users?",
        "How would you verify the correctness of this solution through automated testing?",
        "What design trade-offs did you consider, and why did you choose this particular approach?",
        "Can you explain what would happen if an external dependency (such as the database or an API) becomes temporarily unavailable?",
    ];

    public FollowUpPolicyValidator(ILogger<FollowUpPolicyValidator>? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Validates a follow-up result produced by an LLM service.
    /// Returns the original result if valid; otherwise returns a sanitized fallback.
    /// </summary>
    public FollowUpResult Validate(FollowUpResult result, string originalQuestion = "", string transcript = "")
    {
        // 1. Handle service-level failure
        if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.FollowUpQuestionText))
        {
            _logger?.LogWarning("Follow-up result was unsuccessful or empty — substituting fallback question.");
            return CreateFallback(originalQuestion, transcript, "LLM returned empty or failed result.");
        }

        var text = result.FollowUpQuestionText.Trim();

        // 2. Enforce length bounds
        if (text.Length < MinQuestionLength)
        {
            _logger?.LogWarning("Follow-up question too short ({Length} chars) — substituting fallback.", text.Length);
            return CreateFallback(originalQuestion, transcript, $"Question too short ({text.Length} chars).");
        }

        if (text.Length > MaxQuestionLength)
        {
            _logger?.LogWarning("Follow-up question too long ({Length} chars) — substituting fallback.", text.Length);
            return CreateFallback(originalQuestion, transcript, $"Question too long ({text.Length} chars).");
        }

        // 3. Check prohibited content patterns
        foreach (var pattern in ProhibitedPatterns)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                _logger?.LogWarning("Follow-up question matched prohibited pattern '{Pattern}' — substituting fallback.", pattern);
                return CreateFallback(originalQuestion, transcript, $"Prohibited content pattern matched: {pattern}");
            }
        }

        // 4. Validate cognitive intent is one of the allowed values
        var allowedIntents = new[] { "Clarification", "Deep-Dive", "Edge-Case", "Counter-Example" };
        var intent = result.CognitiveIntent?.Trim();
        if (string.IsNullOrWhiteSpace(intent) || !allowedIntents.Contains(intent, StringComparer.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("Unknown cognitive intent '{Intent}' — normalizing to 'Deep-Dive'.", intent);
            return result with { CognitiveIntent = "Deep-Dive" };
        }

        // All checks pass — return original result as-is
        return result;
    }

    /// <summary>
    /// Creates a safe, topic-relevant fallback follow-up question.
    /// </summary>
    private FollowUpResult CreateFallback(string originalQuestion, string transcript, string reason)
    {
        var topicQuestion = SelectTopicFallback(originalQuestion, transcript);
        _logger?.LogInformation("Fallback question selected. Reason: {Reason} | Question: {Question}", reason, topicQuestion);
        return new FollowUpResult(
            IsSuccess: true,
            FollowUpQuestionText: topicQuestion,
            CognitiveIntent: "Clarification"
        );
    }

    /// <summary>
    /// Selects the most contextually relevant fallback question from the domain banks.
    /// </summary>
    private static string SelectTopicFallback(string question, string transcript)
    {
        var combined = $"{question} {transcript}".ToLowerInvariant();

        foreach (var (keyword, questions) in FallbackBankByTopic)
        {
            if (combined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return questions[Math.Abs(combined.GetHashCode()) % questions.Count];
            }
        }

        // Fall through to universal fallbacks
        return UniversalFallbacks[Math.Abs(combined.GetHashCode()) % UniversalFallbacks.Count];
    }
}
