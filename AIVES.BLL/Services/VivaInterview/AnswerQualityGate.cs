using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AIVES.BLL.Models.VivaInterview;

namespace AIVES.BLL.Services.VivaInterview;

/// <summary>
/// Quyết định một câu trả lời có đủ nội dung để đáng được hỏi xoáy hay không.
///
/// Trước đây orchestrator luôn sinh câu hỏi xoáy sau lượt trả lời đầu tiên, bất kể sinh viên nói gì.
/// Hệ quả: sinh viên trả lời "tôi không biết", "alo alo", hoặc đọc quảng cáo YouTube vẫn bị hỏi xoáy,
/// và câu hỏi xoáy đó lại được tính vào điểm rubric — vừa vô nghĩa vừa làm loãng bằng chứng học vụ.
///
/// Cổng này chấm chất lượng câu trả lời NGAY tại lượt đầu tiên:
///   Accepted   — có nội dung, đúng chủ đề  → sinh câu hỏi xoáy như bình thường
///   OffTopic   — có nói nhưng không liên quan → không hỏi xoáy, chuyển câu tiếp
///   NoContent  — im lặng / câu đệm / quảng cáo → coi như không trả lời (0 điểm), chuyển câu tiếp
///
/// Đây là heuristic từ vựng, không phải mô hình AI: mục tiêu là chặn rác rõ ràng, không phán đoán
/// đúng sai chuyên môn (việc đó thuộc về GradingOrchestrator).
/// </summary>
public static class AnswerQualityGate
{
    /// <summary>
    /// Số từ tối thiểu để coi là có trả lời. Đặt thấp vì câu trả lời kỹ thuật đúng có thể rất ngắn
    /// ("Mỗi request một DbContext" = 4 từ); việc chặn rác dựa vào kiểm tra từ đệm và độ trùng chủ đề,
    /// không dựa vào độ dài.
    /// </summary>
    public const int MinMeaningfulWords = 4;

    /// <summary>Số từ có nội dung tối thiểu sau khi bỏ các cụm đệm.</summary>
    public const int MinSubstantiveWords = 3;

    /// <summary>Tỷ lệ từ khoá của câu hỏi phải xuất hiện trong câu trả lời để coi là đúng chủ đề.</summary>
    public const double MinTopicOverlap = 0.12;

    private static readonly Regex WordSplit = new(@"[\s,.;:!?()\[\]""'“”‘’\-–—/\\|]+", RegexOptions.Compiled);

    /// <summary>
    /// Câu đệm / xác nhận / từ chối — không mang nội dung chuyên môn nào.
    /// So khớp trên chuỗi đã bỏ dấu, chữ thường.
    /// </summary>
    private static readonly string[] FillerPhrases =
    [
        "toi khong biet", "khong biet", "em khong biet", "minh khong biet",
        "toi khong ro", "khong ro", "khong nho", "em khong nho",
        "alo", "a lo", "hello", "xin chao", "chao thay", "chao co",
        "ok", "oke", "okay", "vang", "da vang", "u", "uh", "um", "hmm",
        "toi la", "em la", "minh la", "ten toi la", "ten em la",
        "test", "test test", "1 2 3", "mot hai ba",
        "khong co gi", "chua nghi ra", "de em nghi da", "cho em chut",
        "toi khong hieu", "khong hieu", "em khong hieu",
    ];

    /// <summary>Dấu hiệu nội dung không phải bài thi (quảng cáo, outro YouTube, câu đùa).</summary>
    private static readonly string[] OffTaskMarkers =
    [
        "subscribe", "dang ky kenh", "ghien mi go", "like va share", "like share",
        "nhan chuong", "bam chuong", "video hap dan", "khong bo lo",
        "cam on cac ban da theo doi", "cam on cac ban da xem", "hen gap lai",
        "thanks for watching", "thank you for watching", "please subscribe",
        "xem them tai", "link o duoi", "duoi phan mo ta",
    ];

    /// <summary>Từ dừng tiếng Việt/Anh — bỏ khỏi tập từ khoá của câu hỏi khi tính độ trùng khớp.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "la", "va", "cua", "co", "cho", "khi", "thi", "ma", "de", "trong", "mot",
        "nhung", "cac", "voi", "hay", "nhu", "the", "nao", "gi", "sao", "vi",
        "ra", "vao", "len", "xuong", "duoc", "bi", "se", "da", "dang", "khong",
        "toi", "em", "ban", "minh", "no", "do", "day", "kia", "nay",
        "the", "and", "or", "of", "to", "in", "on", "for", "with", "is", "are",
        "what", "why", "how", "would", "should", "if", "a", "an", "it", "this", "that",
    };

    /// <summary>Chấm chất lượng một câu trả lời (đã qua STT).</summary>
    public static AnswerQuality Evaluate(string? transcript, string? questionContent, decimal? confidence = null)
    {
        var raw = (transcript ?? string.Empty).Trim();
        var normalized = Normalize(raw);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return AnswerQuality.NoContent("Không có nội dung (bản ghi rỗng).");
        }

        var words = WordSplit.Split(normalized)
            .Where(w => w.Length > 0)
            .ToList();

        // 1. Bắt buộc có đủ số từ. "Alo alo alo" hay "tôi không biết" không thể là câu trả lời.
        if (words.Count < MinMeaningfulWords)
        {
            return AnswerQuality.NoContent($"Câu trả lời quá ngắn ({words.Count} từ, cần tối thiểu {MinMeaningfulWords}).");
        }

        // 2. Rác rõ ràng: quảng cáo / outro / câu đùa ngoài lề bài thi.
        var offTaskHit = OffTaskMarkers.FirstOrDefault(m => normalized.Contains(m, StringComparison.Ordinal));
        if (offTaskHit is not null)
        {
            return AnswerQuality.NoContent($"Phát hiện nội dung ngoài bài thi (\"{offTaskHit}\").");
        }

        // 3. Toàn câu đệm: bỏ hết các CỤM đệm rồi xem còn bao nhiêu từ thực chất.
        //    Lưu ý: FillerPhrases chứa cụm nhiều từ ("toi khong biet"), nên phải so khớp trên
        //    chuỗi đã chuẩn hoá chứ không phải từng từ đơn — nếu không "tôi không biết" sẽ lọt qua.
        var residual = normalized;
        foreach (var phrase in FillerPhrases.OrderByDescending(p => p.Length))
        {
            residual = residual.Replace(phrase, " ", StringComparison.Ordinal);
        }

        var substantive = WordSplit.Split(residual)
            .Where(w => w.Length > 0)
            .ToList();

        if (substantive.Count < MinSubstantiveWords)
        {
            return AnswerQuality.NoContent(
                $"Câu trả lời chỉ gồm câu đệm ({substantive.Count} từ có nội dung).");
        }

        // 4. Đúng trọng tâm: câu trả lời phải chạm vào từ khoá của câu hỏi.
        //    Nếu câu hỏi và câu trả lời khác ngôn ngữ (đề tiếng Anh, trả lời tiếng Việt) thì không thể
        //    so khớp từ vựng — khi đó chỉ cần đủ nội dung, không kết luận lạc đề.
        var questionKeywords = ExtractKeywords(questionContent);
        if (questionKeywords.Count > 0)
        {
            var answered = questionKeywords.Count(k => substantive.Any(w => w.Contains(k, StringComparison.Ordinal) || k.Contains(w, StringComparison.Ordinal)));
            var overlap = (double)answered / questionKeywords.Count;

            // Nếu câu hỏi và câu trả lời khác ngôn ngữ thì không thể kết luận lạc đề bằng cách đếm
            // từ khoá: bản dịch tiếng Anh của một câu trả lời đúng vẫn chỉ khớp được vài thuật ngữ
            // ("dbcontext"), đủ để answered > 0 nhưng không đủ ngưỡng trùng khớp. Phải kiểm tra
            // ngôn ngữ TRƯỚC khi so tỷ lệ, không chỉ khi answered == 0.
            if (overlap < MinTopicOverlap && LooksLikeDifferentLanguage(questionContent, raw))
            {
                // Không kết luận lạc đề khi hai bên khác ngôn ngữ: nội dung đã đủ dài và không phải câu đệm.
                return AnswerQuality.Accepted(
                    "Không so khớp được từ khoá do câu hỏi và câu trả lời khác ngôn ngữ; chấp nhận để AI chấm.");
            }

            if (answered == 0 || overlap < MinTopicOverlap)
            {
                return AnswerQuality.OffTopic(
                    $"Không chạm từ khoá nào của câu hỏi (trùng khớp {overlap:P0}, cần ≥ {MinTopicOverlap:P0}).");
            }
        }

        // 5. Độ tin cậy STT chỉ dùng để cảnh báo, không dùng để chặn — transcript vẫn đủ nội dung.
        var note = confidence is { } c && c < 0.55m
            ? $"Độ tin cậy STT thấp ({c:P0}) — nội dung có thể sai lệch."
            : null;

        return AnswerQuality.Accepted(note);
    }

    /// <summary>Rút từ khoá chuyên môn từ nội dung câu hỏi (bỏ từ dừng, giữ thuật ngữ).</summary>
    public static List<string> ExtractKeywords(string? questionContent)
    {
        var normalized = Normalize(questionContent);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return WordSplit.Split(normalized)
            .Where(w => w.Length >= 3)
            .Where(w => !StopWords.Contains(w))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsFillerWord(string word) => FillerPhrases.Contains(word, StringComparer.Ordinal);

    /// <summary>
    /// Ước lượng câu hỏi và câu trả lời có dùng ngôn ngữ khác nhau không, dựa trên sự có mặt
    /// của ký tự có dấu tiếng Việt. Chỉ dùng để MIỄN so khớp từ khoá, không dùng để đánh giá đúng sai.
    /// </summary>
    private static bool LooksLikeDifferentLanguage(string? question, string answer)
    {
        var questionIsVietnamese = HasVietnameseDiacritics(question);
        var answerIsVietnamese = HasVietnameseDiacritics(answer);
        return questionIsVietnamese != answerIsVietnamese;
    }

    private static bool HasVietnameseDiacritics(string? value) =>
        !string.IsNullOrEmpty(value) && value.Any(ch => ch > 127);

    /// <summary>
    /// Bỏ dấu tiếng Việt, chữ thường, gộp khoảng trắng. Dấu câu được đổi thành khoảng trắng để
    /// cụm như "Alo, alo" khớp được với mẫu "alo".
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var lowered = value.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) || ch == ' ' ? ch : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Có nên sinh câu hỏi xoáy cho lượt này không.</summary>
    public static bool ShouldGenerateFollowUp(AnswerQuality quality) => quality.IsAccepted;
}

/// <summary>Kết quả chấm chất lượng một câu trả lời.</summary>
public sealed record AnswerQuality(AnswerQualityKind Kind, string Reason)
{
    public bool IsAccepted => Kind == AnswerQualityKind.Accepted;
    public bool HasContent => Kind != AnswerQualityKind.NoContent;

    public static AnswerQuality Accepted(string? note = null) =>
        new(AnswerQualityKind.Accepted, note ?? "Câu trả lời có nội dung và đúng chủ đề.");

    public static AnswerQuality OffTopic(string reason) =>
        new(AnswerQualityKind.OffTopic, reason);

    public static AnswerQuality NoContent(string reason) =>
        new(AnswerQualityKind.NoContent, reason);
}

public enum AnswerQualityKind
{
    /// <summary>Có nội dung, đúng chủ đề → hỏi xoáy bình thường.</summary>
    Accepted = 0,

    /// <summary>Có nói nhưng lạc đề → không hỏi xoáy, chuyển câu tiếp.</summary>
    OffTopic = 1,

    /// <summary>Im lặng / câu đệm / quảng cáo → tính như không trả lời.</summary>
    NoContent = 2,
}
