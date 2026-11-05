using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Grading;

namespace AIVES.BLL.Services.Ai.Unavailable;

/// <summary>
/// Trả lỗi tiếng Việt rõ ràng khi một năng lực AI không resolve được service thật và
/// <c>AllowMockFallback=false</c>, thay vì âm thầm trả kết quả mock.
/// </summary>
public static class AiUnavailableMessages
{
    public const string RealAiDisabled = "chưa bật AI thật trên trang Cấu hình AI";
    public const string NoProfileSelected = "chưa chọn hồ sơ nhà cung cấp trong Định tuyến năng lực";
    public const string ProviderNotSupported = "loại nhà cung cấp của hồ sơ không được hỗ trợ";
    public const string CapabilityNotEnabled = "hồ sơ được chọn chưa bật năng lực này hoặc thiếu API key";
    public const string CreationFailed = "không khởi tạo được dịch vụ của nhà cung cấp";

    public static string Format(string capabilityName, string reason) =>
        $"{capabilityName} chưa sẵn sàng: {reason}. Vui lòng nhờ Admin kiểm tra Cấu hình AI.";
}

public sealed class UnavailableSpeechToTextService(string reason) : ISpeechToTextService
{
    public string ErrorMessage { get; } = AiUnavailableMessages.Format("Nhận diện giọng nói (STT)", reason);

    public Task<SpeechToTextResult> TranscribeAsync(Stream audioStream, string contentType, CancellationToken ct = default) =>
        Task.FromResult(new SpeechToTextResult(false, string.Empty, 0m, ErrorMessage));
}

public sealed class UnavailableTextToSpeechService(string reason) : ITextToSpeechService
{
    public string ErrorMessage { get; } = AiUnavailableMessages.Format("Đọc câu hỏi (TTS)", reason);

    public Task<TextToSpeechResult> SynthesizeAsync(string text, string voiceLocale = "vi-VN", CancellationToken ct = default) =>
        Task.FromResult(new TextToSpeechResult(false, null, null, ErrorMessage));
}

public sealed class UnavailableFollowUpGeneratorService(string reason) : IFollowUpGeneratorService
{
    public string ErrorMessage { get; } = AiUnavailableMessages.Format("Câu hỏi phụ (LLM)", reason);

    public Task<FollowUpResult> GenerateFollowUpQuestionAsync(
        string originalQuestion,
        string studentAnswerTranscript,
        string subjectContext,
        CancellationToken ct = default) =>
        Task.FromResult(new FollowUpResult(false, string.Empty, string.Empty, ErrorMessage));
}

public sealed class UnavailableAnswerGradingService(string reason) : IAnswerGradingService
{
    public string ErrorMessage { get; } = AiUnavailableMessages.Format("Chấm điểm AI (Grading)", reason);

    public Task<QuestionGradingResultDto> GradeAnswerAsync(
        GradingInputDto input, 
        CancellationToken ct = default)
    {
        return Task.FromResult(new QuestionGradingResultDto
        {
            Id = Guid.NewGuid(),
            SessionId = input?.SessionId ?? Guid.Empty,
            QuestionId = input?.QuestionId ?? string.Empty,
            QuestionTitle = input?.QuestionText ?? string.Empty,
            BloomLevel = input?.BloomLevel,
            MaxPoints = input?.MaxPoints ?? 10m,
            AiScore = 0m,
            AiFeedback = ErrorMessage,
            State = GradingState.Failed,
            AiProvider = "Unavailable"
        });
    }
}
