using WorkMonitor.Agent;

namespace WorkMonitor.Tests;

public sealed class AzureOpenAIServiceTests
{
    [Fact]
    public void IgnoresEmptyStreamingToolArgumentFragment()
    {
        var fragment = AzureOpenAIService.DecodeFunctionArgumentsUpdate(
            BinaryData.Empty);

        Assert.Null(fragment);
    }

    [Fact]
    public void DecodesNonEmptyStreamingToolArgumentFragment()
    {
        var fragment = AzureOpenAIService.DecodeFunctionArgumentsUpdate(
            BinaryData.FromString("""{"query":"assigned to me"}"""));

        Assert.Equal("""{"query":"assigned to me"}""", fragment);
    }
}
