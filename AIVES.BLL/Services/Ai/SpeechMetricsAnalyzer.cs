using System.Text.RegularExpressions;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Grading;

namespace AIVES.BLL.Services.Ai;

/// <summary>
/// Dịch vụ tính toán các tín hiệu phụ giọng nói (thời gian trả lời, tốc độ WPM, tỷ lệ từ đệm).
/// </summary>
public sealed class SpeechMetricsAnalyzer : ISpeechMetricsAnalyzer
{
    private const double DefaultEstimatedWpm = 130.0;

    // Danh sách cụm từ đệm nhiều từ (ưu tiên quét trước để tránh tách từ lẻ)
    private static readonly string[] MultiWordFillerPhrases =
    [
        "kiểu như là",
        "kiểu như",
        "kiểu là",
        "nói chung là",
        "thực ra thì",
        "thực ra là",
        "thì là mà",
        "thì là",
        "như là",
        "vậy đó",
        "cơ mà",
        "you know",
        "so yeah",
        "i mean",
        "as in"
    ];

    // Danh sách từ đệm đơn (tiếng Việt & tiếng Anh)
    private static readonly HashSet<string> SingleWordFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "à", "ờ", "ừ", "ừm", "ơ", "thì", "kiểu",
        "um", "uh", "er", "ah", "like", "basically", "actually", "literally"
    };

    public SpeechMetricsDto Analyze(
        string? mainTranscript, 
        double durationSeconds = 0, 
        IEnumerable<string>? followUpTurns = null)
    {
        var combinedText = $"{mainTranscript ?? string.Empty} {string.Join(" ", followUpTurns ?? Enumerable.Empty<string>())}".Trim();

        if (string.IsNullOrWhiteSpace(combinedText))
        {
            return new SpeechMetricsDto
            {
                AnswerDurationSeconds = 0,
                SpeakingSpeedWpm = 0,
                FillerWordsCount = 0,
                FillerWordsRatio = 0,
                IdentifiedFillerWords = new List<string>()
            };
        }

        var normalizedLower = combinedText.ToLowerInvariant();
        var identifiedFillers = new List<string>();
        var fillerWordCount = 0;

        // 1. Quét các cụm từ đệm nhiều từ trước
        var textForSingleWordScan = normalizedLower;
        foreach (var phrase in MultiWordFillerPhrases)
        {
            var pattern = $@"\b{Regex.Escape(phrase)}\b";
            var matches = Regex.Matches(textForSingleWordScan, pattern, RegexOptions.IgnoreCase);
            if (matches.Count > 0)
            {
                fillerWordCount += matches.Count;
                identifiedFillers.Add(phrase);
                // Xóa cụm từ đã nhận diện để không đếm trùng ở bước quét từ đơn
                textForSingleWordScan = Regex.Replace(textForSingleWordScan, pattern, " ");
            }
        }

        // 2. Tách từ và quét các từ đệm đơn
        var tokens = Regex.Matches(textForSingleWordScan, @"\b[\p{L}\p{N}']+\b")
            .Select(m => m.Value)
            .ToList();

        var totalWords = Regex.Matches(normalizedLower, @"\b[\p{L}\p{N}']+\b").Count;

        foreach (var token in tokens)
        {
            if (SingleWordFillers.Contains(token))
            {
                fillerWordCount++;
                if (!identifiedFillers.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    identifiedFillers.Add(token);
                }
            }
        }

        // 3. Tính thời lượng trả lời (Duration)
        double effectiveDuration;
        if (durationSeconds > 0)
        {
            effectiveDuration = Math.Round(durationSeconds, 1);
        }
        else
        {
            // Ước tính thời lượng nếu không có thời lượng audio thực tế
            effectiveDuration = Math.Round((totalWords / DefaultEstimatedWpm) * 60.0, 1);
            if (effectiveDuration < 1.0 && totalWords > 0)
            {
                effectiveDuration = 1.0;
            }
        }

        // 4. Tính tốc độ nói (Speaking Speed - WPM)
        double wpm = 0.0;
        if (effectiveDuration > 0 && totalWords > 0)
        {
            var minutes = effectiveDuration / 60.0;
            wpm = Math.Round(totalWords / minutes, 1);
            wpm = Math.Clamp(wpm, 0.0, 450.0);
        }

        // 5. Tính tỷ lệ từ đệm (Filler Words Ratio)
        double ratio = 0.0;
        if (totalWords > 0)
        {
            ratio = Math.Round((double)fillerWordCount / totalWords, 4);
            ratio = Math.Clamp(ratio, 0.0, 1.0);
        }

        return new SpeechMetricsDto
        {
            AnswerDurationSeconds = effectiveDuration,
            SpeakingSpeedWpm = wpm,
            FillerWordsCount = fillerWordCount,
            FillerWordsRatio = ratio,
            IdentifiedFillerWords = identifiedFillers
        };
    }
}

