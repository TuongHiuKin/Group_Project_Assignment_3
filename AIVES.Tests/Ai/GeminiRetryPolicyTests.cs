using System.Net;
using System.Text;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Options;
using AIVES.BLL.Services.Ai.Adapters;
using AIVES.BLL.Services.Ai.Gemini;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class GeminiRetryPolicyTests
{
    private const string FakeKey = "AIzaSyFakeKey1234567890abcdef";
    private static readonly IReadOnlyList<TimeSpan> NoDelay = new[] { TimeSpan.Zero, TimeSpan.Zero };

    private static readonly AiProviderProfile Profile = new()
    {
        Id = "gemini-1",
        Name = "Gemini",
        Kind = AiProviderKinds.Gemini,
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta",
        ApiKey = FakeKey
    };

    private const string FollowUpOkJson = """
    {
      "candidates": [
        { "content": { "parts": [ { "text": "{\"followUpQuestion\": \"Tại sao DbContext Singleton lại gây rò rỉ bộ nhớ trong ứng dụng web?\", \"cognitiveIntent\": \"Deep-Dive\"}" } ] } }
      ]
    }
    """;

    private const string HighDemandJson = """{"error":{"code":503,"message":"This model is currently experiencing high demand."}}""";

    /// <summary>Returns the queued responses in order (the last one repeats) and records every request.</summary>
    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        public List<(string? ApiKey, string? Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null, body));
            var (status, responseBody) = responses[Math.Min(Requests.Count - 1, responses.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<HttpResponseMessage> SendThroughPolicy(SequenceHandler handler)
    {
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/models/m:generateContent")
        {
            Content = new StringContent("{\"payload\":1}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", FakeKey);
        return await GeminiRetryPolicy.SendAsync(client, request, NoDelay, null, "test", CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task SendAsync_TransientErrorThenSuccess_RetriesWithSameHeadersAndBody(HttpStatusCode transient)
    {
        var handler = new SequenceHandler((transient, HighDemandJson), (transient, HighDemandJson), (HttpStatusCode.OK, "{}"));

        using var response = await SendThroughPolicy(handler);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(FakeKey, r.ApiKey);
            Assert.Equal("{\"payload\":1}", r.Body);
        });
    }

    [Fact]
    public async Task SendAsync_StillOverloadedAfterTwoRetries_ReturnsLastErrorResponse()
    {
        var handler = new SequenceHandler((HttpStatusCode.ServiceUnavailable, HighDemandJson));

        using var response = await SendThroughPolicy(handler);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendAsync_NonTransientError_DoesNotRetry(HttpStatusCode status)
    {
        var handler = new SequenceHandler((status, "{}"));

        using var response = await SendThroughPolicy(handler);

        Assert.Equal(status, response.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void DefaultDelays_AreOneThenThreeSeconds()
    {
        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) }, GeminiRetryPolicy.DefaultDelays);
    }

    [Fact]
    public async Task FollowUp_503ThenSuccess_ReturnsGeneratedQuestion()
    {
        var handler = new SequenceHandler((HttpStatusCode.ServiceUnavailable, HighDemandJson), (HttpStatusCode.OK, FollowUpOkJson));
        using var client = new HttpClient(handler);
        var service = new GeminiFollowUpGeneratorService(client, Profile, "gemini-3.8-flash", retryDelays: NoDelay);

        var result = await service.GenerateFollowUpQuestionAsync("Vì sao DbContext là Scoped?", "Vì mỗi request có một DbContext riêng.", "EXAM01");

        Assert.True(result.IsSuccess);
        Assert.Contains("rò rỉ bộ nhớ", result.FollowUpQuestionText);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FollowUp_503ThreeTimes_FailsWithProviderMessageAndNoMock()
    {
        var handler = new SequenceHandler((HttpStatusCode.ServiceUnavailable, HighDemandJson));
        using var client = new HttpClient(handler);
        var service = new GeminiFollowUpGeneratorService(client, Profile, "gemini-3.8-flash", retryDelays: NoDelay);

        var result = await service.GenerateFollowUpQuestionAsync("Vì sao DbContext là Scoped?", "Vì mỗi request có một DbContext riêng.", "EXAM01");

        Assert.False(result.IsSuccess);
        Assert.Empty(result.FollowUpQuestionText);
        Assert.Contains("high demand", result.ErrorMessage);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Stt_429ThenSuccess_ReturnsTranscript()
    {
        const string sttOk = """{"candidates":[{"content":{"parts":[{"text":"Mỗi request một DbContext riêng."}]}}]}""";
        var handler = new SequenceHandler((HttpStatusCode.TooManyRequests, "{}"), (HttpStatusCode.OK, sttOk));
        using var client = new HttpClient(handler);
        var service = new GeminiSpeechToTextService(client, Profile, "gemini-3.8-flash", retryDelays: NoDelay);

        var result = await service.TranscribeAsync(new MemoryStream(new byte[] { 1, 2, 3, 4 }), "audio/webm;codecs=opus");

        Assert.True(result.IsSuccess);
        Assert.Equal("Mỗi request một DbContext riêng.", result.TranscriptText);
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Adapter_FollowUpTimeout_IsReadFromAiServiceOptions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new HttpClient(new SlowHandler()));
        services.AddSingleton<IOptions<AiServiceOptions>>(Options.Create(new AiServiceOptions
        {
            UseMock = false,
            AllowMockFallback = false,
            FollowUpTimeoutSeconds = 1
        }));
        using var sp = services.BuildServiceProvider();

        var service = new GeminiProviderAdapter().CreateFollowUpGeneratorService(sp, Profile, "gemini-3.8-flash");
        Assert.NotNull(service);

        var result = await service.GenerateFollowUpQuestionAsync("Vì sao DbContext là Scoped?", "Vì mỗi request có một DbContext riêng.", "EXAM01");

        Assert.False(result.IsSuccess);
        Assert.Equal("Gemini Follow-up timed out after 1 seconds.", result.ErrorMessage);
    }
}
