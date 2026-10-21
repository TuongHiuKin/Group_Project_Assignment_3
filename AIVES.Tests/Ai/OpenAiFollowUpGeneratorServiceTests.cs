using System;
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

public class OpenAiFollowUpGeneratorServiceTests
{
    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return await _handler(request, cancellationToken);
        }
    }

    private static IOptions<AiServiceOptions> CreateOptions(
        string? apiKey = "sk-test-key",
        bool useMock = false,
        int timeoutSeconds = 15)
    {
        return Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = useMock,
            FollowUpTimeoutSeconds = timeoutSeconds,
            OpenAI = new OpenAiOptions
            {
                ApiKey = apiKey,
                Model = "gpt-4o-mini"
            }
        });
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Returns_Question_And_Intent_On_Success()
    {
        var responseJson = """
        {
            "choices": [
                {
                    "message": {
                        "content": "{\"followUpQuestion\":\"How does DbContext track entity modifications in memory?\",\"cognitiveIntent\":\"Deep-Dive\"}"
                    }
                }
            ]
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var options = CreateOptions();
        var service = new OpenAiFollowUpGeneratorService(client, options);

        var result = await service.GenerateFollowUpQuestionAsync(
            originalQuestion: "What is the purpose of DbContext in Entity Framework Core?",
            studentAnswerTranscript: "It manages the connection to SQL Server and saves entity changes.",
            subjectContext: "PRN222"
        );

        Assert.True(result.IsSuccess);
        Assert.Equal("How does DbContext track entity modifications in memory?", result.FollowUpQuestionText);
        Assert.Equal("Deep-Dive", result.CognitiveIntent);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test-key", handler.LastRequest.Headers.Authorization?.Parameter);

        // Verify system prompt enforces Assignment 2 no-scoring rule
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("STRICTLY FORBIDDEN: Do NOT provide grades", handler.LastRequestBody);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Handles_401_Unauthorized()
    {
        var errorJson = """
        {
            "error": {
                "message": "Invalid API key provided."
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
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions(apiKey: "sk-invalid"));

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid API key", result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Handles_429_Rate_Limit()
    {
        var errorJson = """
        {
            "error": {
                "message": "Rate limit exceeded for gpt-4o-mini."
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
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions());

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.False(result.IsSuccess);
        Assert.Contains("Rate limit exceeded", result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Handles_500_Server_Error()
    {
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions());

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Falls_Back_To_Mock_When_UseMock_Is_True()
    {
        var mockService = new MockFollowUpGeneratorService
        {
            OverrideFollowUpQuestion = "Fallback follow-up question.",
            OverrideCognitiveIntent = "Clarification"
        };

        using var client = new HttpClient();
        var options = CreateOptions(useMock: true);
        var service = new OpenAiFollowUpGeneratorService(client, options, fallbackService: mockService);

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.True(result.IsSuccess);
        Assert.Equal("Fallback follow-up question.", result.FollowUpQuestionText);
        Assert.Equal("Clarification", result.CognitiveIntent);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Falls_Back_To_Mock_When_ApiKey_Missing()
    {
        var mockService = new MockFollowUpGeneratorService
        {
            OverrideFollowUpQuestion = "Mock question due to no key."
        };

        using var client = new HttpClient();
        var options = CreateOptions(apiKey: null, useMock: false);
        var service = new OpenAiFollowUpGeneratorService(client, options, fallbackService: mockService);

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.True(result.IsSuccess);
        Assert.Equal("Mock question due to no key.", result.FollowUpQuestionText);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Fails_When_ApiKey_Missing_And_No_Fallback()
    {
        using var client = new HttpClient();
        var options = CreateOptions(apiKey: null, useMock: false);
        var service = new OpenAiFollowUpGeneratorService(client, options, fallbackService: null);

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.False(result.IsSuccess);
        Assert.Contains("API key is not configured", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task GenerateFollowUpQuestionAsync_Fails_When_Original_Question_Is_Empty(string? emptyQuestion)
    {
        using var client = new HttpClient();
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions());

        var result = await service.GenerateFollowUpQuestionAsync(emptyQuestion!, "Answer", "PRN222");

        Assert.False(result.IsSuccess);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Handles_Malformed_LLM_Json_Content()
    {
        var malformedJson = """
        {
            "choices": [
                {
                    "message": {
                        "content": "Not a JSON document at all!"
                    }
                }
            ]
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(malformedJson, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        });

        using var client = new HttpClient(handler);
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions());

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.False(result.IsSuccess);
        Assert.Contains("Failed to parse", result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Respects_Caller_CancellationToken()
    {
        using var client = new HttpClient();
        var service = new OpenAiFollowUpGeneratorService(client, CreateOptions());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222", cts.Token));
    }

    [Fact]
    public async Task GenerateFollowUpQuestionAsync_Handles_Timeout_Gracefully()
    {
        var handler = new DelegatingHandlerStub(async (req, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{}"}}]}""")
            };
        });

        using var client = new HttpClient(handler);
        var options = CreateOptions(apiKey: "sk-key", useMock: false, timeoutSeconds: 1);
        var service = new OpenAiFollowUpGeneratorService(client, options);

        var result = await service.GenerateFollowUpQuestionAsync("Q1", "Answer", "PRN222");

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
