using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Whisper invents YouTube outro phrases ("Hãy subscribe cho kênh Ghiền Mì Gõ...", "Cảm ơn các bạn đã theo dõi")
/// when the recording is silent or only noise. Those sentences are dropped so silence is not stored as an answer.
/// </summary>
internal static class WhisperHallucinationFilter
{
    // Matched against text without diacritics, lowercase, single-spaced.
    private static readonly string[] Markers =
    {
        "subscribe cho kenh", "hay subscribe", "subscribe kenh", "dang ky kenh", "dang ky cho kenh",
        "ghien mi go", "la la school", "nhan chuong", "bam chuong", "like va share", "like share",
        "cam on cac ban da theo doi", "cam on cac ban da xem", "cam on cac ban da lang nghe",
        "hen gap lai cac ban", "hen gap lai trong", "khong bo lo nhung video", "video hap dan",
        "thanks for watching", "thank you for watching", "please subscribe"
    };

    // Standard Whisper heuristic for segments that are silence rather than speech.
    private const double NoSpeechProbThreshold = 0.6;
    private const double LowAvgLogProbThreshold = -1.0;

    private static readonly Regex SentenceSplit = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled);

    /// <summary>Returns the transcript without hallucinated segments or sentences; empty when nothing real was said.</summary>
    public static string Clean(JsonElement root, string fullText)
    {
        var text = fullText;
        if (root.TryGetProperty("segments", out var segments) && segments.ValueKind == JsonValueKind.Array &&
            segments.EnumerateArray().Any(s => s.TryGetProperty("text", out _)))
        {
            var kept = segments.EnumerateArray()
                .Where(s => !IsSilentSegment(s))
                .Select(s => s.TryGetProperty("text", out var t) ? t.GetString()?.Trim() : null)
                .Where(t => !string.IsNullOrEmpty(t));
            text = string.Join(" ", kept);
        }

        var sentences = SentenceSplit.Split(text.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s) && !IsHallucinatedSentence(s));
        return string.Join(" ", sentences).Trim();
    }

    public static bool IsHallucinatedSentence(string sentence)
    {
        var normalized = Normalize(sentence);
        return Markers.Any(m => normalized.Contains(m, StringComparison.Ordinal));
    }

    private static bool IsSilentSegment(JsonElement segment) =>
        segment.TryGetProperty("no_speech_prob", out var noSpeech) && noSpeech.TryGetDouble(out var p) && p > NoSpeechProbThreshold &&
        segment.TryGetProperty("avg_logprob", out var logProb) && logProb.TryGetDouble(out var lp) && lp < LowAvgLogProbThreshold;

    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
