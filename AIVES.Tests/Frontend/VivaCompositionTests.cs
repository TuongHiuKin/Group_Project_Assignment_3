using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Services.Ai.Mocks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AIVES.Tests.Frontend;

public sealed class VivaCompositionTests : IClassFixture<VivaCompositionTests.CompositionFactory>
{
    public sealed class CompositionFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["VivaInterview:Enabled"] = "true",
                ["AiServices:UseMock"] = "true",
                ["AiServices:CredentialStorePath"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aives-mock-store-" + System.Guid.NewGuid() + ".json"),
                ["ConnectionStrings:DefaultConnection"] = "Server=unused;Database=CompositionFixture;Integrated Security=true;TrustServerCertificate=true",
                ["DefaultAdmin:Email"] = "composition@example.test",
                ["DefaultAdmin:Password"] = "fixture-only-not-a-real-account"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddLogging(options => options.SetMinimumLevel(LogLevel.Error));
            });
        }
    }

    private readonly CompositionFactory _factory;

    public VivaCompositionTests(CompositionFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void VivaServices_AreResolvedCorrectly_WithMockTts()
    {
        using var scope = _factory.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetService<IVivaSessionOrchestrator>();
        Assert.NotNull(orchestrator);

        var tts = scope.ServiceProvider.GetService<ITextToSpeechService>();
        Assert.NotNull(tts);
        Assert.IsType<MockTextToSpeechService>(tts);
    }

    [Fact]
    public async Task Get_MockQuestionAudio_Returns200AndAudioMpeg()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/audio/mock-question.mp3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Content.Headers.ContentType);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType.MediaType);
    }
}
