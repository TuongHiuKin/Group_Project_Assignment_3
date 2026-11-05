using System.Net;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Adapters;
using AIVES.BLL.Services.Ai.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class OpenAiAnswerGradingServiceTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return await handler(request, cancellationToken);
        }
    }

    private static GradingInputDto CreateSampleInput() => new()
    {
        SessionId = Guid.NewGuid(),
        QuestionId = "EXAM01-Q01",
        QuestionText = "Giải thích kiến trúc 3 lớp (3-layer architecture) trong ASP.NET Core.",
        BloomLevel = "Understand",
        MaxPoints = 10m,
        StudentAnswerTranscript = "Kiến trúc 3 lớp gồm Presentation, BLL và DAL. Presentation nhận request, BLL xử lý nghiệp vụ, DAL thao tác với CSDL qua EF Core.",
        RubricCriteria = new List<RubricCriterionDto>
        {
            new() { CriterionCode = "EXAM01-Q01-C1", CriterionName = "Nêu đúng 3 tầng và vai trò", MaxPoints = 5m },
            new() { CriterionCode = "EXAM01-Q01-C2", CriterionName = "Giải thích luồng dữ liệu giữa các tầng", MaxPoints = 5m }
        }
    };

    [Fact]
    public async Task GradeAnswerAsync_Success_ReturnsGradedResult_WithCriteriaAndScores()
    {
        const string fakeKey = "sk-test-fake-openai-grading-key-1234567890";
        var profile = new AiProviderProfile
        {
            Id = "openai-grading",
            Name = "OpenAI",
            Kind = AiProviderKinds.OpenAi,
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = fakeKey,
            CustomCapabilities = AiCapability.Grading
        };

        var openAiContentJson = "{\n  \"totalScore\": 9.0,\n  \"feedback\": \"Sinh viên trả lời rất rõ ràng và chuẩn xác.\",\n  \"strengths\": \"Phân biệt rành mạch 3 tầng và giải thích vai trò EF Core.\",\n  \"weaknesses\": \"Có thể bổ sung thêm ví dụ về DTO mapping.\",\n  \"criteriaOutcomes\": [\n    {\n      \"criterionCode\": \"EXAM01-Q01-C1\",\n      \"criterionName\": \"Nêu đúng 3 tầng và vai trò\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 5.0,\n      \"isMet\": true,\n      \"explanation\": \"Nêu trọn vẹn vai trò của Presentation, BLL, DAL.\"\n    },\n    {\n      \"criterionCode\": \"EXAM01-Q01-C2\",\n      \"criterionName\": \"Giải thích luồng dữ liệu giữa các tầng\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 4.0,\n      \"isMet\": true,\n      \"explanation\": \"Luồng dữ liệu hợp lý.\"\n    }\n  ]\n}";

        var openAiResponseJson = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-test",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = openAiContentJson
                    }
                }
            }
        });

        DelegatingHandlerStub? handler = null;
        handler = new DelegatingHandlerStub((req, ct) =>
        {
            // Kiểm tra header Authorization: Bearer
            Assert.NotNull(req.Headers.Authorization);
            Assert.Equal("Bearer", req.Headers.Authorization.Scheme);
            Assert.Equal(fakeKey, req.Headers.Authorization.Parameter);

            // Endpoint POST .../chat/completions
            Assert.EndsWith("/chat/completions", req.RequestUri!.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(openAiResponseJson, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var service = new OpenAiAnswerGradingService(client, profile, model: "gpt-4o-mini");

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        // Verify request payload
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("json_object", handler.LastRequestBody);
        Assert.Contains("gpt-4o-mini", handler.LastRequestBody);
        Assert.Contains("EXAM01-Q01", handler.LastRequestBody);

        // Verify result DTO
        Assert.NotNull(result);
        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.Equal(9.0m, result.AiScore);
        Assert.Equal(10m, result.MaxPoints);
        Assert.Equal("OpenAI", result.AiProvider);
        Assert.Equal("gpt-4o-mini", result.AiModel);
        Assert.Contains("rất rõ ràng", result.AiFeedback);
        Assert.NotNull(result.Strengths);
        Assert.Contains("Phân biệt", result.Strengths);
        Assert.NotNull(result.Weaknesses);
        Assert.Contains("DTO", result.Weaknesses);
        Assert.Equal(2, result.CriteriaOutcomes.Count);

        var c1 = result.CriteriaOutcomes.First(c => c.CriterionCode == "EXAM01-Q01-C1");
        Assert.Equal(5.0m, c1.AwardedPoints);
        Assert.True(c1.IsMet);

        var c2 = result.CriteriaOutcomes.First(c => c.CriterionCode == "EXAM01-Q01-C2");
        Assert.Equal(4.0m, c2.AwardedPoints);
        Assert.True(c2.IsMet);
    }

    [Fact]
    public async Task GradeAnswerAsync_ClampsScores_WhenAiReturnsOutOfBounds()
    {
        var profile = new AiProviderProfile
        {
            Id = "openai-test",
            ApiKey = "sk-valid-key",
            Kind = AiProviderKinds.OpenAi
        };

        var outOfBoundsJson = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "{\n  \"totalScore\": 20.0,\n  \"feedback\": \"Điểm vượt trần\",\n  \"strengths\": \"Tốt\",\n  \"weaknesses\": \"Không\",\n  \"criteriaOutcomes\": [\n    {\n      \"criterionCode\": \"EXAM01-Q01-C1\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 10.0,\n      \"isMet\": true,\n      \"explanation\": \"Vượt max\"\n    },\n    {\n      \"criterionCode\": \"EXAM01-Q01-C2\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": -5.0,\n      \"isMet\": false,\n      \"explanation\": \"Âm\"\n    }\n  ]\n}"
              }
            }
          ]
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(outOfBoundsJson, Encoding.UTF8, "application/json")
        }));

        using var client = new HttpClient(handler);
        var service = new OpenAiAnswerGradingService(client, profile);

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(5.0m, result.AiScore); // Reconciled to sum of valid criteria (5.0m + 0.0m = 5.0m)
        var c1 = result.CriteriaOutcomes.First(c => c.CriterionCode == "EXAM01-Q01-C1");
        Assert.Equal(5.0m, c1.AwardedPoints); // Clamped to 5m

        var c2 = result.CriteriaOutcomes.First(c => c.CriterionCode == "EXAM01-Q01-C2");
        Assert.Equal(0.0m, c2.AwardedPoints); // Clamped from -5 to 0m
    }

    [Fact]
    public async Task GradeAnswerAsync_MissingApiKey_UsesFallbackService_WhenProvided()
    {
        var profile = new AiProviderProfile
        {
            Id = "openai-no-key",
            ApiKey = "",
            Kind = AiProviderKinds.OpenAi
        };

        var mock = new MockAnswerGradingService();
        using var client = new HttpClient(new DelegatingHandlerStub((_, _) => throw new InvalidOperationException("Should not be called")));
        var service = new OpenAiAnswerGradingService(client, profile, fallbackService: mock);

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.Equal("Mock", result.AiProvider);
    }

    [Fact]
    public async Task GradeAnswerAsync_MissingApiKey_ReturnsFailedResult_WhenNoFallback()
    {
        var profile = new AiProviderProfile
        {
            Id = "openai-no-key",
            ApiKey = "   ",
            Kind = AiProviderKinds.OpenAi
        };

        using var client = new HttpClient(new DelegatingHandlerStub((_, _) => throw new InvalidOperationException("Should not be called")));
        var service = new OpenAiAnswerGradingService(client, profile, fallbackService: null);

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.Equal(GradingState.Failed, result.State);
        Assert.Equal(0m, result.AiScore);
        Assert.Contains("Chưa cấu hình API key", result.AiFeedback);
    }

    [Fact]
    public async Task GradeAnswerAsync_CustomEndpoint_WithoutApiKey_Succeeds()
    {
        var profile = new AiProviderProfile
        {
            Id = "ollama-local",
            Kind = AiProviderKinds.OpenAiCompatible,
            BaseUrl = "http://localhost:11434/v1",
            ApiKey = "" // Local providers often don't require an API key
        };

        var localResponse = """
        {
          "choices": [
            {
              "message": {
                "role": "assistant",
                "content": "{\n  \"totalScore\": 7.5,\n  \"feedback\": \"Chấm từ mô hình local Ollama.\",\n  \"strengths\": \"Đầy đủ ý\",\n  \"weaknesses\": \"Chưa sâu\",\n  \"criteriaOutcomes\": [\n    {\n      \"criterionCode\": \"EXAM01-Q01-C1\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 4.0,\n      \"isMet\": true,\n      \"explanation\": \"Đạt\"\n    },\n    {\n      \"criterionCode\": \"EXAM01-Q01-C2\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 3.5,\n      \"isMet\": true,\n      \"explanation\": \"Đạt\"\n    }\n  ]\n}"
              }
            }
          ]
        }
        """;

        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            Assert.Null(req.Headers.Authorization); // No auth header required
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(localResponse, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var service = new OpenAiAnswerGradingService(client, profile, model: "llama3");

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.Equal(7.5m, result.AiScore);
        Assert.Equal("OpenAI-Compatible", result.AiProvider);
        Assert.Equal("llama3", result.AiModel);
    }

    [Fact]
    public async Task GradeAnswerAsync_HttpError_UsesFallback_WhenAvailable()
    {
        var profile = new AiProviderProfile
        {
            Id = "openai-err",
            ApiKey = "sk-valid-key",
            Kind = AiProviderKinds.OpenAi
        };

        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\": {\"message\": \"OpenAI server error\"}}", Encoding.UTF8, "application/json")
        }));

        var mock = new MockAnswerGradingService();
        using var client = new HttpClient(handler);
        var service = new OpenAiAnswerGradingService(client, profile, fallbackService: mock);

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.Equal("Mock", result.AiProvider);
    }

    [Fact]
    public async Task GradeAnswerAsync_HttpError_ReturnsFailed_WithoutLeakingKey_WhenNoFallback()
    {
        const string secretKey = "sk-super-secret-openai-leak-key";
        var profile = new AiProviderProfile
        {
            Id = "openai-err",
            ApiKey = secretKey,
            Kind = AiProviderKinds.OpenAi
        };

        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\": {\"message\": \"Incorrect API key provided: " + secretKey + "\"}}", Encoding.UTF8, "application/json")
        }));

        using var client = new HttpClient(handler);
        var service = new OpenAiAnswerGradingService(client, profile, fallbackService: null);

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(GradingState.Failed, result.State);
        Assert.Contains("401", result.AiFeedback);
        Assert.DoesNotContain(secretKey, result.AiFeedback);
    }

    [Fact]
    public async Task GradeAnswerAsync_MalformedJson_UsesFallback_Or_ReturnsFailed()
    {
        var profile = new AiProviderProfile
        {
            Id = "openai-bad-json",
            ApiKey = "sk-test",
            Kind = AiProviderKinds.OpenAi
        };

        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Not valid JSON payload", Encoding.UTF8, "text/plain")
        }));

        using var client = new HttpClient(handler);
        var service = new OpenAiAnswerGradingService(client, profile, fallbackService: null);

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(GradingState.Failed, result.State);
        Assert.Contains("JSON", result.AiFeedback);
    }

    [Fact]
    public async Task OpenAiProviderAdapter_CreateAnswerGradingService_IntegratesCorrectly()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddScoped<MockAnswerGradingService>();

        var sp = services.BuildServiceProvider();
        var adapter = new OpenAiProviderAdapter();

        var profileWithGrading = new AiProviderProfile
        {
            Id = "openai-profile",
            Kind = AiProviderKinds.OpenAi,
            ApiKey = "sk-test-openai",
            CustomCapabilities = AiCapability.Grading
        };

        var gradingService = adapter.CreateAnswerGradingService(sp, profileWithGrading, "gpt-4o-mini");
        Assert.NotNull(gradingService);
        Assert.IsType<OpenAiAnswerGradingService>(gradingService);

        var profileWithoutGrading = new AiProviderProfile
        {
            Id = "openai-no-grading",
            Kind = AiProviderKinds.OpenAi,
            ApiKey = "sk-test-openai",
            CustomCapabilities = AiCapability.SpeechToText
        };

        var nullService = adapter.CreateAnswerGradingService(sp, profileWithoutGrading, "gpt-4o-mini");
        Assert.Null(nullService);
    }

    [Fact]
    public async Task OpenAiCompatibleProviderAdapter_CreateAnswerGradingService_IntegratesCorrectly()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddScoped<MockAnswerGradingService>();

        var sp = services.BuildServiceProvider();
        var adapter = new OpenAiCompatibleProviderAdapter();

        var profileWithGrading = new AiProviderProfile
        {
            Id = "deepseek-profile",
            Kind = AiProviderKinds.OpenAiCompatible,
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "sk-deepseek-key",
            CustomCapabilities = AiCapability.Grading
        };

        var gradingService = adapter.CreateAnswerGradingService(sp, profileWithGrading, "deepseek-chat");
        Assert.NotNull(gradingService);
        Assert.IsType<OpenAiAnswerGradingService>(gradingService);

        var profileWithoutGrading = new AiProviderProfile
        {
            Id = "deepseek-no-grading",
            Kind = AiProviderKinds.OpenAiCompatible,
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "sk-deepseek-key",
            CustomCapabilities = AiCapability.FollowUpLlm
        };

        var nullService = adapter.CreateAnswerGradingService(sp, profileWithoutGrading, "deepseek-chat");
        Assert.Null(nullService);
    }
}

