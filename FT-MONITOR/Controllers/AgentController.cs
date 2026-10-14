using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using FT_MONITOR.Services;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Conversations;

namespace FT_MONITOR.Controllers
{
    /// <summary>
    /// HTTP API for the Agent feature. Exposes the tool registry, the agent run
    /// loop, and CRUD over user-defined skills.
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("/api/agent")]
    public class AgentController : ControllerBase
    {
        private readonly AgentService _agent;
        private readonly AgentToolRegistry _tools;
        private readonly SkillStore _skills;
        private readonly IWebDevOpsTokenService _devOpsToken;
        private readonly SessionStore _conversations;
        private readonly IAgentModel _model;
        private readonly ConversationCompactor _compactor;
        private readonly ConversationTurnCoordinator _turns;
        private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public AgentController(
            AgentService agent,
            AgentToolRegistry tools,
            SkillStore skills,
            IWebDevOpsTokenService devOpsToken,
            SessionStore conversations,
            IAgentModel model,
            ConversationCompactor compactor,
            ConversationTurnCoordinator turns)
        {
            _agent = agent;
            _tools = tools;
            _skills = skills;
            _devOpsToken = devOpsToken;
            _conversations = conversations;
            _model = model;
            _compactor = compactor;
            _turns = turns;
        }

        // ----- Tools -----

        /// <summary>Lists every registered agent tool (used by the UI to render the toolbox).</summary>
        [HttpGet("tools")]
        public IActionResult ListTools()
        {
            return Ok(_tools.All.Select(t => new
            {
                name = t.Name,
                description = t.Description,
                isWriteTool = t.IsWriteTool
            }));
        }

        // ----- Run -----

        public class RunRequest
        {
            public string Prompt { get; set; } = "";
            public string? SkillId { get; set; }
            public bool DryRun { get; set; } = true;

            /// <summary>
            /// Optional working set: items the user has pinned via the "Send to Agent"
            /// button. When present, the agent grounds every action on these ids.
            /// </summary>
            public List<AgentWorkingItem>? WorkingSet { get; set; }
        }

        /// <summary>Runs the agent loop for a single user prompt and returns a transcript.</summary>
        [HttpPost("run")]
        public async Task<IActionResult> Run([FromBody] RunRequest req, CancellationToken ct)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Prompt))
                return BadRequest(new { error = "Prompt is required." });
            if (!req.DryRun)
                return BadRequest(new { error = "The web agent is read-only.", errorCode = "read_only" });

            AgentSkill? skill = null;
            if (!string.IsNullOrWhiteSpace(req.SkillId))
            {
                skill = _skills.Get(req.SkillId!);
                if (skill == null) return NotFound(new { error = $"Skill '{req.SkillId}' not found." });
            }

            var token = await _devOpsToken.GetTokenAsync(ct);
            var ctx = new AgentContext
            {
                UserName  = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "unknown",
                UserEmail = User.FindFirst("preferred_username")?.Value ?? "",
                DevOpsToken = token,
                DryRun = true
            };

            var result = await _agent.RunAsync(
                req.Prompt,
                skill,
                ctx,
                req.WorkingSet,
                cancellationToken: ct);
            return Ok(result);
        }

        [HttpGet("conversations")]
        public async Task<IActionResult> ListConversations(
            CancellationToken ct,
            [FromQuery, Range(0, int.MaxValue)] int offset = 0,
            [FromQuery, Range(1, 100)] int limit = 20)
        {
            Response.Headers.CacheControl = "no-store";
            return Ok(await _conversations.ListForOwnerAsync(
                WebConversationIdentity.GetOwnerId(User), limit, ct, offset));
        }

        [HttpGet("csrf")]
        public IActionResult GetCsrfToken(
            [FromServices] IAntiforgery antiforgery,
            [FromServices] IConfiguration configuration)
        {
            if (WebAuthenticationModeConfiguration.GetMode(configuration) !=
                WebAuthenticationMode.LocalDevelopment)
                return NotFound();
            Response.Headers.CacheControl = "no-store";
            return Ok(new { requestToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });
        }

        [HttpPost("conversations")]
        public async Task<IActionResult> CreateConversation(CancellationToken ct)
        {
            var conversation = await _conversations.CreateForOwnerAsync(
                WebConversationIdentity.GetOwnerId(User),
                _model.ModelName,
                ct: ct);
            Response.Headers.CacheControl = "no-store";
            return Ok(conversation);
        }

        [HttpGet("conversations/{id}")]
        public async Task<IActionResult> GetConversation(string id, CancellationToken ct)
        {
            Response.Headers.CacheControl = "no-store";
            var conversation = await _conversations.LoadForOwnerAsync(
                WebConversationIdentity.GetOwnerId(User), id, ct);
            return conversation == null ? NotFound() : Ok(conversation);
        }

        public sealed class StreamRequest
        {
            [Required, MaxLength(16000)]
            public string Prompt { get; set; } = "";
            public string? SkillId { get; set; }
            public bool DryRun { get; set; } = true;
            public List<AgentWorkingItem>? WorkingSet { get; set; }
        }

        [HttpPost("conversations/{id}/stream")]
        public async Task<IActionResult> StreamConversation(
            string id,
            [FromBody] StreamRequest request,
            CancellationToken ct)
        {
            var owner = WebConversationIdentity.GetOwnerId(User);
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return BadRequest(new { error = "Prompt is required." });
            if (!request.DryRun)
                return BadRequest(new { error = "The web agent is read-only.", errorCode = "read_only" });
            if (request.WorkingSet?.Count > 50)
                return BadRequest(new { error = "Working sets are limited to 50 items." });

            var existing = await _conversations.LoadForOwnerAsync(owner, id, ct);
            if (existing == null) return NotFound();
            if (!_turns.TryBegin(id))
                return Conflict(new { error = "This conversation already has an active turn.", errorCode = "turn_in_progress" });

            var promptSaved = false;
            var terminalEvent = false;
            try
            {
                var session = await _conversations.LoadForOwnerAsync(owner, id, ct)
                    ?? throw new KeyNotFoundException("Conversation no longer exists.");
                AgentSkill? skill = null;
                if (!string.IsNullOrWhiteSpace(request.SkillId))
                {
                    skill = _skills.Get(request.SkillId);
                    if (skill == null) return NotFound(new { error = "Skill was not found." });
                }

                var token = await _devOpsToken.GetTokenAsync(ct);
                var fixedContext = request.Prompt.Length + 20000 +
                    (request.WorkingSet?.Sum(item =>
                        (item.Title?.Length ?? 0) +
                        (item.AreaPath?.Length ?? 0) +
                        (item.AssignedTo?.Length ?? 0) + 100) ?? 0);
                if (_compactor.NeedsCompaction(session.Summary, session.Messages, 60000, fixedContext))
                {
                    if (session.Messages.Count <= 2)
                        return BadRequest(new { error = "Conversation context is too large. Start a new conversation.", errorCode = "context_limit" });

                    var compacted = await _compactor.CompactAsync(
                        session.Summary,
                        session.Messages,
                        Math.Min(12, Math.Max(2, session.Messages.Count / 2)),
                        ct);
                    if (_compactor.NeedsCompaction(
                            compacted.Summary, compacted.RetainedMessages, 60000, fixedContext))
                        return BadRequest(new { error = "Conversation context exceeds the limit. Start a new conversation.", errorCode = "context_limit" });

                    await _conversations.ReplaceHistoryForOwnerAsync(
                        owner, id, compacted.Summary, compacted.RetainedMessages, ct);
                    session = await _conversations.LoadForOwnerAsync(owner, id, ct)
                        ?? throw new KeyNotFoundException("Conversation no longer exists.");
                }

                await _conversations.AppendMessageForOwnerAsync(
                    owner, id, new AgentConversationMessage("user", request.Prompt, DateTimeOffset.UtcNow), ct);
                promptSaved = true;
                if (session.Messages.Count == 0)
                {
                    var title = request.Prompt.Length <= 60
                        ? request.Prompt
                        : request.Prompt[..57] + "...";
                    await _conversations.UpdateTitleForOwnerAsync(owner, id, title, ct);
                }

                Response.ContentType = "application/x-ndjson";
                Response.Headers.CacheControl = "no-store";
                Response.Headers["X-Accel-Buffering"] = "no";
                HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                var context = new AgentContext
                {
                    UserName = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "unknown",
                    UserEmail = User.FindFirst("preferred_username")?.Value ?? "",
                    SessionId = id,
                    DevOpsToken = token,
                    DryRun = true
                };

                await foreach (var agentEvent in _agent.RunStreamingAsync(
                    request.Prompt, skill, context, request.WorkingSet,
                    session.Messages, session.Summary, ct))
                {
                    if (agentEvent.Kind == AgentEventKind.ToolCompleted)
                    {
                        var outcome = $"Tool '{agentEvent.ToolName}' completed.\n" +
                            $"Arguments: {LimitText(agentEvent.ToolArguments, 4000)}\n" +
                            $"Result: {LimitText(agentEvent.Content, 16000)}";
                        await _conversations.AppendMessageForOwnerAsync(
                            owner, id, new AgentConversationMessage("tool", outcome, DateTimeOffset.UtcNow),
                            CancellationToken.None);
                    }
                    else if (agentEvent.Kind is AgentEventKind.Completed or AgentEventKind.Error)
                    {
                        var content = agentEvent.Kind == AgentEventKind.Error
                            ? "[Turn failed] " + agentEvent.Content
                            : agentEvent.Content ?? "";
                        await _conversations.AppendMessageForOwnerAsync(
                            owner, id, new AgentConversationMessage("assistant", content, DateTimeOffset.UtcNow),
                            CancellationToken.None);
                        terminalEvent = true;
                    }

                    await Response.WriteAsync(
                        JsonSerializer.Serialize(agentEvent, StreamJson) + "\n", ct);
                    await Response.Body.FlushAsync(ct);
                }

                return new EmptyResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (promptSaved && !terminalEvent)
                {
                    await _conversations.AppendMessageForOwnerAsync(
                        owner, id,
                        new AgentConversationMessage("assistant", "[Turn cancelled]", DateTimeOffset.UtcNow),
                        CancellationToken.None);
                }
                return new EmptyResult();
            }
            finally
            {
                _turns.End(id);
            }
        }

        private static string LimitText(string? value, int limit)
            => string.IsNullOrEmpty(value)
                ? ""
                : value.Length <= limit ? value : value[..limit] + "\n[truncated]";

        // ----- Skills -----

        public class SkillRequest
        {
            public string Name { get; set; } = "";
            public string Description { get; set; } = "";
            public string SystemPromptAddendum { get; set; } = "";
            public string[] SuggestedTools { get; set; } = Array.Empty<string>();
        }

        [HttpGet("skills")]
        public IActionResult ListSkills() => Ok(_skills.List());

        [HttpPost("skills")]
        public async Task<IActionResult> CreateSkill([FromBody] SkillRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { error = "Skill name is required." });

            // Reject any suggested tool names that aren't actually registered,
            // so the LLM can't be steered toward a phantom tool name.
            var unknown = req.SuggestedTools
                .Where(n => !_tools.TryGet(n, out _))
                .ToList();
            if (unknown.Count > 0)
                return BadRequest(new { error = $"Unknown tools: {string.Join(", ", unknown)}" });

            var skill = await _skills.CreateAsync(new AgentSkill
            {
                Name = req.Name.Trim(),
                Description = req.Description?.Trim() ?? "",
                SystemPromptAddendum = req.SystemPromptAddendum?.Trim() ?? "",
                SuggestedTools = req.SuggestedTools ?? Array.Empty<string>()
            });
            return Ok(skill);
        }

        [HttpDelete("skills/{id}")]
        public async Task<IActionResult> DeleteSkill(string id)
        {
            var ok = await _skills.DeleteAsync(id);
            return ok ? NoContent() : NotFound();
        }
    }
}
