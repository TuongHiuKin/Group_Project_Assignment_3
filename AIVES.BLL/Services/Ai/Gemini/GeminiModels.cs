using AIVES.BLL.Models.Ai;

namespace AIVES.BLL.Services.Ai.Gemini;

/// <summary>
/// Tên model Gemini mặc định (một nguồn duy nhất cho adapter, credential store, service và trang cài đặt)
/// cùng các quy tắc chọn model theo năng lực.
/// </summary>
public static class GeminiModels
{
    public const string DefaultSpeechToText = "gemini-3.8-flash";
    public const string DefaultTextToSpeech = "gemini-3.8-flash-tts";
    public const string DefaultFollowUp = "gemini-3.8-flash";
    public const string DefaultVoice = "Puck";

    /// <summary>Danh sách dự phòng khi không gọi được ListModels (không dùng để kiểm tra hợp lệ).</summary>
    public static IReadOnlyList<string> KnownModels { get; } = new[]
    {
        DefaultSpeechToText, DefaultTextToSpeech, "gemini-3.8-flash-lite-tts", "gemini-3.5-flash", "gemini-3.5-flash-lite"
    };

    public static string DefaultFor(AiCapability capability) => capability switch
    {
        AiCapability.TextToSpeech => DefaultTextToSpeech,
        AiCapability.FollowUpLlm => DefaultFollowUp,
        _ => DefaultSpeechToText
    };

    public static bool IsTtsModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Contains("tts", StringComparison.OrdinalIgnoreCase);

    /// <summary>TTS cần model có "tts" trong tên; STT và LLM cần model không phải TTS.</summary>
    public static bool IsSuitableFor(AiCapability capability, string? model) =>
        !string.IsNullOrWhiteSpace(model) && (capability == AiCapability.TextToSpeech ? IsTtsModel(model) : !IsTtsModel(model));

    /// <summary>
    /// Gợi ý model hợp lệ gần nhất trong <paramref name="available"/> cho năng lực (ưu tiên trùng tiền tố dài nhất,
    /// rồi tới model mặc định). Trả về null nếu không có model nào phù hợp.
    /// </summary>
    public static string? SuggestModel(AiCapability capability, string? requested, IEnumerable<string> available)
    {
        var candidates = available
            .Where(m => !string.IsNullOrWhiteSpace(m) && m.StartsWith("gemini", StringComparison.OrdinalIgnoreCase) && IsSuitableFor(capability, m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var target = requested?.Trim() ?? string.Empty;
        var preferred = DefaultFor(capability);
        return candidates
            .OrderByDescending(m => CommonPrefixLength(m, target))
            .ThenByDescending(m => string.Equals(m, preferred, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Length)
            .First();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
        {
            i++;
        }
        return i;
    }
}
