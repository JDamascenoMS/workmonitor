using WorkMonitor.Core.Clients;
using WorkMonitor.Core.Helpers;
using WorkMonitor.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using WorkItem = WorkMonitor.Core.Models.WorkItem;

namespace WorkMonitor.Core.Services
{
    public class MonitorService
    {
        List<WorkItemComments> ListWorkItems;
        private readonly string _accessToken;

        public MonitorService(string accessToken) {
            ListWorkItems = new();
            _accessToken = accessToken;
        }

        private MonitorClient CreateClient() => new MonitorClient(_accessToken);

        public async Task<List<InternalItem>> GetTopBugsByPriority(string projectArea, string assignedTo = null)
        {
            var client = CreateClient();
            var response = await client.GetTopBugsByPriority(projectArea, assignedTo);

            if (response?.value == null || !response.value.Any())
            {
                return new List<InternalItem>();
            }

            return response.value
                .OrderBy(x => x.fields.Priority)
                .ToList();
        }

        public async Task<List<InternalItem>> GetTopFeedbackByPriority(string projectArea, string assignedTo = null)
        {
            var client = CreateClient();
            var response = await client.GetTopFeedbackByPriority(projectArea, assignedTo);

            if (response?.value == null || !response.value.Any())
            {
                return new List<InternalItem>();
            }

            return response.value.ToList();
        }

        public async Task<List<string>> GetAssigneesByArea(string projectArea, string workItemType = "Bug")
        {
            var client = CreateClient();
            return await client.GetAssigneesByArea(projectArea, workItemType);
        }

        public async Task<List<InternalItem>> SearchByMention(
            string projectArea,
            string searchText,
            string workItemType = "Bug",
            CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            var workItemsResponse = await client.SearchByArea(
                projectArea,
                workItemType,
                cancellationToken);

            if (workItemsResponse?.value == null || !workItemsResponse.value.Any())
            {
                return new List<InternalItem>();
            }

            var matchingItems = new List<InternalItem>();

            // First pass: check title and description (already fetched, no extra API calls)
            var needsCommentCheck = new List<InternalItem>();
            foreach (var item in workItemsResponse.value)
            {
                bool found = false;

                if (!string.IsNullOrEmpty(item.fields.Title) &&
                    item.fields.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                }

                if (!found && !string.IsNullOrEmpty(item.fields.Description) &&
                    item.fields.Description.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                }

                if (found)
                    matchingItems.Add(item);
                else
                    needsCommentCheck.Add(item);
            }

            // Second pass: check DevOps comments in parallel batches of 5
            foreach (var batch in needsCommentCheck.Chunk(5))
            {
                var tasks = batch.Select(async item =>
                {
                    try
                    {
                        var comments = await client.GetCommentsByWorkItem(
                            item.id,
                            cancellationToken);
                        if (comments?.comments != null &&
                            comments.comments.Any(c => c.text != null &&
                                c.text.Contains(searchText, StringComparison.OrdinalIgnoreCase)))
                        {
                            return item;
                        }

                        // Check customer comments
                        if (!string.IsNullOrEmpty(item.fields.PostId))
                        {
                            var customerComments = await client.GetVSDivCommentsByPostId(
                                item.fields.PostId,
                                cancellationToken);
                            if (customerComments?.children != null &&
                                customerComments.children.Any(c =>
                                    (c.text != null && c.text.Contains(searchText, StringComparison.OrdinalIgnoreCase)) ||
                                    (c.description != null && c.description.Contains(searchText, StringComparison.OrdinalIgnoreCase))))
                            {
                                return item;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error checking comments for WI {item.id}: {ex.Message}");
                    }
                    return null;
                });

                var results = await Task.WhenAll(tasks);
                matchingItems.AddRange(results.Where(r => r != null)!);
            }

            return matchingItems
                .OrderBy(x => x.fields.Priority)
                .Take(100)
                .ToList();
        }

        public async Task<object> GetWorkItemDetails(
            int workItemId,
            CancellationToken cancellationToken = default)
        {
            var client = CreateClient();
            var comments = await client.GetCommentsByWorkItem(
                workItemId,
                cancellationToken);
            var workItemResponse = await client.GetWorkItems(
                new List<int> { workItemId },
                cancellationToken);

            var workItem = workItemResponse?.value?.FirstOrDefault();
            VsDivCommentResponse? customerComments = null;

            if (workItem?.fields?.PostId != null)
            {
                customerComments = await client.GetVSDivCommentsByPostId(
                    workItem.fields.PostId,
                    cancellationToken);
            }

            return new
            {
                description = workItem?.fields?.Description,
                state = workItem?.fields?.State,
                createdBy = workItem?.fields?.CreatedBy?.displayName,
                postId = workItem?.fields?.PostId,
                postTitle = customerComments?.title,
                postContent = customerComments?.content,
                comments = comments?.comments?.Select(c => new
                {
                    c.text,
                    c.modifiedDate
                }).ToList(),
                customerComments = customerComments?.children?.Select(c => new
                {
                    c.text,
                    author = c.author?.name,
                    c.description
                }).ToList()
            };
        }

        public async Task<dynamic> Initialize()
        {
            try
            {
                var client = CreateClient();
                var WorkItemsId = new List<int>();
                List<CommentsResponse> commentsList = new();
                List<Comment> comments = new();

                var respWiql = await client.GetWiqlIdResponse(KeySearch.QueryIdBase);
                List<string> fields = new List<string>() { "System.Title", "System.Description" };
                List<string> keys = new List<string>() { "relations" };
                List<string> values = new List<string>() { "System.Id" };
                //var respWiql = await client.ExecuteWiqlQuery(KeySearch.WiqlQueryBase);
                List<WorkItem> itemsWiql = new(respWiql.workItems);

                itemsWiql.ForEach(x => WorkItemsId.Add(x.id));

                 var respWorkItems = await client.GetWorkItems(WorkItemsId);
                int counting = 0;
                foreach (var item in respWorkItems.value)
                {
                    var vsDivComments = await client.GetVSDivCommentsByPostId(item.fields.PostId);
                    var respComments = await client.GetCommentsByWorkItem(item.id);
                    ListWorkItems.Add(new WorkItemComments
                    {
                        WorkItemId = item.id,
                        CustomFields = item.fields,
                        Comments = respComments?.comments,
                        CustomerComments = vsDivComments?.children,
                    });
                    counting++;
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return ex.Message;
            }
        }

        public async Task SearchCount()
        {
            try
            {
                var Result = new List<string>();
                var Sources = new List<string>();

                foreach (var keysearch in KeySearch.SearchStrings)
                {
                    string key = keysearch;
                    int occasions = 0;
                    Sources.Add(key);
                    
                    foreach (var wi in ListWorkItems)
                    {
                        int cont = 0;
                        
                        cont = wi.CustomFields.Title.Contains(keysearch, StringComparison.OrdinalIgnoreCase) ? 1 : cont;
                        cont = wi.CustomFields.Description.Contains(keysearch, StringComparison.OrdinalIgnoreCase) ? 1 : cont;
                        cont = wi.Comments?.Where(x => x.text.Contains(keysearch, StringComparison.OrdinalIgnoreCase)).Count() > 0 ? 1 : cont;
                        cont = wi.CustomerComments?.Where(x => x.text.Contains(keysearch, StringComparison.OrdinalIgnoreCase)).Count() > 0 ? 1 : cont;

                        if (cont > 0)
                        {
                            Sources.Add(wi.WorkItemId.ToString());
                            occasions++;
                        }
                    }

                    Result.Add(key+"-"+occasions);
                }

                await WriteFileText(Result, "Results");
                await WriteFileText(Sources, "Sources");
            }
            catch(Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

        }

        private async Task WriteFileText(dynamic content, string dictype)
        {
            string cont = JsonConvert.SerializeObject(content);
            DateTime dateTime = DateTime.Now;
            string filename = "report_FTMON_"+ dictype +"_"+ dateTime.ToString("yyyy_MM_dd_HHmmss")+Constants.FormatExt;
            string filepath = @"C:\_raiz\reports\output\" + filename;
            File.WriteAllText(filepath, cont);
        }

        public async Task<WorkItemResponse> GetWorkItemsFromWiqlQuery(string wiqlQuery, int? top = null)
        {
            var client = CreateClient();
            var wiqlResponse = await client.ExecuteWiqlQuery(wiqlQuery, top);

            if (wiqlResponse?.workItems == null || !wiqlResponse.workItems.Any())
            {
                return new WorkItemResponse();
            }

            var workItemIds = wiqlResponse.workItems.Select(wi => wi.id).ToList();
            var response = await client.GetWorkItems(workItemIds);

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
    }
}
