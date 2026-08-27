using FT_MONITOR.Services;
using FT_MONITOR.Services.Agent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
        private readonly DevOpsTokenService _devOpsToken;

        public AgentController(
            AgentService agent,
            AgentToolRegistry tools,
            SkillStore skills,
            DevOpsTokenService devOpsToken)
        {
            _agent = agent;
            _tools = tools;
            _skills = skills;
            _devOpsToken = devOpsToken;
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

            AgentSkill? skill = null;
            if (!string.IsNullOrWhiteSpace(req.SkillId))
            {
                skill = _skills.Get(req.SkillId!);
                if (skill == null) return NotFound(new { error = $"Skill '{req.SkillId}' not found." });
            }

            string? token = null;
            try { token = await _devOpsToken.GetTokenAsync(); }
            catch { /* tools that need it will surface a clearer error */ }

            var ctx = new AgentContext
            {
                UserName  = User.FindFirst("name")?.Value ?? User.Identity?.Name ?? "unknown",
                UserEmail = User.FindFirst("preferred_username")?.Value ?? "",
                DevOpsToken = token,
                DryRun = req.DryRun
            };

            var result = await _agent.RunAsync(req.Prompt, skill, ctx, req.WorkingSet, ct);
            return Ok(result);
        }

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
