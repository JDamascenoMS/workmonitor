using FT_MONITOR.Clients;
using FT_MONITOR.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace FT_MONITOR.Controllers
{
    [Authorize]
    [Route("/api")]
    public class MonitorController : ControllerBase
    {
        private readonly AzureOpenAIService _aiService;
        private readonly DevOpsTokenService _devOpsTokenService;

        public MonitorController(AzureOpenAIService aiService, DevOpsTokenService devOpsTokenService)
        {
            _aiService = aiService;
            _devOpsTokenService = devOpsTokenService;
        }

        private async Task<string> GetAccessTokenAsync()
        {
            return await _devOpsTokenService.GetTokenAsync();
        }

        private async Task<MonitorService> CreateServiceAsync()
        {
            var token = await GetAccessTokenAsync();
            return new MonitorService(token);
        }

        private async Task<MonitorClient> CreateClientAsync()
        {
            var token = await GetAccessTokenAsync();
            return new MonitorClient(token);
        }

        [HttpGet("Listar")]
        public async Task<IActionResult> GetLista()
        {
            var serv = await CreateServiceAsync();
            await serv.Initialize();
            await serv.SearchCount();

            return Ok();
        }

        [HttpGet("top-bugs")]
        public async Task<IActionResult> GetTopBugs([FromQuery] string projectArea = "Project", [FromQuery] string assignedTo = null)
        {
            var serv = await CreateServiceAsync();
            var bugs = await serv.GetTopBugsByPriority(projectArea, assignedTo);
            return Ok(bugs);
        }

        [HttpGet("top-feedback")]
        public async Task<IActionResult> GetTopFeedback([FromQuery] string projectArea = "Project", [FromQuery] string assignedTo = null)
        {
            var serv = await CreateServiceAsync();
            var feedback = await serv.GetTopFeedbackByPriority(projectArea, assignedTo);
            return Ok(feedback);
        }

        [HttpGet("assignees")]
        public async Task<IActionResult> GetAssignees([FromQuery] string projectArea = "Project", [FromQuery] string workItemType = "Bug")
        {
            var serv = await CreateServiceAsync();
            var assignees = await serv.GetAssigneesByArea(projectArea, workItemType);
            return Ok(assignees);
        }

        [HttpGet("work-item-details/{id}")]
        public async Task<IActionResult> GetWorkItemDetails(int id)
        {
            var serv = await CreateServiceAsync();
            var details = await serv.GetWorkItemDetails(id);
            return Ok(details);
        }

        [HttpGet("search-mentions")]
        public async Task<IActionResult> SearchMentions([FromQuery] string projectArea = "Project", [FromQuery] string searchText = "", [FromQuery] string workItemType = "Bug")
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return BadRequest("searchText is required.");

            var serv = await CreateServiceAsync();
            var results = await serv.SearchByMention(projectArea, searchText, workItemType);
            return Ok(results);
        }

        [HttpGet("summarize/{id}")]
        public async Task<IActionResult> SummarizeBug(int id)
        {
            var serv = await CreateServiceAsync();
            var details = await serv.GetWorkItemDetails(id);

            dynamic d = details;
            string description = d.description ?? "";
            string state = d.state ?? "";
            string title = $"Work Item #{id}";
            var comments = new List<string>();

            if (d.comments != null)
            {
                foreach (var c in d.comments)
                    comments.Add((string)c.text ?? "");
            }

            var summary = await _aiService.SummarizeBug(title, description, state, comments);
            return Ok(new { summary });
        }

        [HttpGet("draft-response/{id}")]
        public async Task<IActionResult> DraftCustomerResponse(int id)
        {
            var serv = await CreateServiceAsync();
            var details = await serv.GetWorkItemDetails(id);

            dynamic d = details;
            string description = d.description ?? "";
            string state = d.state ?? "";
            string title = d.postTitle ?? $"Work Item #{id}";

            var customerComments = new List<string>();
            if (d.customerComments != null)
            {
                foreach (var c in d.customerComments)
                    customerComments.Add((string)(c.text ?? c.description ?? ""));
            }

            var internalComments = new List<string>();
            if (d.comments != null)
            {
                foreach (var c in d.comments)
                    internalComments.Add((string)c.text ?? "");
            }

            var response = await _aiService.GenerateCustomerResponse(title, description, state, customerComments, internalComments);
            return Ok(new { response });
        }

        [HttpGet("feedback-themes")]
        public async Task<IActionResult> GetFeedbackThemes([FromQuery] string projectArea = "Project")
        {
            try
            {
                var serv = await CreateServiceAsync();
                var feedback = await serv.GetTopFeedbackByPriority(projectArea);

                if (feedback == null || !feedback.Any())
                    return Ok(new { themes = "[]", items = new List<object>() });

                // Build a lightweight JSON payload for AI classification
                var itemsForAi = feedback.Select(f => new
                {
                    Id = f.id,
                    Title = f.fields?.Title ?? "",
                    Description = f.fields?.Description ?? "",
                    State = f.fields?.State ?? "",
                    Priority = f.fields?.Priority ?? 0
                }).ToList();

                var json = System.Text.Json.JsonSerializer.Serialize(itemsForAi);

                var themesJson = await _aiService.ClassifyFeedbackThemes(json);

                // Strip markdown fences if the model wraps the response
                themesJson = themesJson.Trim();
                if (themesJson.StartsWith("```"))
                {
                    var firstNewline = themesJson.IndexOf('\n');
                    if (firstNewline >= 0)
                        themesJson = themesJson[(firstNewline + 1)..];
                    if (themesJson.EndsWith("```"))
                        themesJson = themesJson[..^3];
                    themesJson = themesJson.Trim();
                }

                // Return both the AI themes and the full item data so the page can display details
                var itemsLookup = feedback.Select(f => new
                {
                    id = f.id,
                    title = f.fields?.Title ?? "",
                    state = f.fields?.State ?? "",
                    priority = f.fields?.Priority ?? 0,
                    assignedTo = f.fields?.AssignedTo?.displayName ?? "Unassigned",
                    createdDate = f.fields?.CreatedDate ?? ""
                });

                return Ok(new { themes = themesJson, items = itemsLookup });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error analyzing themes: {ex.Message}");
            }
        }

        [HttpGet("most-relevant-tickets")]
        public async Task<IActionResult> GetMostRelevantTickets([FromQuery] string projectArea = "Project")
        {
            try
            {
                var serv = await CreateServiceAsync();
                var feedback = await serv.GetTopFeedbackByPriority(projectArea);

                if (feedback == null || !feedback.Any())
                    return Ok(new { ranked = new List<object>(), items = new List<object>(), themes = "[]" });

                // Build payloads for AI
                var itemsForAi = feedback.Select(f => new
                {
                    Id = f.id,
                    Title = f.fields?.Title ?? "",
                    Description = f.fields?.Description ?? "",
                    State = f.fields?.State ?? "",
                    Priority = f.fields?.Priority ?? 0,
                    CreatedDate = f.fields?.CreatedDate ?? ""
                }).ToList();

                var itemsJson = System.Text.Json.JsonSerializer.Serialize(itemsForAi);

                // Step 1: get themes (so the ranker can weigh theme importance)
                var themesJson = await _aiService.ClassifyFeedbackThemes(itemsJson);
                themesJson = StripFences(themesJson);

                // Step 2: ask AI for the top 5 most relevant tickets
                var rankedJson = await _aiService.RankMostRelevantTickets(itemsJson, themesJson);
                rankedJson = StripFences(rankedJson);

                System.Text.Json.JsonElement rankedParsed;
                try
                {
                    rankedParsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(rankedJson);
                }
                catch
                {
                    return StatusCode(500, "AI returned invalid ranking data.");
                }

                // Lookup of full item info for the page to display details
                var itemsLookup = feedback.Select(f => new
                {
                    id = f.id,
                    title = f.fields?.Title ?? "",
                    state = f.fields?.State ?? "",
                    priority = f.fields?.Priority ?? 0,
                    assignedTo = f.fields?.AssignedTo?.displayName ?? "Unassigned",
                    areaPath = f.fields?.AreaPath ?? "",
                    createdDate = f.fields?.CreatedDate ?? ""
                });

                return Ok(new { ranked = rankedParsed, items = itemsLookup, themes = themesJson });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error ranking most relevant tickets: {ex.Message}");
            }
        }

        private static string StripFences(string s)
        {
            s = s?.Trim() ?? "";
            if (s.StartsWith("```"))
            {
                var firstNewline = s.IndexOf('\n');
                if (firstNewline >= 0)
                    s = s[(firstNewline + 1)..];
                if (s.EndsWith("```"))
                    s = s[..^3];
                s = s.Trim();
            }
            return s;
        }

        [HttpGet("bug-probability/{id}")]
        public async Task<IActionResult> GetBugProbability(int id, [FromQuery] int priority = 3, [FromQuery] bool isTopTheme = false)
        {
            var serv = await CreateServiceAsync();
            var details = await serv.GetWorkItemDetails(id);

            dynamic d = details;
            string description = d.description ?? "";
            string title = $"Work Item #{id}";

            var comments = new List<string>();
            if (d.comments != null)
            {
                foreach (var c in d.comments)
                    comments.Add((string)c.text ?? "");
            }
            if (d.customerComments != null)
            {   
                foreach (var c in d.customerComments)
                    comments.Add((string)(c.text ?? c.description ?? ""));
            }

            var resultJson = await _aiService.AssessBugProbability(title, description, priority, isTopTheme, comments);

            // Strip markdown fences if the model wraps the response
            resultJson = resultJson.Trim();
            if (resultJson.StartsWith("```"))
            {
                var firstNewline = resultJson.IndexOf('\n');
                if (firstNewline >= 0)
                    resultJson = resultJson[(firstNewline + 1)..];
                if (resultJson.EndsWith("```"))
                    resultJson = resultJson[..^3];
                resultJson = resultJson.Trim();
            }

            var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resultJson);
            return Ok(parsed);
        }

        [HttpGet("work-item-types")]
        public async Task<IActionResult> GetWorkItemTypes([FromQuery] string projectArea = "Project")
        {
            var client = await CreateClientAsync();
            string areaPath = !FT_MONITOR.Helpers.Constants.ProjectAreaPath.TryGetValue(projectArea, out string val)
                ? throw new Exception("Project area not found.")
                : val;

            string query = @$"SELECT [System.Id], [System.WorkItemType]
                              FROM WorkItems 
                              WHERE [System.AreaPath] UNDER '{areaPath}'
                                AND [System.State] <> 'Closed'";

            var response = await client.GetWorkItemsFromWiqlQuery(query, top: 200);

            if (response?.value == null)
                return Ok(new List<string>());

            var types = response.value
                .Where(x => x.fields != null && !string.IsNullOrEmpty(x.fields.WorkItemType))
                .Select(x => x.fields.WorkItemType)
                .Distinct()
                .OrderBy(x => x)
                .ToList();

            return Ok(types);
        }

        [HttpGet("execute-query/{queryId}")]
        public async Task<IActionResult> ExecuteSavedQuery(string queryId)
        {
            try
            {
                var client = await CreateClientAsync();
                var response = await client.GetWorkItemsFromSavedQuery(queryId);

                if (response?.value == null || !response.value.Any())
                    return Ok(new List<object>());

                return Ok(response.value);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Error executing saved query: {ex.Message}");
            }
        }

        [HttpGet("my-dashboard")]
        public async Task<IActionResult> GetMyDashboard()
        {
            var userName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "";
            if (string.IsNullOrWhiteSpace(userName))
                return BadRequest("Could not determine user name from session.");

            var client = await CreateClientAsync();
            var (items, totalCount) = await client.GetMyDashboardItems(userName, top: 10);

            var result = items.Select(i => new
            {
                id = i.id,
                title = i.fields?.Title ?? "",
                state = i.fields?.State ?? "",
                priority = i.fields?.Priority ?? 0,
                workItemType = i.fields?.WorkItemType ?? "",
                assignedTo = i.fields?.AssignedTo?.displayName ?? "Unassigned",
                areaPath = i.fields?.AreaPath ?? "",
                changedDate = i.fields?.ChangedDate ?? "",
                createdDate = i.fields?.CreatedDate ?? ""
            });

            return Ok(new { items = result, totalCount, userName });
        }

        [HttpGet("my-items")]
        public async Task<IActionResult> GetMyItems(
            [FromQuery] string type = "Bug",
            [FromQuery] string mode = "assigned",
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var userName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "";
            if (string.IsNullOrWhiteSpace(userName))
                return BadRequest("Could not determine user name from session.");

            var client = await CreateClientAsync();
            var (items, totalCount) = await client.GetMyItems(userName, type, mode, page, pageSize);

            var result = items.Select(i => new
            {
                id = i.id,
                title = i.fields?.Title ?? "",
                state = i.fields?.State ?? "",
                priority = i.fields?.Priority ?? 0,
                workItemType = i.fields?.WorkItemType ?? "",
                assignedTo = i.fields?.AssignedTo?.displayName ?? "Unassigned",
                areaPath = i.fields?.AreaPath ?? "",
                changedDate = i.fields?.ChangedDate ?? "",
                createdDate = i.fields?.CreatedDate ?? ""
            });

            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            return Ok(new { items = result, totalCount, page, pageSize, totalPages, userName });
        }
    }
}
