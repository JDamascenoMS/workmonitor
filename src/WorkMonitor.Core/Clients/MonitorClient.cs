using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Models;
using WorkMonitor.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace WorkMonitor.Core.Clients
{
    public class MonitorClient
    {
        private static readonly HttpClient SharedHttpClient = new();

        private readonly string _accessToken;
        private readonly HttpClient _httpClient;
        private readonly IRecentMentionsProvider _recentMentionsProvider;

        public MonitorClient(string accessToken)
            : this(
                accessToken,
                SharedHttpClient,
                new AzureDevOpsRecentMentionsProvider())
        {
        }

        public MonitorClient(
            string accessToken,
            HttpClient httpClient,
            IRecentMentionsProvider? recentMentionsProvider = null)
        {
            _accessToken = string.IsNullOrWhiteSpace(accessToken)
                ? throw new ArgumentException("An Azure DevOps access token is required.", nameof(accessToken))
                : accessToken;
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _recentMentionsProvider =
                recentMentionsProvider ?? new AzureDevOpsRecentMentionsProvider();
        }
        public async Task<WiqlResponse> GetWiqlIdResponse(
            string queryId,
            CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(queryId, out _))
            {
                throw new ArgumentException(
                    "Saved query id must be a GUID.",
                    nameof(queryId));
            }

            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/wiql/{Uri.EscapeDataString(queryId)}?{Constants.ApiVersion}";
            var response = await GenericCallGet(Url, cancellationToken);
            var ret = JsonConvert.DeserializeObject<WiqlResponse>(response);
            return ret;
        }

        public async Task<(List<InternalItem> Items, int TotalCount)>
            GetWorkItemsFromSavedQuery(
                string queryId,
                int maxResults = 20,
                CancellationToken cancellationToken = default)
        {
            if (maxResults is < 1 or > 200)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxResults),
                    "maxResults must be between 1 and 200.");
            }

            var wiqlResponse = await GetWiqlIdResponse(
                queryId,
                cancellationToken);

            var allIds = ExtractWorkItemIds(wiqlResponse);
            if (allIds.Count == 0)
            {
                return (new List<InternalItem>(), 0);
            }

            var selectedIds = allIds.Take(maxResults).ToList();
            var response = await GetWorkItems(selectedIds, cancellationToken);

            if (response?.value == null)
            {
                return (new List<InternalItem>(), allIds.Count);
            }

            var lookup = response.value.ToDictionary(x => x.id);
            var items = selectedIds
                .Where(lookup.ContainsKey)
                .Select(id => lookup[id])
                .ToList();
            return (items, allIds.Count);
        }

        public async Task<WorkItemResponse> GetWorkItems(
            List<int> ListIds,
            CancellationToken cancellationToken = default)
        {
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/workitemsbatch?{Constants.ApiVersion}";
            var body = new WorkItemRequest(ListIds);
            var response = await GenericCallPost(Url, body, cancellationToken);
            var ret = JsonConvert.DeserializeObject<WorkItemResponse>(response);
            return ret;
        }

        public async Task<WorkItemResponse> GetWorkItemsList(List<int> ListIds)
        {
            string ids = string.Empty;
            ListIds.ForEach(x => ids += x.ToString()+";");
            ids = "2264647";
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/workitems?ids={ids}&{Constants.ApiVersion}";
            var response = await GenericCallGet(Url);
            var ret = JsonConvert.DeserializeObject<WorkItemResponse>(response);
            return ret;
        }

        public async Task<CommentsResponse> GetCommentsByWorkItem(
            int WorkItemId,
            CancellationToken cancellationToken = default)
        {
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/workItems/{WorkItemId}/comments?{Constants.ApiVersion}-preview.4";
            var response = await GenericCallGet(Url, cancellationToken);
            var ret = JsonConvert.DeserializeObject<CommentsResponse>(response);
            return ret;
        }

        public async Task<CommentsResponse> GetRecentCommentsByWorkItem(
            int workItemId,
            int maxResults = 3,
            CancellationToken cancellationToken = default)
        {
            if (maxResults is < 1 or > 20)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxResults),
                    "maxResults must be between 1 and 20.");
            }

            string url =
                $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}" +
                $"/_apis/wit/workItems/{workItemId}/comments" +
                $"?$top={maxResults}&order=desc&api-version=7.1-preview.4";
            var response = await GenericCallGet(url, cancellationToken);
            return JsonConvert.DeserializeObject<CommentsResponse>(response)
                ?? new CommentsResponse();
        }

        public async Task<CommentScanResult> GetCommentsForScanning(
            int workItemId,
            int maxComments = 500,
            CancellationToken cancellationToken = default)
        {
            if (maxComments is < 1 or > 1000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxComments),
                    "maxComments must be between 1 and 1000.");
            }

            const int pageSize = 100;
            string? continuationToken = null;
            var comments = new List<Comment>();
            var totalCount = 0;

            do
            {
                var remaining = maxComments - comments.Count;
                var top = Math.Min(pageSize, remaining);
                var continuation =
                    string.IsNullOrWhiteSpace(continuationToken)
                        ? string.Empty
                        : $"&continuationToken={Uri.EscapeDataString(continuationToken)}";
                var url =
                    $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}" +
                    $"/_apis/wit/workItems/{workItemId}/comments" +
                    $"?$top={top}&order=desc{continuation}&api-version=7.1-preview.4";
                using var request = CreateRequest(HttpMethod.Get, url);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var page = JsonConvert.DeserializeObject<CommentsResponse>(
                    responseBody)
                    ?? new CommentsResponse();
                totalCount = Math.Max(totalCount, page.totalCount);
                comments.AddRange(page.comments ?? Enumerable.Empty<Comment>());
                continuationToken =
                    response.Headers.TryGetValues(
                        "x-ms-continuationtoken",
                        out var values)
                        ? values.FirstOrDefault()
                        : page.continuationToken;
            }
            while (
                comments.Count < maxComments &&
                !string.IsNullOrWhiteSpace(continuationToken));

            return new CommentScanResult(
                totalCount == 0 ? comments.Count : totalCount,
                !string.IsNullOrWhiteSpace(continuationToken) ||
                    (totalCount > 0 && comments.Count < totalCount),
                comments);
        }

        public async Task<WorkItemResponse> GetWorkItemsFromWiqlQuery(
            string wiqlQuery,
            int? top = null,
            CancellationToken cancellationToken = default)
        {
            var wiqlResponse = await ExecuteWiqlQuery(wiqlQuery, top, cancellationToken);

            if (wiqlResponse?.workItems == null || !wiqlResponse.workItems.Any())
            {
                return new WorkItemResponse();
            }

            var workItemIds = wiqlResponse.workItems.Select(wi => wi.id).ToList();
            var response = await GetWorkItems(workItemIds, cancellationToken);

            // Preserve WIQL order — batch API does not guarantee it
            if (response?.value != null)
            {
                var lookup = response.value.ToDictionary(x => x.id);
                response.value = workItemIds
                    .Where(id => lookup.ContainsKey(id))
                    .Select(id => lookup[id])
                    .ToList();
            }

            return response;
        }

        public async Task<WorkItemResponse> GetWorkItemsFromWiqlQueryList(List<string> fields, List<string> keys, List<string> values)
        {
            string query = @$"SELECT {string.Join(", ", fields)} 
                                FROM WorkItems WHERE {string.Join(" AND ", keys.Zip(values, (k, v) => $"{k} = '{v}'"))}";
            return await GetWorkItemsFromWiqlQuery(query, top: Constants.LimitWorkItems);
        }

        public async Task<WorkItemResponse> GetWorkItemsBetweenDates(DateTime dtFrom, DateTime dtTo, string projectArea, string WorkItemType)
        {
            List<string> fields = 
                new List<string>() { "System.Id", "System.Title", "System.CreatedDate", "System.AreaPath", "System.WorkItemType" };
            List<string> keys = new List<string>() { "System.Id", "System.Title", "System.CreatedDate", "System.AreaPath", "System.WorkItemType" };
            List<string> values = new List<string>() { "System.CreatedDate", "System.CreatedDate", "System.AreaPath", "System.WorkItemType" };

            string AreaPath = !Constants.ProjectAreaPath.TryGetValue(projectArea, out string val) ? throw new Exception("Project area not found in constants.") : val;

            string query = @$"SELECT {string.Join(", ", fields)} 
                                FROM WorkItems WHERE {string.Join(" AND ", keys.Zip(values, (k, v) => $"{k} = '{v}'"))} 
                                AND System.CreatedDate >= '{dtFrom.ToString("yyyy-MM-ddTHH:mm:ssZ")}' AND System.CreatedDate <= '{dtTo.ToString("yyyy-MM-ddTHH:mm:ssZ")}'

                                AND System.AreaPath UNDER {AreaPath} And System.WorkItemType = {WorkItemType}";

            return await GetWorkItemsFromWiqlQuery(query, top: Constants.LimitWorkItems);

        }

        public async Task<WorkItemResponse> GetTopByPriority(string projectArea, string workItemType = "Bug", string assignedTo = null)
        {
            string areaPath = !Constants.ProjectAreaPath.TryGetValue(projectArea, out string val)
                ? throw new Exception("Project area not found in constants.")
                : val;
            workItemType = AzureDevOpsQueryValues.NormalizeWorkItemType(workItemType);
            areaPath = AzureDevOpsQueryValues.EscapeLiteral(areaPath);

            string assigneeFilter = assignedTo == "__none__"
                ? "AND [System.AssignedTo] = ''"
                : !string.IsNullOrEmpty(assignedTo)
                    ? $"AND [System.AssignedTo] = '{AzureDevOpsQueryValues.EscapeLiteral(assignedTo)}'"
                    : "";

            string query = @$"SELECT [System.Id], [System.Title], [System.State], 
                                [Microsoft.VSTS.Common.Priority], [System.CreatedDate], 
                                [System.AreaPath], [System.AssignedTo]
                              FROM WorkItems 
                              WHERE [System.WorkItemType] = '{workItemType}' 
                                AND [System.AreaPath] UNDER '{areaPath}'
                                AND [System.State] <> 'Closed'
                                {assigneeFilter}
                              ORDER BY [Microsoft.VSTS.Common.Priority] ASC";

            return await GetWorkItemsFromWiqlQuery(query, top: 100);
        }

        public async Task<WorkItemResponse> GetTopBugsByPriority(string projectArea, string assignedTo = null)
        {
            return await GetTopByPriority(projectArea, "Bug", assignedTo);
        }

        public async Task<WorkItemResponse> GetTopFeedbackByPriority(string projectArea, string assignedTo = null)
        {
            string areaPath = !Constants.ProjectAreaPath.TryGetValue(projectArea, out string val)
                ? throw new Exception("Project area not found in constants.")
                : val;

            string assigneeFilter = assignedTo == "__none__"
                ? "AND [System.AssignedTo] = ''"
                : !string.IsNullOrEmpty(assignedTo)
                    ? $"AND [System.AssignedTo] = '{assignedTo}'"
                    : "";

            string query = @$"SELECT [System.Id], [System.Title], [System.State], 
                                [Microsoft.VSTS.Common.Priority], [System.CreatedDate], 
                                [System.AreaPath], [System.AssignedTo]
                              FROM WorkItems 
                              WHERE [System.WorkItemType] = 'FeedbackTicket' 
                                AND [System.AreaPath] UNDER '{areaPath}'
                                AND [System.State] <> 'Closed'
                                AND [System.State] <> 'Resolved'
                                AND NOT [System.State] CONTAINS 'Closed'
                                AND NOT [System.State] CONTAINS 'Resolved'
                                {assigneeFilter}
                              ORDER BY [System.CreatedDate] DESC, [Microsoft.VSTS.Common.Priority] ASC";

            return await GetWorkItemsFromWiqlQuery(query, top: 200);
        }

        public async Task<WorkItemResponse> SearchByArea(
            string projectArea,
            string workItemType = "Bug",
            CancellationToken cancellationToken = default)
        {
            string areaPath = !Constants.ProjectAreaPath.TryGetValue(projectArea, out string val)
                ? throw new Exception("Project area not found in constants.")
                : val;
            workItemType = AzureDevOpsQueryValues.NormalizeWorkItemType(workItemType);
            areaPath = AzureDevOpsQueryValues.EscapeLiteral(areaPath);

            string query = @$"SELECT [System.Id], [System.Title], [System.State], 
                                [Microsoft.VSTS.Common.Priority], [System.CreatedDate], 
                                [System.AreaPath], [System.AssignedTo]
                              FROM WorkItems 
                              WHERE [System.WorkItemType] = '{workItemType}' 
                                AND [System.AreaPath] UNDER '{areaPath}'
                                AND [System.State] <> 'Closed'
                              ORDER BY [Microsoft.VSTS.Common.Priority] ASC";

            return await GetWorkItemsFromWiqlQuery(
                query,
                top: 200,
                cancellationToken);
        }

        public async Task<WorkItemResponse> SearchBugsByArea(string projectArea)
        {
            return await SearchByArea(projectArea, "Bug");
        }

        public async Task<List<string>> GetAssigneesByArea(string projectArea, string workItemType = "Bug")
        {
            string areaPath = !Constants.ProjectAreaPath.TryGetValue(projectArea, out string val)
                ? throw new Exception("Project area not found in constants.")
                : val;
            workItemType = AzureDevOpsQueryValues.NormalizeWorkItemType(workItemType);
            areaPath = AzureDevOpsQueryValues.EscapeLiteral(areaPath);

            string query = @$"SELECT [System.Id], [System.AssignedTo]
                              FROM WorkItems 
                              WHERE [System.WorkItemType] = '{workItemType}' 
                                AND [System.AreaPath] UNDER '{areaPath}'
                                AND [System.State] <> 'Closed'";

            var response = await GetWorkItemsFromWiqlQuery(query, top: 200);

            if (response?.value == null)
                return new List<string>();

            return response.value
                .Where(x => x.fields.AssignedTo != null && !string.IsNullOrEmpty(x.fields.AssignedTo.displayName))
                .Select(x => x.fields.AssignedTo.displayName)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }

        public async Task<(List<InternalItem> items, int totalCount)> GetMyDashboardItems(string userName, int top = 10)
        {
            string query = @$"SELECT [System.Id]
                              FROM WorkItems
                              WHERE ([System.AssignedTo] = '{userName}'
                                     OR [System.History] CONTAINS '{userName}')
                                AND [System.State] <> 'Closed'
                                AND ([System.WorkItemType] = 'Bug' OR [System.WorkItemType] = 'FeedbackTicket')
                              ORDER BY [System.ChangedDate] DESC";

            var wiqlResponse = await ExecuteWiqlQuery(query, top: 200);
            if (wiqlResponse?.workItems == null || !wiqlResponse.workItems.Any())
                return (new List<InternalItem>(), 0);

            var allIds = wiqlResponse.workItems.Select(wi => wi.id).ToList();
            var pageIds = allIds.Take(top).ToList();
            var response = await GetWorkItems(pageIds);

            var items = new List<InternalItem>();
            if (response?.value != null)
            {
                var lookup = response.value.ToDictionary(x => x.id);
                items = pageIds.Where(id => lookup.ContainsKey(id)).Select(id => lookup[id]).ToList();
            }

            return (items, allIds.Count);
        }

        public async Task<(List<InternalItem> Items, int TotalCount)> GetMyAssignedItems(
            int maxResults = 10,
            string? workItemType = null,
            CancellationToken cancellationToken = default)
        {
            if (maxResults is < 1 or > 50)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxResults),
                    "maxResults must be between 1 and 50.");
            }

            var typeFilter = string.IsNullOrWhiteSpace(workItemType) ||
                             workItemType.Equals("All", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $"AND [System.WorkItemType] = '{AzureDevOpsQueryValues.NormalizeWorkItemType(workItemType)}'";

            string query = @$"SELECT [System.Id]
                              FROM WorkItems
                              WHERE [System.AssignedTo] = @Me
                                AND [System.State] <> 'Closed'
                                AND [System.State] <> 'Resolved'
                                AND NOT [System.State] CONTAINS 'Closed'
                                AND NOT [System.State] CONTAINS 'Resolved'
                                AND NOT [System.State] CONTAINS 'Done'
                                AND NOT [System.State] CONTAINS 'Removed'
                                AND NOT [System.State] CONTAINS 'Completed'
                                {typeFilter}
                              ORDER BY [System.ChangedDate] DESC";

            var wiqlResponse = await ExecuteWiqlQuery(
                query,
                top: null,
                cancellationToken);
            if (wiqlResponse?.workItems == null || !wiqlResponse.workItems.Any())
            {
                return (new List<InternalItem>(), 0);
            }

            var allIds = wiqlResponse.workItems.Select(item => item.id).ToList();
            var selectedIds = allIds.Take(maxResults).ToList();
            var response = await GetWorkItems(selectedIds, cancellationToken);
            if (response?.value == null)
            {
                return (new List<InternalItem>(), allIds.Count);
            }

            var byId = response.value.ToDictionary(item => item.id);
            var ordered = selectedIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
            return (ordered, allIds.Count);
        }

        public async Task<(List<InternalItem> Items, int TotalCount)>
            GetMyMentionedItems(
                int maxResults = 10,
                string? workItemType = null,
                CancellationToken cancellationToken = default)
        {
            if (maxResults is < 1 or > 50)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxResults),
                    "maxResults must be between 1 and 50.");
            }

            var normalizedType = string.IsNullOrWhiteSpace(workItemType) ||
                                 workItemType.Equals(
                                     "All",
                                     StringComparison.OrdinalIgnoreCase)
                ? null
                : AzureDevOpsQueryValues.NormalizeWorkItemType(workItemType);
            var mentions = await _recentMentionsProvider.GetRecentMentionsAsync(
                _accessToken,
                cancellationToken);
            var mentionIds = mentions
                .Select(mention => mention.WorkItemId)
                .Distinct()
                .Take(200)
                .ToList();
            if (mentionIds.Count == 0)
            {
                return (new List<InternalItem>(), 0);
            }

            var response = await GetWorkItems(mentionIds, cancellationToken);
            if (response?.value == null)
            {
                return (new List<InternalItem>(), 0);
            }

            var byId = response.value
                .Where(item =>
                    IsActiveState(item.fields.State) &&
                    (normalizedType == null ||
                     item.fields.WorkItemType.Equals(
                         normalizedType,
                         StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(item => item.id);
            var orderedIds = mentions
                .Select(mention => mention.WorkItemId)
                .Where(byId.ContainsKey)
                .Distinct()
                .ToList();
            var ordered = orderedIds
                .Take(maxResults)
                .Select(id => byId[id])
                .ToList();
            return (ordered, orderedIds.Count);
        }

        public async Task<IReadOnlyList<PullRequestReviewItem>>
            GetPullRequestsRequiringMyReview(
                int maxResults = 20,
                CancellationToken cancellationToken = default)
        {
            if (maxResults is < 1 or > 50)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxResults),
                    "maxResults must be between 1 and 50.");
            }

            var connectionUrl =
                $"{Constants.ApiUrlBase}/{Constants.Organization}/_apis/connectionData" +
                "?connectOptions=1&lastChangeId=-1&lastChangeId64=-1&api-version=7.1-preview.1";
            var connectionJson = await GenericCallGet(
                connectionUrl,
                cancellationToken);
            var connection = JsonConvert.DeserializeObject<AzureDevOpsConnectionData>(
                connectionJson);
            var reviewerId = connection?.AuthenticatedUser?.Id;
            if (string.IsNullOrWhiteSpace(reviewerId))
            {
                throw new InvalidOperationException(
                    "Azure DevOps did not return the authenticated user's identity.");
            }

            const int pageSize = 100;
            var pending = new List<PullRequestReviewItem>();
            for (var page = 0; ; page++)
            {
                var pullRequestUrl =
                    $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}" +
                    "/_apis/git/pullrequests" +
                    $"?searchCriteria.reviewerId={Uri.EscapeDataString(reviewerId)}" +
                    "&searchCriteria.status=active" +
                    $"&$skip={page * pageSize}" +
                    $"&$top={pageSize}" +
                    $"&{Constants.ApiVersion}";
                var pullRequestJson = await GenericCallGet(
                    pullRequestUrl,
                    cancellationToken);
                var response =
                    JsonConvert.DeserializeObject<AzureDevOpsPullRequestResponse>(
                        pullRequestJson)
                    ?? new AzureDevOpsPullRequestResponse();

                pending.AddRange(response.Value.Select(pullRequest =>
                {
                    var reviewer = pullRequest.Reviewers.FirstOrDefault(item =>
                        item.Id?.Equals(
                            reviewerId,
                            StringComparison.OrdinalIgnoreCase) == true);
                    var repositoryName =
                        pullRequest.Repository?.Name ?? "unknown";
                    var webUrl =
                        $"https://dev.azure.com/{Constants.Organization}/" +
                        $"{Constants.Project}/_git/{Uri.EscapeDataString(repositoryName)}" +
                        $"/pullrequest/{pullRequest.PullRequestId}";

                    return new PullRequestReviewItem(
                        pullRequest.PullRequestId,
                        pullRequest.Title ?? string.Empty,
                        repositoryName,
                        TrimGitRef(pullRequest.SourceRefName),
                        TrimGitRef(pullRequest.TargetRefName),
                        pullRequest.CreatedBy?.ProviderDisplayName ??
                            pullRequest.CreatedBy?.DisplayName ??
                            pullRequest.CreatedBy?.CustomDisplayName ??
                            "unknown",
                        pullRequest.CreationDate,
                        reviewer?.Vote,
                        reviewer?.IsRequired ?? false,
                        webUrl);
                }).Where(item => item.MyVote is null or 0));

                if (pending.Count >= maxResults ||
                    response.Value.Count < pageSize)
                {
                    break;
                }
            }

            return pending
                .OrderByDescending(item => item.CreatedAt)
                .Take(maxResults)
                .ToList();
        }

        public async Task<RecentActivityResult> GetMyRecentActivity(
            int days = 14,
            int maxResults = 10,
            int commentsPerItem = 1,
            CancellationToken cancellationToken = default)
        {
            if (days is < 1 or > 90)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(days),
                    "days must be between 1 and 90.");
            }

            if (maxResults is < 1 or > 10)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxResults),
                    "maxResults must be between 1 and 10.");
            }

            if (commentsPerItem is < 0 or > 2)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(commentsPerItem),
                    "commentsPerItem must be between 0 and 2.");
            }

            var stateAndDateFilter = @$"
                                AND [System.ChangedDate] >= @Today - {days}
                                AND [System.State] <> 'Closed'
                                AND [System.State] <> 'Resolved'
                                AND NOT [System.State] CONTAINS 'Closed'
                                AND NOT [System.State] CONTAINS 'Resolved'
                                AND NOT [System.State] CONTAINS 'Done'
                                AND NOT [System.State] CONTAINS 'Removed'
                                AND NOT [System.State] CONTAINS 'Completed'";
            var assignedQuery = @$"SELECT [System.Id]
                                   FROM WorkItems
                                   WHERE [System.AssignedTo] = @Me
                                   {stateAndDateFilter}
                                   ORDER BY [System.ChangedDate] DESC";
            var assignedTask = ExecuteWiqlQuery(
                assignedQuery,
                top: 50,
                cancellationToken);
            var mentionedTask = _recentMentionsProvider.GetRecentMentionsAsync(
                _accessToken,
                cancellationToken);
            await Task.WhenAll(assignedTask, mentionedTask);
            var assignedResponse = await assignedTask;
            var mentionedResponse = await mentionedTask;
            var mentionCutoff = DateTimeOffset.UtcNow.Date.AddDays(-days);
            var recentMentions = mentionedResponse
                .Where(mention =>
                    mention.MentionedAt.HasValue &&
                    mention.MentionedAt.Value >= mentionCutoff)
                .ToList();
            var allIds = new[]
            {
                assignedResponse.workItems?
                    .Select(item => item.id)
                    .ToList() ?? new List<int>(),
                recentMentions
                    .Select(item => item.WorkItemId)
                    .Take(50)
                    .ToList()
            }
            .SelectMany(items => items)
            .Distinct()
            .Take(100)
            .ToList();
            if (allIds.Count == 0)
            {
                return new RecentActivityResult(
                    0,
                    false,
                    Array.Empty<RecentWorkItemActivity>());
            }

            var candidateWindowTruncated =
                (assignedResponse.workItems?.Count ?? 0) >= 50 ||
                recentMentions.Count > 50;
            var response = await GetWorkItems(allIds, cancellationToken);
            if (response?.value == null)
            {
                return new RecentActivityResult(
                    allIds.Count,
                    candidateWindowTruncated,
                    Array.Empty<RecentWorkItemActivity>());
            }

            var orderedItems = response.value
                .Where(item =>
                    IsActiveState(item.fields.State) &&
                    ParseDate(item.fields.ChangedDate) >= mentionCutoff)
                .OrderByDescending(item => ParseDate(item.fields.ChangedDate))
                .Take(maxResults)
                .ToList();
            var commentTasks = orderedItems.Select(async item =>
            {
                if (commentsPerItem == 0)
                {
                    return Array.Empty<RecentCommentActivity>();
                }

                var comments = await GetRecentCommentsByWorkItem(
                    item.id,
                    commentsPerItem,
                    cancellationToken);
                return comments.comments?
                    .Where(comment => !comment.isDeleted)
                    .OrderByDescending(comment => ParseDate(comment.modifiedDate))
                    .Take(commentsPerItem)
                    .Select(comment => new RecentCommentActivity(
                        comment.commentId,
                        Truncate(comment.text, 500),
                        ParseDate(comment.modifiedDate)))
                    .ToArray()
                    ?? Array.Empty<RecentCommentActivity>();
            });
            var commentsByItem = await Task.WhenAll(commentTasks);

            var activity = orderedItems
                .Select((item, index) => new RecentWorkItemActivity(
                    item.id,
                    Truncate(item.fields.Title, 300),
                    item.fields.WorkItemType ?? string.Empty,
                    item.fields.State ?? string.Empty,
                    item.fields.Priority,
                    item.fields.AreaPath ?? string.Empty,
                    item.fields.AssignedTo?.displayName ?? "Unassigned",
                    ParseDate(item.fields.ChangedDate),
                    commentsByItem[index]))
                .ToList();
            return ReadResultBudget.FitCollection<
                RecentWorkItemActivity,
                RecentActivityResult>(
                activity,
                (items, truncated) => new RecentActivityResult(
                    allIds.Count,
                    candidateWindowTruncated,
                    items,
                    truncated));
        }

        public async Task<(List<InternalItem> items, int totalCount)> GetMyItems(string userName, string workItemType, string mode, int page, int pageSize)
        {
            string whereClause = mode == "mentioned"
                ? $"[System.History] CONTAINS '{userName}'"
                : $"[System.AssignedTo] = '{userName}'";

            string query = @$"SELECT [System.Id]
                              FROM WorkItems
                              WHERE {whereClause}
                                AND [System.State] <> 'Closed'
                                AND [System.WorkItemType] = '{workItemType}'
                              ORDER BY [System.ChangedDate] DESC";

            var wiqlResponse = await ExecuteWiqlQuery(query, top: 500);
            if (wiqlResponse?.workItems == null || !wiqlResponse.workItems.Any())
                return (new List<InternalItem>(), 0);

            var allIds = wiqlResponse.workItems.Select(wi => wi.id).ToList();
            var pageIds = allIds.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            if (!pageIds.Any())
                return (new List<InternalItem>(), allIds.Count);

            var response = await GetWorkItems(pageIds);

            var items = new List<InternalItem>();
            if (response?.value != null)
            {
                var lookup = response.value.ToDictionary(x => x.id);
                items = pageIds.Where(id => lookup.ContainsKey(id)).Select(id => lookup[id]).ToList();
            }

            return (items, allIds.Count);
        }

        public async Task<WiqlResponse> ExecuteWiqlQuery(
            string wiqlQuery,
            int? top = null,
            CancellationToken cancellationToken = default)
        {
            string topParam = top.HasValue ? $"&$top={top.Value}" : "";
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/wiql?{Constants.ApiVersion}{topParam}";
            var body = new { query = wiqlQuery };
            var response = await GenericCallPost(Url, body, cancellationToken);
            var ret = JsonConvert.DeserializeObject<WiqlResponse>(response);
            return ret;
        }

        public async Task<VsDivCommentResponse?> GetVSDivCommentsByPostId(
            string PostId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                string Url = $"https://sendvsfeedback2.azurewebsites.net/api/detailsV3/rootpost?Id={Uri.EscapeDataString(PostId)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, Url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonConvert.DeserializeObject<VsDivCommentResponse>(responseBody);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Posts a comment on a work item. Returns the raw API response body.
        /// Used by the Agent feature.
        /// </summary>
        public async Task<string> AddCommentToWorkItem(
            int workItemId,
            string text,
            CancellationToken cancellationToken = default)
        {
            string url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/workItems/{workItemId}/comments?{Constants.ApiVersion}-preview.4";
            var body = new { text };
            cancellationToken.ThrowIfCancellationRequested();
            return await GenericCallPost(url, body, CancellationToken.None);
        }

        public async Task<string> GenericCallGet(
            string Url,
            CancellationToken cancellationToken = default)
        {
            using var request = CreateRequest(HttpMethod.Get, Url);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();
            return responseBody;
        }

        public async Task<string> GenericCallPost(
            string Url,
            object Body,
            CancellationToken cancellationToken = default)
        {
            using var request = CreateRequest(HttpMethod.Post, Url);
            request.Content = new StringContent(
                JsonConvert.SerializeObject(Body),
                Encoding.UTF8,
                "application/json");
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();
            return responseBody;
        }

        private HttpRequestMessage CreateRequest(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            return request;
        }

        private static string TrimGitRef(string? reference)
            => reference?.StartsWith("refs/heads/", StringComparison.Ordinal) == true
                ? reference["refs/heads/".Length..]
                : reference ?? string.Empty;

        private static bool IsActiveState(string? state)
        {
            if (string.IsNullOrWhiteSpace(state))
            {
                return false;
            }

            return !new[]
            {
                "Closed",
                "Resolved",
                "Done",
                "Removed",
                "Completed"
            }.Any(terminal =>
                state.Contains(terminal, StringComparison.OrdinalIgnoreCase));
        }

        private static List<int> ExtractWorkItemIds(WiqlResponse? response)
        {
            if (response == null)
            {
                return new List<int>();
            }

            if (response.workItems is { Count: > 0 })
            {
                return response.workItems
                    .Select(item => item.id)
                    .Distinct()
                    .ToList();
            }

            if (response.workItemRelations is { Count: > 0 })
            {
                return response.workItemRelations
                    .SelectMany(relation => new[]
                    {
                        relation.source?.id ?? 0,
                        relation.target?.id ?? 0
                    })
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
            }

            if (response.queryType?.Equals(
                    "tree",
                    StringComparison.OrdinalIgnoreCase) == true ||
                response.queryType?.Equals(
                    "oneHop",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                return new List<int>();
            }

            if (!string.Equals(
                    response.queryType,
                    "flat",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Saved query type '{response.queryType}' did not return work-item relations.");
            }

            return new List<int>();
        }

        private static DateTimeOffset? ParseDate(string? value)
            => DateTimeOffset.TryParse(value, out var parsed)
                ? parsed
                : null;

        private static string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value[..maxLength] + "\n[truncated]";
        }
    }
}
