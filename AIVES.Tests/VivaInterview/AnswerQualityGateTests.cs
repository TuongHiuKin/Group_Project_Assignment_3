using AIVES.BLL.Services.VivaInterview;
using Xunit;

namespace AIVES.Tests.VivaInterview;

/// <summary>
/// Cổng chất lượng câu trả lời. Các chuỗi dùng ở đây lấy nguyên văn từ bản ghi STT thật
/// trong ảnh chụp màn hình của giảng viên, để lỗi tái diễn sẽ làm test đỏ ngay.
/// </summary>
public class AnswerQualityGateTests
{
    private const string DbContextQuestion =
        "Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime " +
        "thay vì Singleton, và điều gì sẽ xảy ra với Entity Tracker và kết nối cơ sở dữ liệu " +
        "nếu một service Singleton giữ tham chiếu đến DbContext?";

    private const string DbContextQuestionEn =
        "Explain why DbContext should be registered with Scoped lifetime instead of Singleton, " +
        "and what happens to the Entity Tracker and database connections if a Singleton service " +
        "holds a reference to DbContext.";

    // ---------- Trường hợp PHẢI CHẶN: rác, không nội dung ----------

    [Theory]
    [InlineData("Alo, alo, alo, alo, alo, tôi là Kiệc, tôi là Kiệc.")]
    [InlineData("Alo alo")]
    [InlineData("Hãy subscribe cho kênh Ghiền Mì Gõ để ủng hộ mình nhé các bạn")]
    [InlineData("Tôi không biết, tôi không biết")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ừm ừm ờ ờ")]
    public void Blocks_answers_without_substance(string transcript)
    {
        var quality = AnswerQualityGate.Evaluate(transcript, DbContextQuestion, 1.0m);

        Assert.False(
            AnswerQualityGate.ShouldGenerateFollowUp(quality),
            $"Phải chặn nhưng lại cho qua: '{transcript}' -> {quality.Kind} ({quality.Reason})");
    }

    [Fact]
    public void Advert_read_into_microphone_is_blocked_even_with_perfect_confidence()
    {
        // Đây chính là ca trong ảnh: đọc quảng cáo YouTube, STT trả về confidence 1.00.
        var quality = AnswerQualityGate.Evaluate(
            "Hãy subscribe cho kênh Ghiền Mì Gõ để ủng hộ mình nhé các bạn",
            DbContextQuestion,
            1.0m);

        // Quảng cáo bị xếp vào NoContent (không có nội dung bài thi) — chặt hơn OffTopic,
        // vì nó phải bị tính là KHÔNG TRẢ LỜI chứ không phải "trả lời lạc đề".
        Assert.Equal(AnswerQualityKind.NoContent, quality.Kind);
    }

    // ---------- Trường hợp PHẢI CHO QUA: câu trả lời thật ----------

    [Fact]
    public void Accepts_correct_vietnamese_answer()
    {
        var quality = AnswerQualityGate.Evaluate(
            "Vì DbContext không thread-safe, mỗi request cần một instance riêng. " +
            "Nếu dùng Singleton thì Entity Tracker sẽ giữ toàn bộ entity đã load và " +
            "một kết nối cơ sở dữ liệu mở suốt vòng đời ứng dụng, gây rò rỉ bộ nhớ và dữ liệu cũ.",
            DbContextQuestion,
            0.92m);

        Assert.Equal(AnswerQualityKind.Accepted, quality.Kind);
        Assert.True(AnswerQualityGate.ShouldGenerateFollowUp(quality));
    }

    [Fact]
    public void Accepts_correct_english_answer()
    {
        var quality = AnswerQualityGate.Evaluate(
            "DbContext must be registered as scoped because it is not thread safe. " +
            "One DB context instance tracks every entity it has loaded, so a singleton would " +
            "keep one entity tracker and one database connection alive for the whole application " +
            "lifetime, causing memory leaks and stale cached data.",
            DbContextQuestionEn,
            0.95m);

        Assert.Equal(AnswerQualityKind.Accepted, quality.Kind);
    }

    [Fact]
    public void Accepts_short_but_on_topic_answer()
    {
        // Câu trả lời kỹ thuật đúng có thể rất ngắn — không được chặn chỉ vì ít từ.
        var quality = AnswerQualityGate.Evaluate(
            "Vì DbContext Scoped không thread-safe.",
            DbContextQuestion,
            0.9m);

        Assert.Equal(AnswerQualityKind.Accepted, quality.Kind);
    }

    // ---------- Độ tin cậy thấp: chuyển sang giảng viên chấm tay ----------

    [Fact]
    public void Very_low_confidence_blocks_followup()
    {
        // Tin cậy rất thấp = có thể STT nghe sai; không nên đẻ thêm câu hỏi xoáy dựa trên bản ghi sai.
        var quality = AnswerQualityGate.Evaluate(
            "Vì DbContext không thread-safe nên mỗi request cần một instance riêng.",
            DbContextQuestion,
            0.15m);

        Assert.False(AnswerQualityGate.ShouldGenerateFollowUp(quality));
    }

    [Fact]
    public void Real_world_session_where_student_says_i_dont_know_produces_no_followup()
    {
        // Tái hiện đúng phiên trong ảnh: "Tôi không biết" rồi AI vẫn hỏi xoáy, học viên đáp "Hello".
        var quality = AnswerQualityGate.Evaluate("Tôi không biết", DbContextQuestion, 1.0m);

        Assert.False(AnswerQualityGate.ShouldGenerateFollowUp(quality));
        Assert.Equal(AnswerQualityKind.NoContent, quality.Kind);
    }

    [Fact]
    public void Filler_phrases_are_stripped_as_whole_phrases_not_word_by_word()
    {
        // Đủ dài để qua bước kiểm tra số từ, nhưng sau khi bỏ cụm đệm "toi khong biet" thì rỗng.
        // Đây là hồi quy cho lỗi so khớp từng từ đơn với danh sách cụm nhiều từ.
        var quality = AnswerQualityGate.Evaluate(
            "Toi khong biet, toi khong biet, em khong biet",
            DbContextQuestion,
            1.0m);

        Assert.Equal(AnswerQualityKind.NoContent, quality.Kind);
        Assert.Contains("đệm", quality.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Student_who_says_i_dont_know_then_hello_gets_no_second_followup()
    {
        // Chuỗi trong ảnh 3: "Tôi không biết, tôi không biết" -> AI hỏi xoáy -> "Hello".
        // Cả hai lượt đều phải bị chặn, nên không có câu hỏi xoáy nào được sinh.
        foreach (var transcript in new[]
                 {
                     "Tôi không biết, tôi không biết",
                     "Hello",
                 })
        {
            var quality = AnswerQualityGate.Evaluate(transcript, DbContextQuestion, 1.0m);
            Assert.False(
                AnswerQualityGate.ShouldGenerateFollowUp(quality),
                $"Lượt '{transcript}' phải bị chặn nhưng vẫn sinh câu hỏi xoáy ({quality.Kind}).");
        }
    }
}
