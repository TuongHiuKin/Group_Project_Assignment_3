using AIVES.BLL.Interfaces.Ai;
using AIVES.BLL.Models.Ai;
using AIVES.BLL.Services.Ai;
using AIVES.BLL.Services.Ai.Adapters;
using Xunit;

namespace AIVES.Tests.Ai;

public sealed class AiProviderRegistryTests
{
    [Fact]
    public void Registry_Resolves_Known_Providers_Case_Insensitively()
    {
        var registry = new AiProviderRegistry([
            new OpenAiProviderAdapter(),
            new GeminiProviderAdapter(),
            new OpenAiCompatibleProviderAdapter()
        ]);

        var openAi = registry.GetAdapter("openai");
        var gemini = registry.GetAdapter("GEMINI");
        var compatible = registry.GetAdapter("OpenAiCompatible");

        Assert.NotNull(openAi);
        Assert.Equal(AiProviderKinds.OpenAi, openAi.Kind);

        Assert.NotNull(gemini);
        Assert.Equal(AiProviderKinds.Gemini, gemini.Kind);

        Assert.NotNull(compatible);
        Assert.Equal(AiProviderKinds.OpenAiCompatible, compatible.Kind);
    }

    [Fact]
    public void Registry_Returns_Null_For_Unknown_Kind()
    {
        var registry = new AiProviderRegistry([
            new OpenAiProviderAdapter()
        ]);

        var result = registry.GetAdapter("AnthropicUnknown");
        Assert.Null(result);
    }

    [Fact]
    public void Registry_GetAllAdapters_Returns_All_Registered()
    {
        var registry = new AiProviderRegistry([
            new OpenAiProviderAdapter(),
            new GeminiProviderAdapter()
        ]);

        var all = registry.GetAllAdapters();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.Kind == AiProviderKinds.OpenAi);
        Assert.Contains(all, a => a.Kind == AiProviderKinds.Gemini);
    }
}
