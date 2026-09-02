using System.Net;
using System.Text;
using WorkMonitor.Core.Clients;

namespace WorkMonitor.Tests;

public sealed class MonitorClientTests
{
    [Fact]
    public async Task MyAssignedItemsUsesAuthenticatedUserMacro()
    {
        var handler = new CaptureHandler();
        var client = new MonitorClient(
            "test-token",
            new HttpClient(handler));

        var result = await client.GetMyAssignedItems(
            maxResults: 10,
            cancellationToken: CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Contains(
            "[System.AssignedTo] = @Me",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "'@Me'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Closed'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Resolved'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Done'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Removed'",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT [System.State] CONTAINS 'Completed'",
            handler.RequestBody,
            StringComparison.Ordinal);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"workItems":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
