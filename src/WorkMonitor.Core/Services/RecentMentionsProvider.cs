using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;
using WorkMonitor.Core.Models;

namespace WorkMonitor.Core.Services;

public interface IRecentMentionsProvider
{
    Task<IReadOnlyList<RecentMentionReference>> GetRecentMentionsAsync(
        string accessToken,
        CancellationToken cancellationToken);
}

public sealed class AzureDevOpsRecentMentionsProvider : IRecentMentionsProvider
{
    public async Task<IReadOnlyList<RecentMentionReference>> GetRecentMentionsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var connection = new VssConnection(
            new Uri("https://dev.azure.com/devdiv"),
            new VssOAuthAccessTokenCredential(accessToken));
        using var client = await connection.GetClientAsync<WorkItemTrackingHttpClient>(
            cancellationToken);
        var mentions = await client.GetRecentMentionsAsync(
            userState: null,
            cancellationToken);

        return mentions
            .Select(mention => new RecentMentionReference(
                mention.Id,
                mention.MentionedDateField))
            .OrderByDescending(mention => mention.MentionedAt)
            .ToList();
    }
}
