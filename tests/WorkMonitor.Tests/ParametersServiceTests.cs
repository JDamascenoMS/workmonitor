using Microsoft.Extensions.Configuration;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Tests;

public sealed class ParametersServiceTests
{
    [Fact]
    public void LoadsSavedQueriesFromConfigurationWhenFileIsAbsent()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "workmonitor-parameters-tests",
            Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureDevOps:SavedQueries:0:name"] = "My query",
                ["AzureDevOps:SavedQueries:0:queryId"] =
                    "fa66def3-ecef-49ba-bd72-c95abf0c9496"
            })
            .Build();

        try
        {
            var service = new ParametersService(directory, configuration);
            var query = Assert.Single(service.GetSavedQueries());
            Assert.Equal("My query", query.Key);
            Assert.Equal(
                "fa66def3-ecef-49ba-bd72-c95abf0c9496",
                query.Value);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
