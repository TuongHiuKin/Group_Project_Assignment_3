using System.Net;
using System.Text;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class OpenAiCompatibleAdapterTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return await handler(request, cancellationToken);
        }
    }

    [Fact]
    public async Task OpenAiCompatible_Localhost_With_Empty_Key_Does_Not_Send_Authorization_Header()
    {
        var profile = new AiProviderProfile
        {
            Id = "ollama-local",
            Name = "Ollama Local",
            Kind = AiProviderKinds.OpenAiCompatible,
            BaseUrl = "http://localhost:11434/v1",
            ApiKey = null, // No key for local Ollama
            CustomCapabilities = AiCapability.FollowUpLlm
        };

        var responseJson = """
        {
          "choices": [
            {
              "message": {
                "content": "{\"followUpQuestion\": \"Làm sao để giải quyết race condition khi nhiều thread cùng ghi vào collection?\", \"cognitiveIntent\": \"Deep-Dive\"}"
              }
            }
          ]
        }
        """;

        DelegatingHandlerStub? handler = null;
        handler = new DelegatingHandlerStub((req, ct) =>
        {
            // Verify request went to localhost endpoint
            Assert.StartsWith("http://localhost:11434/v1/chat/completions", req.RequestUri!.ToString());

            // Verify NO authorization header was sent
            Assert.Null(req.Headers.Authorization);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        });

        using var client = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new AiServiceOptions
        {
            UseMock = false,
            OpenAI = new OpenAiOptions
            {
                BaseUrl = profile.BaseUrl,
                Model = "llama3.2",
                ApiKey = null
            }
        });

        var service = new OpenAiFollowUpGeneratorService(client, options);
        var result = await service.GenerateFollowUpQuestionAsync("Concurrency là gì?", "Em dùng lock để đồng bộ.", "Computer Science");

        Assert.True(result.IsSuccess);
        Assert.Equal("Làm sao để giải quyết race condition khi nhiều thread cùng ghi vào collection?", result.FollowUpQuestionText);
    }

    [Fact]
    public void OpenAiCompatible_Enforces_CustomCapabilities_Filtering()
    {
        var profileLlmOnly = new AiProviderProfile
        {
            Id = "groq-llm",
            Name = "Groq",
            Kind = AiProviderKinds.OpenAiCompatible,
            BaseUrl = "https://api.groq.com/openai/v1",
            ApiKey = "gsk_test123",
            CustomCapabilities = AiCapability.FollowUpLlm // Only LLM enabled, no STT or TTS
        };

        var services = new ServiceCollection();
        services.AddSingleton(new HttpClient());
        services.AddSingleton(new FollowUpPolicyValidator());
        var sp = services.BuildServiceProvider();

        var adapter = new OpenAiCompatibleProviderAdapter();

        var stt = adapter.CreateSpeechToTextService(sp, profileLlmOnly, "whisper-large-v3");
        var tts = adapter.CreateTextToSpeechService(sp, profileLlmOnly, "tts-1", "alloy", null);
        var llm = adapter.CreateFollowUpGeneratorService(sp, profileLlmOnly, "llama-3.3-70b-versatile");

        // STT and TTS must return null (fallback to mock) because Groq profile declared only LLM
        Assert.Null(stt);
        Assert.Null(tts);

        // LLM must be created successfully
        Assert.NotNull(llm);
    }
}
