using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Xunit;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Mocks;

namespace AIVES.Tests.Ai;

public class OpenAiTextToSpeechServiceTests
{
    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public HttpRequestMessage? LastRequest { get; private set; }

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return await _handler(request, cancellationToken);
        }
    }

    private static IOptions<AiServiceOptions> CreateOptions(
        string? apiKey = "sk-test-key",
        bool useMock = false,
        int timeoutSeconds = 10)
    {
        return Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = useMock,
            TextToSpeechTimeoutSeconds = timeoutSeconds,
            OpenAI = new OpenAiOptions
            {
                ApiKey = apiKey,
                TtsModel = "tts-1",
                TtsVoice = "alloy"
            }
        });
    }

    [Fact]
    public async Task SynthesizeAsync_Returns_Audio_Url_And_Bytes_On_Success()
    {
        var mockAudioBytes = new byte[] { 0xFF, 0xFB, 0x90, 0x64, 0x01, 0x02, 0x03 };

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(mockAudioBytes)
            };
            res.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var options = CreateOptions();
        var service = new OpenAiTextToSpeechService(client, options);

        var result = await service.SynthesizeAsync("Xin chào thí sinh, hãy giới thiệu bản thân.", "vi-VN");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.AudioUrl);
        Assert.StartsWith("/audio/cache/tts-", result.AudioUrl);
        Assert.Equal(mockAudioBytes, result.AudioData);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test-key", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task SynthesizeAsync_Serves_From_Disk_Cache_When_Available()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "aives_tts_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var text = "Cached question text";
            var locale = "vi-VN";
            var cachedBytes = new byte[] { 0xAA, 0xBB, 0xCC };

            // Pre-seed cache file
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes($"{locale}:{text}")))[..12].ToLowerInvariant();
            var cachedFilePath = Path.Combine(tempDir, $"tts-{hash}.mp3");
            await File.WriteAllBytesAsync(cachedFilePath, cachedBytes);

            // Use client that would fail if network was touched
            var handler = new DelegatingHandlerStub((req, ct) => throw new InvalidOperationException("Network should not be called when cached!"));
            using var client = new HttpClient(handler);
            var service = new OpenAiTextToSpeechService(client, CreateOptions(), cacheDirectory: tempDir);

            var result = await service.SynthesizeAsync(text, locale);

            Assert.True(result.IsSuccess);
            Assert.Equal($"/audio/cache/tts-{hash}.mp3", result.AudioUrl);
            Assert.Equal(cachedBytes, result.AudioData);
            Assert.Null(handler.LastRequest); // Verify network was never touched!
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SynthesizeAsync_Handles_401_Unauthorized()
    {
        var errorJson = """
        {
            "error": {
                "message": "Incorrect API key provided for TTS."
            }
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new OpenAiTextToSpeechService(client, CreateOptions(apiKey: "sk-invalid"));

        var result = await service.SynthesizeAsync("Question text", "vi-VN");

        Assert.False(result.IsSuccess);
        Assert.Contains("Incorrect API key", result.ErrorMessage);
    }

    [Fact]
    public async Task SynthesizeAsync_Handles_429_Rate_Limit()
    {
        var errorJson = """
        {
            "error": {
                "message": "TTS rate limit exceeded."
            }
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(errorJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new OpenAiTextToSpeechService(client, CreateOptions());

        var result = await service.SynthesizeAsync("Question text", "vi-VN");

        Assert.False(result.IsSuccess);
        Assert.Contains("rate limit", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SynthesizeAsync_Handles_500_Server_Error()
    {
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new OpenAiTextToSpeechService(client, CreateOptions());

        var result = await service.SynthesizeAsync("Question text", "vi-VN");

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task SynthesizeAsync_Falls_Back_To_Mock_When_UseMock_Is_True()
    {
        var mockService = new MockTextToSpeechService
        {
            ConfiguredAudioUrl = "/audio/mock-fallback.mp3"
        };

        using var client = new HttpClient();
        var options = CreateOptions(useMock: true);
        var service = new OpenAiTextToSpeechService(client, options, fallbackService: mockService);

        var result = await service.SynthesizeAsync("Some question", "vi-VN");

        Assert.True(result.IsSuccess);
        Assert.Equal("/audio/mock-fallback.mp3", result.AudioUrl);
    }

    [Fact]
    public async Task SynthesizeAsync_Falls_Back_To_Mock_When_ApiKey_Missing()
    {
        var mockService = new MockTextToSpeechService
        {
            ConfiguredAudioUrl = "/audio/mock-no-key.mp3"
        };

        using var client = new HttpClient();
        var options = CreateOptions(apiKey: null, useMock: false);
        var service = new OpenAiTextToSpeechService(client, options, fallbackService: mockService);

        var result = await service.SynthesizeAsync("Question", "vi-VN");

        Assert.True(result.IsSuccess);
        Assert.Equal("/audio/mock-no-key.mp3", result.AudioUrl);
    }

    [Fact]
    public async Task SynthesizeAsync_Fails_When_ApiKey_Missing_And_No_Fallback()
    {
        using var client = new HttpClient();
        var options = CreateOptions(apiKey: null, useMock: false);
        var service = new OpenAiTextToSpeechService(client, options, fallbackService: null);

        var result = await service.SynthesizeAsync("Question", "vi-VN");

        Assert.False(result.IsSuccess);
        Assert.Contains("API key is not configured", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SynthesizeAsync_Fails_When_Text_Is_Empty(string? emptyText)
    {
        using var client = new HttpClient();
        var service = new OpenAiTextToSpeechService(client, CreateOptions());

        var result = await service.SynthesizeAsync(emptyText!, "vi-VN");

        Assert.False(result.IsSuccess);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SynthesizeAsync_Respects_Caller_CancellationToken()
    {
        using var client = new HttpClient();
        var service = new OpenAiTextToSpeechService(client, CreateOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SynthesizeAsync("Question", "vi-VN", cts.Token));
    }

    [Fact]
    public async Task SynthesizeAsync_Handles_Timeout_Gracefully()
    {
        var handler = new DelegatingHandlerStub(async (req, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
            };
        });

        using var client = new HttpClient(handler);
        var options = CreateOptions(apiKey: "sk-key", useMock: false, timeoutSeconds: 1);
        var service = new OpenAiTextToSpeechService(client, options);

        var result = await service.SynthesizeAsync("Question text", "vi-VN");

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
