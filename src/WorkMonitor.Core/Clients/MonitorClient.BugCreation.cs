using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Models;

namespace WorkMonitor.Core.Clients;

public partial class MonitorClient
{
    private static string WorkItemApiBase =>
        $"{Constants.ApiUrlBase.TrimEnd('/')}/{Uri.EscapeDataString(Constants.Organization)}/{Uri.EscapeDataString(Constants.Project)}/_apis/wit";

    public static string FeedbackWorkItemUrl(int id) => $"{WorkItemApiBase}/workitems/{id}";

    private static string WorkItemBrowserUrl(int id) =>
        $"{Constants.ApiUrlBase.TrimEnd('/')}/{Uri.EscapeDataString(Constants.Organization)}/{Uri.EscapeDataString(Constants.Project)}/_workitems/edit/{id}";

    public async Task<CreatedBugResult> CreateBugFromFeedback(
        int feedbackTicketId,
        BugCreationFields bug,
        CancellationToken cancellationToken = default)
    {
        if (feedbackTicketId <= 0)
            throw new ArgumentOutOfRangeException(nameof(feedbackTicketId), "feedbackTicketId must be positive.");
        ArgumentNullException.ThrowIfNull(bug);
        var fields = bug.ToFieldValues();
        var sourceUrl = FeedbackWorkItemUrl(feedbackTicketId);
        string? canonicalProject = null;
        using (var sourceRequest = CreateRequest(
            HttpMethod.Get, $"{sourceUrl}?fields=System.WorkItemType,System.TeamProject&api-version=7.1"))
        {
            var source = ParseBugPreflightDocument(await SendBugPreflightAsync(sourceRequest, cancellationToken));
            if (ReadWorkItemId(source) != feedbackTicketId ||
                source["fields"] is not JObject sourceFields ||
                !string.Equals(ReadString(sourceFields, "System.WorkItemType"), "FeedbackTicket", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ReadString(sourceFields, "System.TeamProject"), Constants.Project, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Work item {feedbackTicketId} is not a verified FeedbackTicket in the configured project. No Bug was created.");
            var canonicalUrl = ReadString(source, "url");
            if (Uri.TryCreate(canonicalUrl, UriKind.Absolute, out var sourceUri))
            {
                var segments = sourceUri.AbsolutePath.Trim('/').Split('/');
                if (segments.Length == 6 && Guid.TryParse(segments[1], out _))
                    canonicalProject = segments[1];
            }
            if (!IsWorkItemApiUrl(canonicalUrl, feedbackTicketId, canonicalProject))
                throw new InvalidOperationException("The source FeedbackTicket's canonical API URL is invalid. No Bug was created.");
            sourceUrl = canonicalUrl!;
        }

        using (var metadataRequest = CreateRequest(
            HttpMethod.Get, $"{WorkItemApiBase}/workitemtypes/Bug/fields?$expand=All&api-version=7.1"))
        {
            var metadata = ParseBugPreflightDocument(await SendBugPreflightAsync(metadataRequest, cancellationToken));
            if (metadata["value"] is not JArray definitions || definitions.Count == 0)
                throw new InvalidOperationException("Bug field metadata is invalid. No Bug was created.");
            foreach (var field in fields)
            {
                var definition = definitions.OfType<JObject>().FirstOrDefault(item =>
                    string.Equals((string?)item["referenceName"], field.Key, StringComparison.Ordinal));
                if (definition == null)
                    throw new ArgumentException($"The Bug process does not expose field '{field.Key}'. No Bug was created.");
                if ((field.Key is "Microsoft.VSTS.Common.Priority" or "Microsoft.DevDiv.Source") &&
                    definition["allowedValues"] is JArray { Count: > 0 } allowed &&
                    !allowed.Any(value => string.Equals(value.ToString(), JToken.FromObject(field.Value).ToString(), StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(
                        $"Bug field '{field.Key}' does not allow '{field.Value}'. " +
                        $"Allowed values: {string.Join(", ", allowed.Select(value => value.ToString()))}. No Bug was created.");
            }
        }

        var patch = new JArray(fields.Select(field => new JObject
        {
            ["op"] = "add",
            ["path"] = $"/fields/{field.Key}",
            ["value"] = JToken.FromObject(field.Value)
        }));
        patch.Add(new JObject
        {
            ["op"] = "add",
            ["path"] = "/relations/-",
            ["value"] = new JObject
            {
                ["rel"] = BugCreationFields.FeedbackRelation,
                ["url"] = sourceUrl
            }
        });
        var payload = patch.ToString(Formatting.None);
        var createUrl = $"{WorkItemApiBase}/workitems/$Bug";

        using (var validation = CreateBugRequest(
            $"{createUrl}?validateOnly=true&$expand=Relations&api-version=7.1", payload))
        {
            // Validate conditional/required process rules and the link without creating anything.
            await SendBugPreflightAsync(validation, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var request = CreateBugRequest(
            $"{createUrl}?$expand=Relations&api-version=7.1", payload);
        int? createdId = null;
        try
        {
            // Creation is non-idempotent: observe the outcome even if the caller cancels.
            using var response = await _httpClient.SendAsync(request, CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                var detail = $"Azure DevOps rejected Bug creation (HTTP {(int)response.StatusCode}): {GetDevOpsErrorDetail(body)}";
                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
                    throw new BugCreationOutcomeUnknownException(feedbackTicketId, detail);
                throw new HttpRequestException(detail, null, response.StatusCode);
            }

            var receipt = JObject.Parse(body);
            createdId = ReadWorkItemId(receipt);
            if (!createdId.HasValue)
                throw new BugCreationOutcomeUnknownException(feedbackTicketId, "Azure DevOps returned no valid created work item id.");
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: null } or OperationCanceledException or JsonException)
        {
            throw new BugCreationOutcomeUnknownException(
                feedbackTicketId, ex.Message, createdId,
                createdId.HasValue ? WorkItemBrowserUrl(createdId.Value) : null, ex);
        }

        var id = createdId.Value;
        var browserUrl = WorkItemBrowserUrl(id);
        try
        {
            using var verification = CreateRequest(
                HttpMethod.Get, $"{FeedbackWorkItemUrl(id)}?$expand=Relations&api-version=7.1");
            var verified = JObject.Parse(await SendBugPreflightAsync(verification, CancellationToken.None));
            var relation = (verified["relations"] as JArray)?.OfType<JObject>().FirstOrDefault(item =>
                ReadString(item, "rel") == BugCreationFields.FeedbackRelation &&
                IsWorkItemApiUrl(ReadString(item, "url"), feedbackTicketId, canonicalProject));
            if (ReadWorkItemId(verified) != id ||
                verified["fields"] is not JObject verifiedFields ||
                ReadString(verifiedFields, "System.WorkItemType") != "Bug" ||
                !IsWorkItemApiUrl(ReadString(verified, "url"), id, canonicalProject) ||
                relation == null)
                throw new BugCreationOutcomeUnknownException(
                    feedbackTicketId, "The created Bug and its actual Related source relationship could not be verified.", id, browserUrl);

            return new CreatedBugResult(id, browserUrl, (string)verified["url"]!, feedbackTicketId, (string)relation["url"]!);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            throw new BugCreationOutcomeUnknownException(feedbackTicketId, ex.Message, id, browserUrl, ex);
        }
    }

    private HttpRequestMessage CreateBugRequest(string url, string payload)
    {
        var request = CreateRequest(HttpMethod.Post, url);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json-patch+json");
        return request;
    }

    private async Task<string> SendBugPreflightAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Azure DevOps rejected {request.Method} {request.RequestUri} " +
                $"(HTTP {(int)response.StatusCode}): {GetDevOpsErrorDetail(body)}",
                null, response.StatusCode);
        return body;
    }

    private static JObject ParseBugPreflightDocument(string body)
    {
        try
        {
            return JObject.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Azure DevOps returned invalid preflight data. No Bug was created.", ex);
        }
    }

    private static int? ReadWorkItemId(JObject item) =>
        item["id"]?.Type == JTokenType.Integer &&
        int.TryParse(item["id"]!.ToString(), out var id) && id > 0 ? id : null;

    private static string? ReadString(JObject item, string property) =>
        item[property]?.Type == JTokenType.String ? item.Value<string>(property) : null;

    private static bool IsWorkItemApiUrl(string? value, int id, string? canonicalProject)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var actual))
            return false;
        var expected = new Uri(FeedbackWorkItemUrl(id));
        var segments = actual.AbsolutePath.Trim('/').Split('/');
        if (segments.Length is not (5 or 6) ||
            !segments[0].Equals(Uri.EscapeDataString(Constants.Organization), StringComparison.OrdinalIgnoreCase))
            return false;
        if (segments.Length == 6 &&
            !segments[1].Equals(Uri.EscapeDataString(Constants.Project), StringComparison.OrdinalIgnoreCase) &&
            !segments[1].Equals(canonicalProject, StringComparison.OrdinalIgnoreCase))
            return false;
        var apiIndex = segments.Length - 4;
        return actual.Scheme == expected.Scheme &&
            actual.Authority.Equals(expected.Authority, StringComparison.OrdinalIgnoreCase) &&
            actual.UserInfo.Length == 0 && actual.Query.Length == 0 && actual.Fragment.Length == 0 &&
            segments[apiIndex].Equals("_apis", StringComparison.OrdinalIgnoreCase) &&
            segments[apiIndex + 1].Equals("wit", StringComparison.OrdinalIgnoreCase) &&
            segments[apiIndex + 2].Equals("workitems", StringComparison.OrdinalIgnoreCase) &&
            segments[apiIndex + 3] == id.ToString();
    }
}
