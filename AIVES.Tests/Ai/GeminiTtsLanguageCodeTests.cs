using System.Reflection;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Services.Ai.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AIVES.Tests.Ai;

/// <summary>
/// Chọn <c>languageCode</c> cho Gemini TTS.
///
/// Gemini TTS tự nhận diện ngôn ngữ khi trường này vắng mặt, và đó là chế độ DUY NHẤT xử lý đúng
/// câu trộn Việt + Anh. Câu hỏi vấn đáp của AIVES luôn chứa thuật ngữ tiếng Anh ("DbContext",
/// "Scoped lifetime", "Entity Tracker"), nên ép cứng "vi-VN" khiến model đọc từ tiếng Anh theo
/// quy tắc tiếng Việt — đúng lỗi người dùng báo: "khi tới từ tiếng Anh thì không đọc được".
/// </summary>
public class GeminiTtsLanguageCodeTests
{
    private static string? Resolve(string text, string locale = "vi-VN")
    {
        var method = typeof(GeminiTextToSpeechService)
            .GetMethod("ResolveLanguageCode", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [text, locale]);
    }

    [Fact]
    public void Mixed_vietnamese_and_english_returns_null_so_model_auto_detects()
    {
        const string question =
            "Hãy giải thích vì sao DbContext trong ASP.NET Core phải được đăng ký với Scoped lifetime " +
            "thay vì Singleton, và điều gì xảy ra với Entity Tracker?";

        Assert.Null(Resolve(question));
    }

    [Theory]
    [InlineData("Giải thích vòng đời Scoped của DbContext.")]
    [InlineData("Phân biệt Transient, Scoped và Singleton trong Dependency Injection.")]
    [InlineData("Tại sao SignalR Hub không được inject DbContext trực tiếp?")]
    [InlineData("Trình bày kiến trúc 3 lớp: Presentation, Business Logic, Data Access.")]
    public void Vietnamese_questions_with_technical_terms_return_null(string question)
    {
        // Đây chính là các câu hỏi thật của EXAM01 — tất cả đều trộn thuật ngữ tiếng Anh.
        Assert.Null(Resolve(question));
    }

    [Fact]
    public void Pure_vietnamese_keeps_the_locale()
    {
        const string text = "Hôm nay trời đẹp và chúng ta sẽ bắt đầu buổi thi vấn đáp.";
        Assert.Equal("vi-VN", Resolve(text));
    }

    [Fact]
    public void Pure_english_keeps_the_requested_locale_for_stability()
    {
        // Không có dấu tiếng Việt và không trộn -> gửi locale để giọng đọc ổn định.
        const string text = "Explain the scoped lifetime of the database context in this framework.";
        Assert.Equal("vi-VN", Resolve(text));
    }

    [Fact]
    public void Empty_or_whitespace_returns_null()
    {
        Assert.Null(Resolve(""));
        Assert.Null(Resolve("   "));
        Assert.Null(Resolve("Hãy giải thích DbContext", locale: ""));
    }

    [Fact]
    public void Short_accented_vietnamese_without_long_ascii_words_keeps_locale()
    {
        // "và", "có" ngắn; không có thuật ngữ tiếng Anh nào >= 4 ký tự ASCII.
        Assert.Equal("vi-VN", Resolve("Thế nào là đúng và đủ?"));
    }

    [Fact]
    public void English_terms_shorter_than_four_chars_do_not_count_as_mixed()
    {
        // "API" chỉ 3 ký tự -> không coi là trộn ngôn ngữ, giữ locale.
        Assert.Equal("vi-VN", Resolve("Hãy mô tả API này giúp tôi."));
    }
}
