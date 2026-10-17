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

public class WhisperSpeechToTextServiceTests
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
        string? apiKey = "sk-mock-key",
        bool useMock = false,
        int timeoutSeconds = 15)
    {
        return Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = useMock,
            SpeechToTextTimeoutSeconds = timeoutSeconds,
            OpenAI = new OpenAiOptions
            {
                ApiKey = apiKey,
                WhisperModel = "whisper-1"
            }
        });
    }

    [Fact]
    public async Task TranscribeAsync_Returns_Transcript_On_Successful_Api_Call()
    {
        var jsonResponse = """
        {
            "text": "Scoped DbContext is instantiated once per HTTP request.",
            "language": "english",
            "segments": [
                { "no_speech_prob": 0.05 }
            ]
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var options = CreateOptions(apiKey: "sk-test-valid");
        var service = new WhisperSpeechToTextService(client, options);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.True(result.IsSuccess);
        Assert.Equal("Scoped DbContext is instantiated once per HTTP request.", result.TranscriptText);
        Assert.Equal(0.95m, result.ConfidenceScore);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test-valid", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task TranscribeAsync_Handles_OpenAI_401_Unauthorized()
    {
        var errorResponse = """
        {
            "error": {
                "message": "Incorrect API key provided: sk-invalid.",
                "type": "invalid_request_error"
            }
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(errorResponse, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new WhisperSpeechToTextService(client, CreateOptions(apiKey: "sk-invalid"));

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Contains("Incorrect API key provided", result.ErrorMessage);
    }

    [Fact]
    public async Task TranscribeAsync_Handles_Rate_Limit_429()
    {
        var errorResponse = """
        {
            "error": {
                "message": "You exceeded your current quota.",
                "type": "insufficient_quota"
            }
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(errorResponse, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new WhisperSpeechToTextService(client, CreateOptions());

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/wav");

        Assert.False(result.IsSuccess);
        Assert.Contains("quota", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeAsync_Handles_Http_500_Server_Error()
    {
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server temporarily unavailable")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new WhisperSpeechToTextService(client, CreateOptions());

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task TranscribeAsync_Falls_Back_To_Mock_When_UseMock_Is_True()
    {
        var mockService = new MockSpeechToTextService
        {
            ConfiguredTranscript = "Mock transcript fallback."
        };

        using var client = new HttpClient();
        var options = CreateOptions(useMock: true);
        var service = new WhisperSpeechToTextService(client, options, fallbackService: mockService);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.True(result.IsSuccess);
        Assert.Equal("Mock transcript fallback.", result.TranscriptText);
    }

    [Fact]
    public async Task TranscribeAsync_Falls_Back_To_Mock_When_ApiKey_Is_Missing()
    {
        var mockService = new MockSpeechToTextService
        {
            ConfiguredTranscript = "Mock fallback due to missing key."
        };

        using var client = new HttpClient();
        var options = CreateOptions(apiKey: null, useMock: false);
        var service = new WhisperSpeechToTextService(client, options, fallbackService: mockService);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.True(result.IsSuccess);
        Assert.Equal("Mock fallback due to missing key.", result.TranscriptText);
    }

    [Fact]
    public async Task TranscribeAsync_Fails_When_ApiKey_Missing_And_No_Fallback()
    {
        using var client = new HttpClient();
        var options = CreateOptions(apiKey: null, useMock: false);
        var service = new WhisperSpeechToTextService(client, options, fallbackService: null);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Contains("API key is not configured", result.ErrorMessage);
    }

    [Fact]
    public async Task TranscribeAsync_Fails_On_Null_Stream()
    {
        using var client = new HttpClient();
        var service = new WhisperSpeechToTextService(client, CreateOptions());

        var result = await service.TranscribeAsync(null!, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Contains("null", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeAsync_Fails_On_Empty_Stream()
    {
        using var client = new HttpClient();
        var service = new WhisperSpeechToTextService(client, CreateOptions());

        using var emptyStream = new MemoryStream(Array.Empty<byte>());
        var result = await service.TranscribeAsync(emptyStream, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeAsync_Respects_Caller_CancellationToken()
    {
        using var client = new HttpClient();
        var service = new WhisperSpeechToTextService(client, CreateOptions());

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.TranscribeAsync(audioStream, "audio/webm", cts.Token));
    }

    [Fact]
    public async Task TranscribeAsync_Handles_Timeout_Gracefully()
    {
        var handler = new DelegatingHandlerStub(async (req, ct) =>
        {
            // Simulate 1500ms delay while timeout is set to 1000ms (1s)
            await Task.Delay(1500, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"late"}""")
            };
        });

        using var client = new HttpClient(handler);
        // Timeout set to 1 second; let's trigger timeout with 1s timeout and delay 1.5s
        var options = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            SpeechToTextTimeoutSeconds = 1,
            OpenAI = new OpenAiOptions { ApiKey = "sk-key" }
        });
        var service = new WhisperSpeechToTextService(client, options);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TranscribeAsync_ContentTypeWithCodecs_SendsRequest()
    {
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"hello"}""", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var options = CreateOptions(apiKey: "sk-test-valid");
        var service = new WhisperSpeechToTextService(client, options);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm;codecs=opus");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task TranscribeAsync_SendsVietnameseLanguage_ByDefault()
    {
        DelegatingHandlerStub? handler = null;
        handler = new DelegatingHandlerStub(async (req, ct) =>
        {
            var content = req.Content as MultipartFormDataContent;
            Assert.NotNull(content);
            var parts = new System.Collections.Generic.Dictionary<string, string>();
            foreach (var part in content)
            {
                if (part.Headers.ContentDisposition?.Name != null)
                {
                    var name = part.Headers.ContentDisposition.Name.Trim('"');
                    if (part is StringContent strContent)
                    {
                        parts[name] = await strContent.ReadAsStringAsync(ct);
                    }
                }
            }
            Assert.True(parts.ContainsKey("language"));
            Assert.Equal("vi", parts["language"]);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"chào bạn"}""", Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var options = CreateOptions(apiKey: "sk-test-valid");
        var service = new WhisperSpeechToTextService(client, options);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task TranscribeAsync_EmptyLanguage_OmitsLanguagePart()
    {
        DelegatingHandlerStub? handler = null;
        handler = new DelegatingHandlerStub(async (req, ct) =>
        {
            var content = req.Content as MultipartFormDataContent;
            Assert.NotNull(content);
            var hasLanguage = false;
            foreach (var part in content)
            {
                if (part.Headers.ContentDisposition?.Name?.Trim('"') == "language")
                {
                    hasLanguage = true;
                }
            }
            Assert.False(hasLanguage);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"hello"}""", Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            SpeechToTextTimeoutSeconds = 15,
            OpenAI = new OpenAiOptions
            {
                ApiKey = "sk-test-valid",
                WhisperModel = "whisper-1",
                TranscriptionLanguage = null
            }
        });
        var service = new WhisperSpeechToTextService(client, options);

        using var audioStream = new MemoryStream(new byte[] { 1, 2, 3 });
        var result = await service.TranscribeAsync(audioStream, "audio/webm");

        Assert.True(result.IsSuccess);
    }
}
