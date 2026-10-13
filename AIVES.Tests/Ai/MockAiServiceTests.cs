using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AIVES.BLL;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai.Mocks;

namespace AIVES.Tests.Ai;

public class MockAiServiceTests
{
    #region MockSpeechToTextService Tests

    [Fact]
    public async Task MockStt_Returns_Default_Transcript_On_Valid_Stream()
    {
        var service = new MockSpeechToTextService();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 });

        var result = await service.TranscribeAsync(stream, "audio/webm");

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.TranscriptText);
        Assert.True(result.ConfidenceScore > 0.9m);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task MockStt_Returns_Failure_When_Stream_Is_Null()
    {
        var service = new MockSpeechToTextService();

        var result = await service.TranscribeAsync(null!, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Equal(string.Empty, result.TranscriptText);
        Assert.Contains("null", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockStt_Returns_Simulated_Failure_When_Configured()
    {
        var service = new MockSpeechToTextService
        {
            ShouldSucceed = false,
            SimulatedErrorMessage = "Simulated microphone distortion"
        };
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });

        var result = await service.TranscribeAsync(stream, "audio/wav");

        Assert.False(result.IsSuccess);
        Assert.Equal("Simulated microphone distortion", result.ErrorMessage);
    }

    [Fact]
    public async Task MockStt_Respects_CancellationToken()
    {
        var service = new MockSpeechToTextService();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TranscribeAsync(stream, "audio/webm", cts.Token));
    }

    #endregion

    #region MockTextToSpeechService Tests

    [Fact]
    public async Task MockTts_Returns_Audio_Url_And_Bytes_On_Valid_Text()
    {
        var service = new MockTextToSpeechService();

        var result = await service.SynthesizeAsync("What is DbContext lifecycle in ASP.NET Core?", "vi-VN");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.AudioUrl);
        Assert.StartsWith("/audio/", result.AudioUrl);
        Assert.NotNull(result.AudioData);
        Assert.NotEmpty(result.AudioData);
        Assert.Null(result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task MockTts_Fails_When_Text_Is_Empty(string? emptyText)
    {
        var service = new MockTextToSpeechService();

        var result = await service.SynthesizeAsync(emptyText!);

        Assert.False(result.IsSuccess);
        Assert.Null(result.AudioUrl);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockTts_Returns_Simulated_Failure_When_Configured()
    {
        var service = new MockTextToSpeechService
        {
            ShouldSucceed = false,
            SimulatedErrorMessage = "TTS quota exceeded"
        };

        var result = await service.SynthesizeAsync("Test text");

        Assert.False(result.IsSuccess);
        Assert.Equal("TTS quota exceeded", result.ErrorMessage);
    }

    [Fact]
    public async Task MockTts_Respects_CancellationToken()
    {
        var service = new MockTextToSpeechService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SynthesizeAsync("Test", "vi-VN", cts.Token));
    }

    #endregion

    #region MockFollowUpGeneratorService Tests

    [Fact]
    public async Task MockFollowUp_Returns_Clarification_When_Answer_Is_Short()
    {
        var service = new MockFollowUpGeneratorService();

        var result = await service.GenerateFollowUpQuestionAsync(
            originalQuestion: "Explain Dependency Injection in .NET.",
            studentAnswerTranscript: "I don't know.",
            subjectContext: "PRN222"
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("Clarification", result.CognitiveIntent);
        Assert.NotEmpty(result.FollowUpQuestionText);
    }

    [Fact]
    public async Task MockFollowUp_Returns_DeepDive_When_Answer_Mentions_DbContext()
    {
        var service = new MockFollowUpGeneratorService();

        var result = await service.GenerateFollowUpQuestionAsync(
            originalQuestion: "Explain service lifetimes in ASP.NET Core.",
            studentAnswerTranscript: "DbContext has a Scoped lifetime and is created per request.",
            subjectContext: "PRN222"
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("Deep-Dive", result.CognitiveIntent);
        Assert.Contains("DbContext", result.FollowUpQuestionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockFollowUp_Returns_EdgeCase_When_Answer_Mentions_Concurrency()
    {
        var service = new MockFollowUpGeneratorService();

        var result = await service.GenerateFollowUpQuestionAsync(
            originalQuestion: "How do you handle background operations?",
            studentAnswerTranscript: "We use async await to prevent thread blocking and concurrency issues.",
            subjectContext: "PRN222"
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("Edge-Case", result.CognitiveIntent);
        Assert.Contains("exception", result.FollowUpQuestionText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockFollowUp_Uses_Override_When_Provided()
    {
        var service = new MockFollowUpGeneratorService
        {
            OverrideFollowUpQuestion = "Custom follow up?",
            OverrideCognitiveIntent = "Counter-Example"
        };

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "Context");

        Assert.True(result.IsSuccess);
        Assert.Equal("Custom follow up?", result.FollowUpQuestionText);
        Assert.Equal("Counter-Example", result.CognitiveIntent);
    }

    [Fact]
    public async Task MockFollowUp_Fails_When_Original_Question_Is_Empty()
    {
        var service = new MockFollowUpGeneratorService();

        var result = await service.GenerateFollowUpQuestionAsync(
            originalQuestion: "   ",
            studentAnswerTranscript: "Some answer",
            subjectContext: "Context"
        );

        Assert.False(result.IsSuccess);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MockFollowUp_Respects_CancellationToken()
    {
        var service = new MockFollowUpGeneratorService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateFollowUpQuestionAsync("Q1", "Answer", "Context", cts.Token));
    }

    #endregion

    #region Options & DI Registration Tests

    [Fact]
    public void AiServiceOptions_Has_Sensible_Defaults()
    {
        var options = new AiServiceOptions();

        Assert.True(options.UseMock);
        Assert.Equal(15, options.SpeechToTextTimeoutSeconds);
        Assert.Equal(10, options.TextToSpeechTimeoutSeconds);
        Assert.Equal(15, options.FollowUpTimeoutSeconds);
        Assert.Equal("gpt-4o-mini", options.OpenAI.Model);
        Assert.Equal("southeastasia", options.AzureSpeech.Region);
    }

    [Fact]
    public void AddMockAiServices_Registers_All_Three_Interfaces()
    {
        var services = new ServiceCollection();
        services.AddMockAiServices();

        var provider = services.BuildServiceProvider();

        var stt = provider.GetService<ISpeechToTextService>();
        var tts = provider.GetService<ITextToSpeechService>();
        var followUp = provider.GetService<IFollowUpGeneratorService>();

        Assert.NotNull(stt);
        Assert.IsType<MockSpeechToTextService>(stt);

        Assert.NotNull(tts);
        Assert.IsType<MockTextToSpeechService>(tts);

        Assert.NotNull(followUp);
        Assert.IsType<MockFollowUpGeneratorService>(followUp);
    }

    #endregion
}
