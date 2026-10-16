using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;

namespace AIVES.Tests.Ai;

public class AiServiceTests
{
    private const string SecretApiKey = "sk-test-SECRET-4f9a";

    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            if (!string.IsNullOrEmpty(msg)) Messages.Add(msg);
            if (exception != null) Messages.Add(exception.ToString());
        }
    }

    private static IOptions<AiServiceOptions> CreateOptions(string apiKey = SecretApiKey)
    {
        return Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            SpeechToTextTimeoutSeconds = 5,
            TextToSpeechTimeoutSeconds = 5,
            FollowUpTimeoutSeconds = 5,
            OpenAI = new OpenAiOptions
            {
                ApiKey = apiKey,
                WhisperModel = "whisper-1",
                TtsModel = "tts-1",
                Model = "gpt-4o-mini"
            }
        });
    }

    [Fact]
    public async Task Whisper_Returns_Failure_When_Network_Is_Down()
    {
        var handler = new DelegatingHandlerStub((req, ct) => throw new HttpRequestException("network down"));
        using var client = new HttpClient(handler);
        var logger = new ListLogger<WhisperSpeechToTextService>();
        var service = new WhisperSpeechToTextService(client, CreateOptions(), logger);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var result = await service.TranscribeAsync(stream, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Contains("Network error", result.ErrorMessage);
    }

    [Fact]
    public async Task Tts_Returns_Failure_When_Network_Is_Down()
    {
        var handler = new DelegatingHandlerStub((req, ct) => throw new HttpRequestException("network down"));
        using var client = new HttpClient(handler);
        var logger = new ListLogger<OpenAiTextToSpeechService>();
        var service = new OpenAiTextToSpeechService(client, CreateOptions(), logger);

        var result = await service.SynthesizeAsync("Test sentence");

        Assert.False(result.IsSuccess);
        Assert.Null(result.AudioUrl);
        Assert.Contains("Network error", result.ErrorMessage);
    }

    [Fact]
    public async Task FollowUp_Returns_Failure_When_Network_Is_Down()
    {
        var handler = new DelegatingHandlerStub((req, ct) => throw new HttpRequestException("network down"));
        using var client = new HttpClient(handler);
        var logger = new ListLogger<OpenAiFollowUpGeneratorService>();
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions(), logger);

        var result = await service.GenerateFollowUpQuestionAsync("Original Q", "Student answer", "EXAM01");

        Assert.False(result.IsSuccess);
        Assert.Contains("Network error", result.ErrorMessage);
    }

    [Theory]
    [InlineData("500_Error")]
    [InlineData("429_RateLimit")]
    [InlineData("Network_Down")]
    public async Task Logs_Never_Contain_Api_Key_In_Whisper(string scenario)
    {
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            if (scenario == "Network_Down") throw new HttpRequestException("connection dropped");
            var status = scenario == "429_RateLimit" ? (HttpStatusCode)429 : HttpStatusCode.InternalServerError;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\": {\"message\": \"API error detail\"}}", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var logger = new ListLogger<WhisperSpeechToTextService>();
        var service = new WhisperSpeechToTextService(client, CreateOptions(SecretApiKey), logger);

        using var stream = new MemoryStream(new byte[] { 1, 2, 3 });
        await service.TranscribeAsync(stream, "audio/webm");

        Assert.NotEmpty(logger.Messages);
        foreach (var msg in logger.Messages)
        {
            Assert.DoesNotContain(SecretApiKey, msg);
        }
    }

    [Theory]
    [InlineData("500_Error")]
    [InlineData("429_RateLimit")]
    [InlineData("Network_Down")]
    public async Task Logs_Never_Contain_Api_Key_In_Tts(string scenario)
    {
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            if (scenario == "Network_Down") throw new HttpRequestException("connection dropped");
            var status = scenario == "429_RateLimit" ? (HttpStatusCode)429 : HttpStatusCode.InternalServerError;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\": {\"message\": \"API error detail\"}}", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var logger = new ListLogger<OpenAiTextToSpeechService>();
        var service = new OpenAiTextToSpeechService(client, CreateOptions(SecretApiKey), logger);

        await service.SynthesizeAsync("Test sentence");

        Assert.NotEmpty(logger.Messages);
        foreach (var msg in logger.Messages)
        {
            Assert.DoesNotContain(SecretApiKey, msg);
        }
    }

    [Theory]
    [InlineData("500_Error")]
    [InlineData("429_RateLimit")]
    [InlineData("Network_Down")]
    public async Task Logs_Never_Contain_Api_Key_In_FollowUp(string scenario)
    {
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            if (scenario == "Network_Down") throw new HttpRequestException("connection dropped");
            var status = scenario == "429_RateLimit" ? (HttpStatusCode)429 : HttpStatusCode.InternalServerError;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"error\": {\"message\": \"API error detail\"}}", Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var logger = new ListLogger<OpenAiFollowUpGeneratorService>();
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions(SecretApiKey), logger);

        await service.GenerateFollowUpQuestionAsync("Question", "Answer", "EXAM01");

        Assert.NotEmpty(logger.Messages);
        foreach (var msg in logger.Messages)
        {
            Assert.DoesNotContain(SecretApiKey, msg);
        }
    }

    [Fact]
    public async Task Whisper_Logs_Never_Contain_Raw_Audio()
    {
        var rawAudioMarker = "RAW-AUDIO-MARKER-" + new string('x', 64);
        var rawAudioBytes = Encoding.ASCII.GetBytes(rawAudioMarker);

        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\": {\"message\": \"Internal error\"}}", Encoding.UTF8, "application/json")
        }));

        using var client = new HttpClient(handler);
        var logger = new ListLogger<WhisperSpeechToTextService>();
        var service = new WhisperSpeechToTextService(client, CreateOptions(), logger);

        using var stream = new MemoryStream(rawAudioBytes);
        await service.TranscribeAsync(stream, "audio/webm");

        Assert.NotEmpty(logger.Messages);
        foreach (var msg in logger.Messages)
        {
            Assert.DoesNotContain(rawAudioMarker, msg);
            Assert.DoesNotContain("RAW-AUDIO-MARKER", msg);
        }
    }
}
