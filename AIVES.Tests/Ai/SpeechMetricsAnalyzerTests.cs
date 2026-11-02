using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Mocks;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class SpeechMetricsAnalyzerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Analyze_ReturnsZeroMetrics_WhenTranscriptIsEmpty(string? transcript)
    {
        var analyzer = new SpeechMetricsAnalyzer();
        var metrics = analyzer.Analyze(transcript, 0);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.AnswerDurationSeconds);
        Assert.Equal(0, metrics.SpeakingSpeedWpm);
        Assert.Equal(0, metrics.FillerWordsCount);
        Assert.Equal(0, metrics.FillerWordsRatio);
        Assert.Empty(metrics.IdentifiedFillerWords);
    }

    [Fact]
    public void Analyze_CalculatesAccurateWpm_WhenDurationProvided()
    {
        var analyzer = new SpeechMetricsAnalyzer();
        // 60 từ nói trong 30 giây -> 120 WPM
        var words = string.Join(" ", Enumerable.Repeat("từ", 60));
        var metrics = analyzer.Analyze(words, 30.0);

        Assert.Equal(30.0, metrics.AnswerDurationSeconds);
        Assert.Equal(120.0, metrics.SpeakingSpeedWpm);
    }

    [Fact]
    public void Analyze_EstimatesDuration_WhenDurationNotProvided()
    {
        var analyzer = new SpeechMetricsAnalyzer();
        // 130 từ với tốc độ chuẩn 130 WPM -> ~60 giây
        var words = string.Join(" ", Enumerable.Repeat("từ", 130));
        var metrics = analyzer.Analyze(words, 0);

        Assert.Equal(60.0, metrics.AnswerDurationSeconds);
        Assert.Equal(130.0, metrics.SpeakingSpeedWpm);
    }

    [Fact]
    public void Analyze_IdentifiesVietnameseSingleWordFillers()
    {
        var analyzer = new SpeechMetricsAnalyzer();
        var transcript = "Dạ thưa thầy câu này ờ em nghĩ là à kiến trúc 3 lớp gồm có Presentation và ừm BLL ạ.";

        var metrics = analyzer.Analyze(transcript, 10.0);

        Assert.True(metrics.FillerWordsCount >= 3);
        Assert.Contains("ờ", metrics.IdentifiedFillerWords);
        Assert.Contains("à", metrics.IdentifiedFillerWords);
        Assert.Contains("ừm", metrics.IdentifiedFillerWords);
        Assert.True(metrics.FillerWordsRatio > 0.0);
    }

    [Fact]
    public void Analyze_IdentifiesVietnameseMultiWordFillerPhrases()
    {
        var analyzer = new SpeechMetricsAnalyzer();
        var transcript = "Kiểu như là khi nhận request thì nói chung là BLL sẽ xử lý, thực ra thì DAL chỉ truy vấn database.";

        var metrics = analyzer.Analyze(transcript, 12.0);

        Assert.True(metrics.FillerWordsCount >= 3);
        Assert.Contains("kiểu như là", metrics.IdentifiedFillerWords);
        Assert.Contains("nói chung là", metrics.IdentifiedFillerWords);
        Assert.Contains("thực ra thì", metrics.IdentifiedFillerWords);
    }

    [Fact]
    public void Analyze_IdentifiesEnglishFillers()
    {
        var analyzer = new SpeechMetricsAnalyzer();
        var transcript = "Um you know the Presentation layer basically calls the BLL service and like handles the HTTP request.";

        var metrics = analyzer.Analyze(transcript, 8.0);

        Assert.True(metrics.FillerWordsCount >= 4);
        Assert.Contains("um", metrics.IdentifiedFillerWords);
        Assert.Contains("you know", metrics.IdentifiedFillerWords);
        Assert.Contains("basically", metrics.IdentifiedFillerWords);
        Assert.Contains("like", metrics.IdentifiedFillerWords);
    }

    [Fact]
    public void Analyze_HandlesFollowUpTurns_Combined()
    {
        var analyzer = new SpeechMetricsAnalyzer();
        var main = "Em xin trả lời câu 1 về 3 lớp.";
        var followUps = new[] { "Dạ thì là BLL tách biệt với DAL ạ.", "Nói chung là vậy đó." };

        var metrics = analyzer.Analyze(main, 15.0, followUps);

        Assert.True(metrics.FillerWordsCount >= 2);
        Assert.Contains("thì là", metrics.IdentifiedFillerWords);
        Assert.Contains("nói chung là", metrics.IdentifiedFillerWords);
    }

    [Fact]
    public async Task MockAnswerGradingService_PopulatesSpeechMetrics_Automatically()
    {
        var mock = new MockAnswerGradingService();
        var input = new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "EXAM01-Q01",
            QuestionText = "3 lớp là gì?",
            StudentAnswerTranscript = "Dạ ờ kiến trúc 3 lớp gồm Presentation BLL DAL kiểu như là vậy.",
            AnswerDurationSeconds = 12.0
        };

        var result = await mock.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.NotNull(result.SpeechMetrics);
        Assert.Equal(12.0, result.SpeechMetrics.AnswerDurationSeconds);
        Assert.True(result.SpeechMetrics.FillerWordsCount > 0);
        Assert.True(result.SpeechMetrics.SpeakingSpeedWpm > 0);
    }

    [Fact]
    public void GradingOutputValidator_AttachesSpeechMetrics_WhenMissingInRawResult()
    {
        var validator = new GradingOutputValidator();
        var input = new GradingInputDto
        {
            SessionId = Guid.NewGuid(),
            QuestionId = "EXAM01-Q01",
            QuestionText = "Câu hỏi",
            StudentAnswerTranscript = "Kiến trúc 3 tầng gồm có Presentation, BLL, DAL.",
            AnswerDurationSeconds = 10.0
        };

        var raw = new QuestionGradingResultDto
        {
            SessionId = input.SessionId,
            QuestionId = input.QuestionId,
            AiScore = 10m,
            SpeechMetrics = null // Missing in raw
        };

        var validated = validator.Validate(raw, input);

        Assert.NotNull(validated.SpeechMetrics);
        Assert.Equal(10.0, validated.SpeechMetrics.AnswerDurationSeconds);
        Assert.True(validated.SpeechMetrics.SpeakingSpeedWpm > 0);
    }
}

