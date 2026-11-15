using WorkMonitor.Core.Models;

namespace WorkMonitor.Core.Clients;

public partial class MonitorClient
{
    public async Task<FeedbackCandidateWindow> GetFeedbackThemeCandidates(
        string? areaPath,
        string? queryId,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        if (maxResults is < 1 or > 200)
            throw new ArgumentOutOfRangeException(nameof(maxResults), "maxResults must be between 1 and 200.");
        if ((areaPath == null) == (queryId == null))
            throw new ArgumentException("Specify exactly one area path or saved query id.");

        cancellationToken.ThrowIfCancellationRequested();
        var response = queryId != null
            ? await GetWiqlIdResponse(queryId, cancellationToken, top: maxResults + 1)
            : await ExecuteWiqlQuery(BuildFeedbackQueryForAreaPath(areaPath!), maxResults + 1, cancellationToken);
        if (response == null)
            throw new InvalidOperationException("Azure DevOps returned no query response.");

        var ids = ExtractWorkItemIds(response);
        var rowCount = Math.Max(response.workItems?.Count ?? 0, response.workItemRelations?.Count ?? 0);
        // A capped relation query can contain more ids than rows; neither is an exact total.
        var queryTruncated = rowCount >= maxResults + 1;
        var truncated = queryTruncated || ids.Count > maxResults;
        int? totalCount = queryTruncated ? null : ids.Count;
        var selectedIds = ids.Take(maxResults).ToArray();
        if (selectedIds.Length == 0)
            return new FeedbackCandidateWindow([], [], ids.Count, totalCount, truncated);

        var batch = await GetWorkItems(selectedIds.ToList(), cancellationToken);
        var values = batch?.value
            ?? throw new InvalidOperationException("Azure DevOps returned no work-item batch.");
        var lookup = values.ToDictionary(item => item.id);
        var items = selectedIds.Where(lookup.ContainsKey).Select(id => lookup[id]).ToArray();
        return new FeedbackCandidateWindow(
            items, selectedIds, ids.Count, totalCount, truncated);
    }
}

public sealed record FeedbackCandidateWindow(
    IReadOnlyList<InternalItem> Items,
    IReadOnlyList<int> SelectedIds,
    int ObservedCount,
    int? TotalCount,
    bool Truncated);
