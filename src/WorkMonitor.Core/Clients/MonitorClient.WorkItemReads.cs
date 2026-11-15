using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkMonitor.Core.Models;

namespace WorkMonitor.Core.Clients;

public partial class MonitorClient
{
    public async Task<InternalItem> GetRequiredWorkItem(
        int id, bool summaryOnly = false, CancellationToken cancellationToken = default)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "id must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        var body = new WorkItemRequest([id]);
        if (summaryOnly)
            body.fields = ["System.Id", "System.Title", "System.State", "System.AssignedTo", "Microsoft.VSTS.Common.Priority"];

        string json;
        try
        {
            json = await GenericCallPost($"{WorkItemApiBase}/workitemsbatch?api-version=7.1", body, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            throw new HttpRequestException(
                $"Work item {id} could not be read. Verify the id and your Azure DevOps access ({(int)ex.StatusCode.Value}).",
                null, ex.StatusCode);
        }

        try
        {
            var response = JsonConvert.DeserializeObject<JObject>(json,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) ?? throw InvalidWorkItem();
            if (response["value"] is not JArray values)
                throw InvalidWorkItem();
            if (values.Count == 0)
                throw new KeyNotFoundException($"Work item {id} was not found or is not accessible.");
            if (values.Count != 1 || values[0] is not JObject item ||
                item["id"]?.Type != JTokenType.Integer || item.Value<int>("id") != id ||
                item["rev"]?.Type != JTokenType.Integer || item.Value<int>("rev") <= 0 ||
                item["fields"] is not JObject fields ||
                fields["System.Title"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(fields.Value<string>("System.Title")) ||
                fields["System.State"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(fields.Value<string>("System.State")))
                throw InvalidWorkItem();
            if (fields["Microsoft.VSTS.Common.Priority"] is { Type: not JTokenType.Null } priority &&
                (priority.Type != JTokenType.Integer || priority.Value<int>() < 0))
                throw InvalidWorkItem();
            foreach (var name in new[] { "System.Description", "System.Tags", "Microsoft.DevDiv.DeveloperCommunityId" })
                if (fields[name] is { Type: not (JTokenType.Null or JTokenType.String) })
                    throw InvalidWorkItem();
            if (fields["System.AssignedTo"] is { Type: not JTokenType.Null } assigned)
            {
                if (assigned is not JObject)
                    throw InvalidWorkItem();
                foreach (var name in new[] { "displayName", "uniqueName" })
                    if (assigned[name] is { Type: not (JTokenType.Null or JTokenType.String) })
                        throw InvalidWorkItem();
                if (string.IsNullOrWhiteSpace(assigned.Value<string>("displayName")) &&
                    string.IsNullOrWhiteSpace(assigned.Value<string>("uniqueName")))
                    throw InvalidWorkItem();
            }

            var readPriority = fields.Value<int?>("Microsoft.VSTS.Common.Priority");
            if (!readPriority.HasValue)
                fields.Remove("Microsoft.VSTS.Common.Priority");
            var result = item.ToObject<InternalItem>() ?? throw InvalidWorkItem();
            // Priority is not defined by every work-item process; absence is not priority zero.
            result.ReadPriority = readPriority;
            return result;
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or FormatException)
        {
            throw InvalidWorkItem();
        }
    }

    public async Task<WorkItemReadDiscussion> GetWorkItemReadDiscussion(
        int id, CancellationToken cancellationToken = default, int? maxResults = 20)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "id must be positive.");
        if (maxResults is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(maxResults));
        var top = maxResults.HasValue ? $"$top={maxResults}&order=desc&" : "";
        var url = $"{WorkItemApiBase}/workItems/{id}/comments?{top}api-version=7.1-preview.4";
        using var request = CreateRequest(HttpMethod.Get, url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            var root = JsonConvert.DeserializeObject<JObject>(json,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None }) ?? throw InvalidDiscussion();
            if (root["comments"] is not JArray comments ||
                root["totalCount"] is { Type: not (JTokenType.Integer or JTokenType.Null) })
                throw InvalidDiscussion();
            var total = root.Value<int?>("totalCount");
            if (total < comments.Count)
                throw InvalidDiscussion();
            foreach (var comment in comments)
            {
                if (comment is not JObject ||
                    (comment["text"]?.Type != JTokenType.String && comment.Value<bool?>("isDeleted") != true) ||
                    comment["modifiedDate"] is { Type: not (JTokenType.String or JTokenType.Null) })
                    throw InvalidDiscussion();
                if (comment["workItemId"] is { } owner &&
                    (owner.Type != JTokenType.Integer || owner.Value<int>() != id))
                    throw InvalidDiscussion();
            }
            var continuation = response.Headers.Contains("x-ms-continuationtoken") ||
                !string.IsNullOrWhiteSpace(root.Value<string>("continuationToken")) ||
                !string.IsNullOrWhiteSpace(root.Value<string>("nextPage"));
            var retained = comments.Take(maxResults ?? int.MaxValue).Select(comment => comment.ToObject<Comment>()!).ToArray();
            return new WorkItemReadDiscussion(
                retained, total, comments.Count, continuation || !total.HasValue || total > retained.Length);
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or FormatException or ArgumentException)
        {
            throw InvalidDiscussion();
        }
    }

    private static VsDivCommentResponse ParseRequiredCustomerDiscussion(string json)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<JObject>(json,
                new JsonSerializerSettings { DateParseHandling = DateParseHandling.None });
            if (root?["children"] is not JArray comments)
                throw InvalidCustomerDiscussion();
            foreach (var name in new[] { "title", "content" })
                if (root[name] is { Type: not (JTokenType.Null or JTokenType.String) })
                    throw InvalidCustomerDiscussion();
            foreach (var comment in comments)
            {
                if (comment is not JObject || (comment["text"] == null && comment["description"] == null))
                    throw InvalidCustomerDiscussion();
                foreach (var name in new[] { "text", "description" })
                    if (comment[name] is { Type: not (JTokenType.Null or JTokenType.String) })
                        throw InvalidCustomerDiscussion();
                if (comment["author"] is { Type: not JTokenType.Null } author &&
                    (author is not JObject || author["name"] is { Type: not (JTokenType.Null or JTokenType.String) }))
                    throw InvalidCustomerDiscussion();
            }
            return root.ToObject<VsDivCommentResponse>() ?? throw InvalidCustomerDiscussion();
        }
        catch (Exception ex) when (ex is JsonException or OverflowException or FormatException or ArgumentException)
        {
            throw InvalidCustomerDiscussion();
        }
    }

    private static InvalidOperationException InvalidWorkItem()
        => new("Azure DevOps returned malformed work-item data for the requested id.");

    private static InvalidOperationException InvalidDiscussion()
        => new("Azure DevOps returned malformed work-item discussion data.");

    private static InvalidOperationException InvalidCustomerDiscussion()
        => new("Customer discussion returned malformed data.");
}

public sealed record WorkItemReadDiscussion(
    IReadOnlyList<Comment> Comments, int? TotalCount, int SourceReturnedCount, bool IsPartial);
