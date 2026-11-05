using System.Net;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.Ai.Adapters;
using AIVES.BLL.Services.Ai.Gemini;
using AIVES.BLL.Services.Ai.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class GeminiAnswerGradingServiceTests
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
        const string fakeKey = "AIzaSyGeminiGradingTestKey1234567";
        var profile = new AiProviderProfile
        {
            Id = "gemini-grading",
            Name = "Gemini",
            Kind = AiProviderKinds.Gemini,
            BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
            ApiKey = fakeKey,
            CustomCapabilities = AiCapability.Grading
        };

        var geminiCandidateJson = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "text": "{\n  \"totalScore\": 8.5,\n  \"feedback\": \"Sinh viên nắm vững kiến trúc 3 tầng và vai trò từng tầng.\",\n  \"strengths\": \"Nêu đủ 3 tầng, đúng vai trò của BLL và DAL.\",\n  \"weaknesses\": \"Chưa giải thích chi tiết cơ chế DTO/DI giữa Presentation và BLL.\",\n  \"criteriaOutcomes\": [\n    {\n      \"criterionCode\": \"EXAM01-Q01-C1\",\n      \"criterionName\": \"Nêu đúng 3 tầng và vai trò\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 4.5,\n      \"isMet\": true,\n      \"explanation\": \"Nêu đủ và chính xác vai trò của 3 tầng.\"\n    },\n    {\n      \"criterionCode\": \"EXAM01-Q01-C2\",\n      \"criterionName\": \"Giải thích luồng dữ liệu giữa các tầng\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 4.0,\n      \"isMet\": true,\n      \"explanation\": \"Luồng dữ liệu đúng chiều, chưa nêu rõ DTO.\"\n    }\n  ]\n}"
                  }
                ]
              }
            }
          ]
        }
        """;

        DelegatingHandlerStub? handler = null;
        handler = new DelegatingHandlerStub((req, ct) =>
        {
            // Kiểm tra header x-goog-api-key được gửi đúng
            Assert.True(req.Headers.Contains("x-goog-api-key"));
            Assert.Equal(fakeKey, req.Headers.GetValues("x-goog-api-key").First());

            // API key KHÔNG BAO GIỜ xuất hiện trên URL hay query string
            Assert.DoesNotContain(fakeKey, req.RequestUri!.ToString());

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(geminiCandidateJson, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var service = new GeminiAnswerGradingService(
            client, 
            profile, 
            model: "gemini-3.8-flash", 
            retryDelays: Array.Empty<TimeSpan>());

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        // Verify request payload
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("application/json", handler.LastRequestBody);
        Assert.Contains("system_instruction", handler.LastRequestBody);
        Assert.Contains("EXAM01-Q01", handler.LastRequestBody);

        // Verify result DTO
        Assert.NotNull(result);
        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.Equal(8.5m, result.AiScore);
        Assert.Equal(10m, result.MaxPoints);
        Assert.Equal("Gemini", result.AiProvider);
        Assert.Equal("gemini-3.8-flash", result.AiModel);
        Assert.Contains("nắm vững", result.AiFeedback);
        Assert.NotNull(result.Strengths);
        Assert.Contains("Nêu đủ", result.Strengths);
        Assert.NotNull(result.Weaknesses);
        Assert.Contains("DTO", result.Weaknesses);
        Assert.Equal(2, result.CriteriaOutcomes.Count);

        var c1 = result.CriteriaOutcomes.First(c => c.CriterionCode == "EXAM01-Q01-C1");
        Assert.Equal(4.5m, c1.AwardedPoints);
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
            Id = "gemini-test",
            ApiKey = "AIzaSyTestValidKey",
            Kind = AiProviderKinds.Gemini
        };

        var outOfBoundsJson = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "text": "{\n  \"totalScore\": 15.0,\n  \"feedback\": \"Điểm vượt trần\",\n  \"strengths\": \"Điểm cao\",\n  \"weaknesses\": \"Không\",\n  \"criteriaOutcomes\": [\n    {\n      \"criterionCode\": \"EXAM01-Q01-C1\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": 8.0,\n      \"isMet\": true,\n      \"explanation\": \"Cho vượt max\"\n    },\n    {\n      \"criterionCode\": \"EXAM01-Q01-C2\",\n      \"maxPoints\": 5.0,\n      \"awardedPoints\": -3.0,\n      \"isMet\": false,\n      \"explanation\": \"Điểm âm\"\n    }\n  ]\n}"
                  }
                ]
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
        var service = new GeminiAnswerGradingService(client, profile, retryDelays: Array.Empty<TimeSpan>());

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(5.0m, result.AiScore); // Reconciled to sum of valid criteria (5.0m + 0.0m = 5.0m)
        var c1 = result.CriteriaOutcomes.First(c => c.CriterionCode == "EXAM01-Q01-C1");
        Assert.Equal(5.0m, c1.AwardedPoints); // Clamped to criterion max = 5m

        var c2 = result.CriteriaOutcomes.First(c => c.CriterionCode == "EXAM01-Q01-C2");
        Assert.Equal(0.0m, c2.AwardedPoints); // Clamped from negative to 0m
    }

    [Fact]
    public async Task GradeAnswerAsync_MissingApiKey_UsesFallbackService_WhenProvided()
    {
        var profile = new AiProviderProfile
        {
            Id = "gemini-no-key",
            ApiKey = "", // Empty key
            Kind = AiProviderKinds.Gemini
        };

        var mock = new MockAnswerGradingService();
        using var client = new HttpClient(new DelegatingHandlerStub((_, _) => throw new InvalidOperationException("Should not be called")));
        var service = new GeminiAnswerGradingService(client, profile, fallbackService: mock);

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
            Id = "gemini-no-key",
            ApiKey = "   ",
            Kind = AiProviderKinds.Gemini
        };

        using var client = new HttpClient(new DelegatingHandlerStub((_, _) => throw new InvalidOperationException("Should not be called")));
        var service = new GeminiAnswerGradingService(client, profile, fallbackService: null);

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.NotNull(result);
        Assert.Equal(GradingState.Failed, result.State);
        Assert.Equal(0m, result.AiScore);
        Assert.Contains("Chưa cấu hình API key", result.AiFeedback);
    }

    [Fact]
    public async Task GradeAnswerAsync_HttpError_UsesFallback_WhenAvailable()
    {
        var profile = new AiProviderProfile
        {
            Id = "gemini-err",
            ApiKey = "AIzaSyValidKey",
            Kind = AiProviderKinds.Gemini
        };

        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"error\": {\"message\": \"Gemini internal server error\"}}", Encoding.UTF8, "application/json")
        }));

        var mock = new MockAnswerGradingService();
        using var client = new HttpClient(handler);
        var service = new GeminiAnswerGradingService(client, profile, fallbackService: mock, retryDelays: Array.Empty<TimeSpan>());

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(GradingState.AiGraded, result.State);
        Assert.Equal("Mock", result.AiProvider);
    }

    [Fact]
    public async Task GradeAnswerAsync_HttpError_ReturnsFailed_WithoutLeakingKey_WhenNoFallback()
    {
        const string secretKey = "AIzaSySecretLeakGradingKey999";
        var profile = new AiProviderProfile
        {
            Id = "gemini-err",
            ApiKey = secretKey,
            Kind = AiProviderKinds.Gemini
        };

        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\": {\"message\": \"API key " + secretKey + " is expired or invalid\"}}", Encoding.UTF8, "application/json")
        }));

        using var client = new HttpClient(handler);
        var service = new GeminiAnswerGradingService(client, profile, fallbackService: null, retryDelays: Array.Empty<TimeSpan>());

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
            Id = "gemini-bad-json",
            ApiKey = "AIzaSyTestKey",
            Kind = AiProviderKinds.Gemini
        };

        // Phản hồi không phải JSON
        var handler = new DelegatingHandlerStub((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("This is not valid json at all", Encoding.UTF8, "text/plain")
        }));

        using var client = new HttpClient(handler);
        var service = new GeminiAnswerGradingService(client, profile, fallbackService: null, retryDelays: Array.Empty<TimeSpan>());

        var input = CreateSampleInput();
        var result = await service.GradeAnswerAsync(input);

        Assert.Equal(GradingState.Failed, result.State);
        Assert.Contains("JSON", result.AiFeedback);
    }

    [Fact]
    public async Task GeminiProviderAdapter_CreateAnswerGradingService_IntegratesCorrectly()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.AddScoped<MockAnswerGradingService>();

        var sp = services.BuildServiceProvider();
        var adapter = new GeminiProviderAdapter();

        var profileWithGrading = new AiProviderProfile
        {
            Id = "gemini-profile",
            Kind = AiProviderKinds.Gemini,
            ApiKey = "AIzaSyAdapterTestKey",
            CustomCapabilities = AiCapability.Grading
        };

        var gradingService = adapter.CreateAnswerGradingService(sp, profileWithGrading, "gemini-3.8-flash");
        Assert.NotNull(gradingService);
        Assert.IsType<GeminiAnswerGradingService>(gradingService);

        var profileWithoutGrading = new AiProviderProfile
        {
            Id = "gemini-profile-no-grading",
            Kind = AiProviderKinds.Gemini,
            ApiKey = "AIzaSyAdapterTestKey",
            CustomCapabilities = AiCapability.FollowUpLlm // No grading
        };

        var nullService = adapter.CreateAnswerGradingService(sp, profileWithoutGrading, "gemini-3.8-flash");
        Assert.Null(nullService);
    }
}
