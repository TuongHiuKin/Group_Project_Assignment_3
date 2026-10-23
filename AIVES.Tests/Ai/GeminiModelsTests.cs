using System.Net;
using System.Text;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Adapters;
using AIVES.BLL.Services.Ai.Gemini;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class GeminiModelsTests
{
    private static readonly string[] LiveModels = ["gemini-3.8-flash", "gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts", "gemini-3.5-flash", "gemini-3.5-flash-lite"];

    private static AiProviderProfile Profile(string? apiKey = "AIzaSyFakeKey1234567890abcdef") => new()
    {
        Id = "g", Name = "Gemini", Kind = AiProviderKinds.Gemini,
        BaseUrl = "https://generativelanguage.googleapis.com/v1beta", ApiKey = apiKey
    };

    private sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public void Adapter_DefaultModels_AreValidGemini38Models()
    {
        var adapter = new GeminiProviderAdapter();

        Assert.Equal("gemini-3.8-flash", adapter.GetDefaultModel(AiCapability.SpeechToText));
        Assert.Equal("gemini-3.8-flash-tts", adapter.GetDefaultModel(AiCapability.TextToSpeech));
        Assert.Equal("gemini-3.8-flash", adapter.GetDefaultModel(AiCapability.FollowUpLlm));
    }

    [Fact]
    public void CredentialStoreDefaultRouting_And_ServiceDefaults_UseSameConstants()
    {
        var all = new[] { GeminiModels.DefaultSpeechToText, GeminiModels.DefaultTextToSpeech, GeminiModels.DefaultFollowUp };

        Assert.DoesNotContain(all, m => m.Contains("1.5") || m.Contains("2.0") || m.Contains("preview"));
        Assert.True(GeminiModels.IsSuitableFor(AiCapability.TextToSpeech, GeminiModels.DefaultTextToSpeech));
        Assert.True(GeminiModels.IsSuitableFor(AiCapability.SpeechToText, GeminiModels.DefaultSpeechToText));
        Assert.True(GeminiModels.IsSuitableFor(AiCapability.FollowUpLlm, GeminiModels.DefaultFollowUp));
    }

    [Theory]
    [InlineData(AiCapability.TextToSpeech, "gemini-3.8-flash-tts", true)]
    [InlineData(AiCapability.TextToSpeech, "gemini-2.0-flash", false)]
    [InlineData(AiCapability.SpeechToText, "gemini-3.8-flash", true)]
    [InlineData(AiCapability.SpeechToText, "gemini-3.8-flash-tts", false)]
    [InlineData(AiCapability.FollowUpLlm, "gemini-3.8-flash-lite-tts", false)]
    [InlineData(AiCapability.FollowUpLlm, "", false)]
    public void IsSuitableFor_ChecksTtsNaming(AiCapability capability, string model, bool expected)
    {
        Assert.Equal(expected, GeminiModels.IsSuitableFor(capability, model));
    }

    [Theory]
    [InlineData(AiCapability.TextToSpeech, "gemini-3.8-flash-preview-tts", "gemini-3.8-flash-tts")]
    [InlineData(AiCapability.TextToSpeech, "gemini-2.0-flash", "gemini-3.8-flash-tts")]
    [InlineData(AiCapability.FollowUpLlm, "gemini-1.5-flash", "gemini-3.8-flash")]
    [InlineData(AiCapability.FollowUpLlm, "gemini-3.5-flash-lit", "gemini-3.5-flash-lite")]
    [InlineData(AiCapability.SpeechToText, "gemini-3.8-flash-tts", "gemini-3.8-flash")]
    public void SuggestModel_PicksClosestModelForCapability(AiCapability capability, string requested, string expected)
    {
        Assert.Equal(expected, GeminiModels.SuggestModel(capability, requested, LiveModels));
    }

    [Fact]
    public void SuggestModel_NoSuitableModel_ReturnsNull()
    {
        Assert.Null(GeminiModels.SuggestModel(AiCapability.TextToSpeech, "x", ["gemini-3.8-flash", "gpt-4o"]));
    }

    [Fact]
    public async Task TryListLiveModels_Success_ReturnsLiveNamesWithoutPrefix()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, """{"models":[{"name":"models/gemini-3.8-flash"},{"name":"models/gemini-3.8-flash-tts"}]}""");
        using var client = new HttpClient(handler);

        var models = await new GeminiProviderAdapter().TryListLiveModelsAsync(client, Profile());

        Assert.Equal(new[] { "gemini-3.8-flash", "gemini-3.8-flash-tts" }, models);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task TryListLiveModels_Failure_ReturnsNull_AndListModelsFallsBackToKnownModels(HttpStatusCode status, string body)
    {
        using var client = new HttpClient(new StaticHandler(status, body));
        var adapter = new GeminiProviderAdapter();

        Assert.Null(await adapter.TryListLiveModelsAsync(client, Profile()));
        Assert.Equal(GeminiModels.KnownModels, await adapter.ListModelsAsync(client, Profile()));
    }

    [Fact]
    public async Task TryListLiveModels_WithoutApiKey_DoesNotCallNetwork()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);

        Assert.Null(await new GeminiProviderAdapter().TryListLiveModelsAsync(client, Profile(apiKey: null)));
        Assert.Equal(0, handler.Calls);
    }
}
