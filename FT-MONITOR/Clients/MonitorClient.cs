using FT_MONITOR.Helpers;
using FT_MONITOR.Model;
using Microsoft.VisualStudio.Services.Organization.Client;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http.Headers;
using FT_MONITOR.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace FT_MONITOR.Clients
{
    public class MonitorClient
    {
        private readonly string _accessToken;

        public MonitorClient(string accessToken)
        {
            _accessToken = accessToken;
        }
        public async Task<WiqlResponse> GetWiqlIdResponse(string QueryId)
        {
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/wiql/{QueryId}?{Constants.ApiVersion}";
            var response = await GenericCallGet(Url);
            var ret = JsonConvert.DeserializeObject<WiqlResponse>(response);
            return ret;
        }

        public async Task<WorkItemResponse> GetWorkItemsFromSavedQuery(string queryId)
        {
            var wiqlResponse = await GetWiqlIdResponse(queryId);

            if (wiqlResponse?.workItems == null || !wiqlResponse.workItems.Any())
            {
                return new WorkItemResponse();
            }

            var workItemIds = wiqlResponse.workItems.Select(wi => wi.id).ToList();
            var response = await GetWorkItems(workItemIds);

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

        public async Task<WorkItemResponse> GetWorkItems(List<int> ListIds)
        {
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/workitemsbatch?{Constants.ApiVersion}";
            var body = new WorkItemRequest(ListIds);
            var response = await GenericCallPost(Url, body);
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

        public async Task<CommentsResponse> GetCommentsByWorkItem(int WorkItemId)
        {
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/workItems/{WorkItemId}/comments?{Constants.ApiVersion}-preview.4";
            var response = await GenericCallGet(Url);
            var ret = JsonConvert.DeserializeObject<CommentsResponse>(response);
            return ret;
        }

        public async Task<WorkItemResponse> GetWorkItemsFromWiqlQuery(string wiqlQuery, int? top = null)
        {
            var wiqlResponse = await ExecuteWiqlQuery(wiqlQuery, top);

            if (wiqlResponse?.workItems == null || !wiqlResponse.workItems.Any())
            {
                return new WorkItemResponse();
            }

            var workItemIds = wiqlResponse.workItems.Select(wi => wi.id).ToList();
            var response = await GetWorkItems(workItemIds);

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

            string assigneeFilter = assignedTo == "__none__"
                ? "AND [System.AssignedTo] = ''"
                : !string.IsNullOrEmpty(assignedTo)
                    ? $"AND [System.AssignedTo] = '{assignedTo}'"
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

        public async Task<WorkItemResponse> SearchByArea(string projectArea, string workItemType = "Bug")
        {
            string areaPath = !Constants.ProjectAreaPath.TryGetValue(projectArea, out string val)
                ? throw new Exception("Project area not found in constants.")
                : val;

            string query = @$"SELECT [System.Id], [System.Title], [System.State], 
                                [Microsoft.VSTS.Common.Priority], [System.CreatedDate], 
                                [System.AreaPath], [System.AssignedTo]
                              FROM WorkItems 
                              WHERE [System.WorkItemType] = '{workItemType}' 
                                AND [System.AreaPath] UNDER '{areaPath}'
                                AND [System.State] <> 'Closed'
                              ORDER BY [Microsoft.VSTS.Common.Priority] ASC";

            return await GetWorkItemsFromWiqlQuery(query, top: 200);
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

        public async Task<WiqlResponse> ExecuteWiqlQuery(string wiqlQuery, int? top = null)
        {
            string topParam = top.HasValue ? $"&$top={top.Value}" : "";
            string Url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/wiql?{Constants.ApiVersion}{topParam}";
            var body = new { query = wiqlQuery };
            var response = await GenericCallPost(Url, body);
            var ret = JsonConvert.DeserializeObject<WiqlResponse>(response);
            return ret;
        }

        public async Task<VsDivCommentResponse> GetVSDivCommentsByPostId(string PostId)
        {
            try
            {
                string Url = $"https://sendvsfeedback2.azurewebsites.net/api/detailsV3/rootpost?Id={PostId}";
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Accept.Add(
                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                var response = await httpClient.GetStringAsync(Url);
                var ret = JsonConvert.DeserializeObject<VsDivCommentResponse>(response);
                return ret;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Posts a comment on a work item. Returns the raw API response body.
        /// Used by the Agent feature.
        /// </summary>
        public async Task<string> AddCommentToWorkItem(int workItemId, string text)
        {
            string url = $"{Constants.ApiUrlBase}/{Constants.Organization}/{Constants.Project}/_apis/wit/workItems/{workItemId}/comments?{Constants.ApiVersion}-preview.4";
            var body = new { text };

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var json = JsonConvert.SerializeObject(body);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();
            response.EnsureSuccessStatusCode();
            return responseBody;
        }

        public async Task<dynamic> GenericCallGet(string Url)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                    using (HttpResponseMessage response = client.GetAsync(Url).Result)
                    {
                        response.EnsureSuccessStatusCode();
                        string responseBody = await response.Content.ReadAsStringAsync();
                        return responseBody;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                throw;
            }
        }

        public async Task<dynamic> GenericCallPost(string Url, object Body)
        {
            string returnMessage = string.Empty;
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

                    using (HttpResponseMessage response = client.PostAsJsonAsync(Url, Body).Result)
                    {
                        returnMessage = await response.Content.ReadAsStringAsync();
                            response.EnsureSuccessStatusCode();
                        string responseBody = await response.Content.ReadAsStringAsync();
                        return responseBody;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(returnMessage);
                throw;
            }
        }
    }
}
