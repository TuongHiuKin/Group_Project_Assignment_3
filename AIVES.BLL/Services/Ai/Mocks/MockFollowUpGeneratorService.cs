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
                "Bạn có thể giải thích chi tiết và cụ thể hơn về cách tiếp cận của mình đối với yêu cầu cốt lõi này không?",
                "Clarification"
            );
        }

        // 2. DbContext / Lifetime / Dependency Injection keywords -> Deep-Dive
        if (text.Contains("dbcontext") || text.Contains("scoped") || text.Contains("singleton") || text.Contains("transient"))
        {
            return (
                "Tại sao việc đăng ký DbContext với vòng đời Scoped và inject vào một Singleton service lại có thể gây ra lỗi xung đột đa luồng (concurrency) hoặc rò rỉ bộ nhớ?",
                "Deep-Dive"
            );
        }

        // 3. Concurrency / Multi-threading / Async / Await keywords -> Edge-Case
        if (text.Contains("async") || text.Contains("await") || text.Contains("thread") || text.Contains("task") || text.Contains("concurrency"))
        {
            return (
                "Điều gì sẽ xảy ra với connection pool của cơ sở dữ liệu nếu xuất hiện exception (ngoại lệ) không được xử lý bên trong pipeline bất đồng bộ async/await?",
                "Edge-Case"
            );
        }

        // 4. Repository / 3-Layer / Architecture / Separation of concerns -> Deep-Dive
        if (text.Contains("layer") || text.Contains("repository") || text.Contains("bll") || text.Contains("dal") || text.Contains("architecture"))
        {
            return (
                "Làm thế nào việc cô lập các quy tắc nghiệp vụ trong tầng BLL mà không phụ thuộc trực tiếp vào DbContext hay EF Core giúp bảo vệ kiến trúc dự án lâu dài?",
                "Deep-Dive"
            );
        }

        // 5. SignalR / Realtime / WebSocket keywords -> Edge-Case
        if (text.Contains("signalr") || text.Contains("hub") || text.Contains("websocket") || text.Contains("realtime"))
        {
            return (
                "Làm thế nào để bạn đảm bảo các sự kiện trong phòng thi vấn đáp qua SignalR vẫn được truyền nhận chính xác nếu thí sinh bị mất kết nối mạng tạm thời?",
                "Edge-Case"
            );
        }

        // Default contextual follow-up based on question
        return (
            $"Dựa trên phần trình bày của bạn, những điểm đánh đổi hoặc trường hợp đặc biệt (edge-case) nào cần được xem xét khi triển khai thực tế trên môi trường {subjectContext}?",
            "Deep-Dive"
        );
    }
}
