using System.Net;
using System.Text;
using System.Text.Json;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Models.Grading;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AIVES.Tests.Ai;

/// <summary>
/// DeepSeek is served by <see cref="OpenAiCompatibleProviderAdapter"/>: same REST shape as OpenAI
/// (Bearer auth, POST /chat/completions, response_format json_object). These tests pin that contract
/// so a future refactor cannot silently break the DeepSeek integration.
/// </summary>
public sealed class DeepSeekIntegrationTests
{
    private const string DeepSeekBaseUrl = "https://api.deepseek.com/v1";
    private const string DeepSeekChatModel = "deepseek-chat";
    private const string FakeKey = "sk-deepseek-fake-key-1234567890";

    private sealed class CapturingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content != null) LastBody = await request.Content.ReadAsStringAsync(ct);
            return await handler(request, ct);
        }
    }

    private static AiProviderProfile DeepSeekProfile(AiCapability capabilities) => new()
    {
        Id = "deepseek",
        Name = "DeepSeek",
        Kind = AiProviderKinds.OpenAiCompatible,
        BaseUrl = DeepSeekBaseUrl,
        ApiKey = FakeKey,
        CustomCapabilities = capabilities
    };

    private static GradingInputDto SampleInput() => new()
    {
        SessionId = Guid.NewGuid(),
        QuestionId = "EXAM01-Q01",
        QuestionText = "Vì sao DbContext phải là Scoped?",
        MaxPoints = 10m,
        StudentAnswerTranscript = "Mỗi HTTP request cần một instance riêng; dùng Singleton sẽ gây rò rỉ bộ nhớ.",
        RubricCriteria = new List<RubricCriterionDto>
        {
            new() { CriterionCode = "Q01-C1", CriterionName = "Vòng đời request", MaxPoints = 5m },
            new() { CriterionCode = "Q01-C2", CriterionName = "Rò rỉ bộ nhớ", MaxPoints = 5m }
        }
    };

    private static string GradingJson() => JsonSerializer.Serialize(new
    {
        totalScore = 9.0,
        feedback = "Trả lời đúng trọng tâm.",
        strengths = "Nêu rõ Scoped và rủi ro memory leak.",
        weaknesses = "Chưa nhắc tới thread-safety.",
        criteriaOutcomes = new[]
        {
            new { criterionCode = "Q01-C1", criterionName = "Vòng đời request", maxPoints = 5.0, awardedPoints = 5.0, isMet = true, explanation = "Đúng." },
            new { criterionCode = "Q01-C2", criterionName = "Rò rỉ bộ nhớ", maxPoints = 5.0, awardedPoints = 4.0, isMet = true, explanation = "Đúng." }
        }
    });

    private static string ChatCompletionEnvelope(string contentJson) => JsonSerializer.Serialize(new
    {
        id = "deepseek-chatcmpl-1",
        @object = "chat.completion",
        model = DeepSeekChatModel,
        choices = new[]
        {
            new { index = 0, message = new { role = "assistant", content = contentJson }, finish_reason = "stop" }
        },
        usage = new { prompt_tokens = 900, completion_tokens = 300, total_tokens = 1200 }
    });

    [Fact]
    public void Adapter_declares_OpenAICompatible_and_supports_grading()
    {
        var adapter = new OpenAiCompatibleProviderAdapter();

        Assert.Equal(AiProviderKinds.OpenAiCompatible, adapter.Kind);
        Assert.True(adapter.SupportedCapabilities.HasFlag(AiCapability.Grading));
    }

    [Fact]
    public void Adapter_creates_a_grading_service_for_a_DeepSeek_profile()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient());

        var adapter = new OpenAiCompatibleProviderAdapter();
        var service = adapter.CreateAnswerGradingService(
            services.BuildServiceProvider(), DeepSeekProfile(AiCapability.Grading), DeepSeekChatModel);

        Assert.NotNull(service);
    }

    [Fact]
    public void Adapter_returns_null_when_the_profile_does_not_enable_grading()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient());

        var adapter = new OpenAiCompatibleProviderAdapter();
        var service = adapter.CreateAnswerGradingService(
            services.BuildServiceProvider(), DeepSeekProfile(AiCapability.FollowUpLlm), DeepSeekChatModel);

        Assert.Null(service);
    }

    [Fact]
    public async Task Grading_posts_to_deepseek_chat_completions_with_bearer_auth_and_json_mode()
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ChatCompletionEnvelope(GradingJson()), Encoding.UTF8, "application/json")
        }));

        var service = new OpenAiAnswerGradingService(
            new HttpClient(handler),
            DeepSeekProfile(AiCapability.Grading),
            model: DeepSeekChatModel);

        var result = await service.GradeAnswerAsync(SampleInput());

        // 1. Endpoint and transport
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal($"{DeepSeekBaseUrl}/chat/completions", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal(FakeKey, handler.LastRequest.Headers.Authorization.Parameter);

        // 2. Payload shape expected by DeepSeek
        using var payload = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(DeepSeekChatModel, payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_object", payload.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Array, payload.RootElement.GetProperty("messages").ValueKind);
        Assert.DoesNotContain("OpenAI-Organization", string.Join(",", handler.LastRequest.Headers.Select(h => h.Key)));

        // 3. Parsed result
        Assert.Equal(9.0m, result.AiScore);
        Assert.Equal(DeepSeekChatModel, result.AiModel);
        Assert.Equal("OpenAI-Compatible", result.AiProvider);
        Assert.Equal(2, result.CriteriaOutcomes.Count);
    }

    [Fact]
    public async Task Grading_surfaces_a_DeepSeek_api_error_without_leaking_the_key()
    {
        const string body = "{\"error\":{\"message\":\"Insufficient Balance\",\"type\":\"insufficient_balance\"}}";
        var handler = new CapturingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var service = new OpenAiAnswerGradingService(
            new HttpClient(handler),
            DeepSeekProfile(AiCapability.Grading),
            model: DeepSeekChatModel);

        var result = await service.GradeAnswerAsync(SampleInput());

        Assert.Equal(GradingState.Failed, result.State);
        Assert.Contains("Insufficient Balance", result.AiFeedback);
        Assert.DoesNotContain(FakeKey, result.AiFeedback);
    }

    [Fact]
    public async Task Adapter_test_connection_hits_models_endpoint_for_DeepSeek()
    {
        var handler = new CapturingHandler((req, _) =>
        {
            Assert.Equal($"{DeepSeekBaseUrl}/models", req.RequestUri!.ToString());
            Assert.Equal(FakeKey, req.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"id\":\"deepseek-chat\"},{\"id\":\"deepseek-reasoner\"}]}", Encoding.UTF8, "application/json")
            });
        });

        var adapter = new OpenAiCompatibleProviderAdapter();
        var (ok, message) = await adapter.TestConnectionAsync(new HttpClient(handler), DeepSeekProfile(AiCapability.Grading));

        Assert.True(ok, message);
    }

    [Fact]
    public async Task Adapter_lists_DeepSeek_models()
    {
        var handler = new CapturingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"data\":[{\"id\":\"deepseek-chat\"},{\"id\":\"deepseek-reasoner\"}]}", Encoding.UTF8, "application/json")
        }));

        var adapter = new OpenAiCompatibleProviderAdapter();
        var models = await adapter.ListModelsAsync(new HttpClient(handler), DeepSeekProfile(AiCapability.Grading));

        Assert.Equal(new[] { "deepseek-chat", "deepseek-reasoner" }, models);
    }
}
