using AIVES.BLL.Services.VivaInterview;
using Xunit;

namespace AIVES.Tests.VivaInterview;

/// <summary>
/// Tái hiện đúng ca trong ảnh: câu trả lời chính là "Alo, alo, 123-467-89" vẫn lọt cổng
/// chất lượng và sinh ra câu hỏi xoáy. Đây là lỗi thật, không phải kỳ vọng sai của test.
/// </summary>
public class AnswerQualityGatePhoneNumberTests
{
    private const string Question =
        "Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime " +
        "thay vì Singleton, và điều gì sẽ xảy ra với Entity Tracker và kết nối database nếu một " +
        "service Singleton giữ tham chiếu đến DbContext?";

    [Theory]
    [InlineData("Alo, alo, 123-467-89")]
    [InlineData("Alo alo 123 467 89")]
    [InlineData("alo alo 0909123456")]
    public void Phone_number_and_filler_must_not_be_accepted(string transcript)
    {
        var quality = AnswerQualityGate.Evaluate(transcript, Question);

        Assert.NotEqual(AnswerQualityKind.Accepted, quality.Kind);
    }

    [Fact]
    public void Allocation_of_three_numbers_is_not_substantive_content()
    {
        // "alo" là câu đệm; bỏ đi còn "123 467 89" = 3 từ, vừa đúng ngưỡng MinSubstantiveWords
        // nên lọt qua bước 3. Bước 4 (trùng từ khoá) phải chặn lại, nhưng lại bị nhánh
        // "khác ngôn ngữ" miễn trừ vì câu trả lời không có dấu tiếng Việt.
        var quality = AnswerQualityGate.Evaluate("Alo, alo, 123-467-89", Question);

        Assert.NotEqual(AnswerQualityKind.Accepted, quality.Kind);
    }

    [Fact]
    public void Real_answer_without_diacritics_still_passes()
    {
        // Câu trả lời đúng nhưng gõ không dấu vẫn phải được chấp nhận — không được siết
        // đến mức chặn nhầm người trả lời đúng.
        const string answer =
            "DbContext phai la Scoped vi moi HTTP request can mot instance rieng, " +
            "neu dung Singleton thi ChangeTracker tich luy entity gay ro ri bo nho.";

        var quality = AnswerQualityGate.Evaluate(answer, Question);

        Assert.Equal(AnswerQualityKind.Accepted, quality.Kind);
    }

    [Fact]
    public void Digits_only_answer_is_rejected()
    {
        var quality = AnswerQualityGate.Evaluate("123 467 89 555 777", Question);

        Assert.NotEqual(AnswerQualityKind.Accepted, quality.Kind);
    }
}
