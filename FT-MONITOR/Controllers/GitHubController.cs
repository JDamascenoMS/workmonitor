using FT_MONITOR.Clients;
using FT_MONITOR.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FT_MONITOR.Controllers
{
    /// <summary>
    /// Read-only API for the GitHub Issues dashboard. The set of available
    /// repositories ("areas") comes from configuration key "GitHub:Repositories".
    /// </summary>
    [Authorize]
    [ApiController]
    [Route("/api/github")]
    public class GitHubController : ControllerBase
    {
        private readonly GitHubClient _github;
        private readonly IConfiguration _config;
        private readonly AzureOpenAIService _ai;

        public GitHubController(GitHubClient github, IConfiguration config, AzureOpenAIService ai)
        {
            _github = github;
            _config = config;
            _ai = ai;
        }

        // ----- helpers -------------------------------------------------------
        private record RepoArea(string Name, string Owner, string Repo);

        private List<RepoArea> GetRepositories()
        {
            var section = _config.GetSection("GitHub:Repositories");
            var list = new List<RepoArea>();
            foreach (var child in section.GetChildren())
            {
                var owner = child["owner"];
                var repo = child["repo"];
                var name = child["name"] ?? repo;
                if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo))
                    list.Add(new RepoArea(name!, owner!, repo!));
            }
            return list;
        }

        private RepoArea? FindArea(string name)
            => GetRepositories().FirstOrDefault(r =>
                string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

        // ----- endpoints -----------------------------------------------------

        /// <summary>Returns the configured GitHub repositories (areas).</summary>
        [HttpGet("repositories")]
        public IActionResult GetRepositories_Endpoint()
        {
            return Ok(GetRepositories().Select(r => new { r.Name, r.Owner, r.Repo }));
        }

        /// <summary>Diagnostic: reports whether a GitHub token is configured (without exposing it).</summary>
        [HttpGet("diag")]
        public IActionResult Diag()
        {
            var token = _config["GitHub:Token"];
            return Ok(new
            {
                tokenConfigured = !string.IsNullOrWhiteSpace(token),
                tokenLength = token?.Length ?? 0,
                tokenPrefix = string.IsNullOrEmpty(token) ? null : token.Substring(0, Math.Min(4, token.Length))
            });
        }

        /// <summary>
        /// Lists issues for an area. Supports state/label/assignee filters,
        /// free-text search (uses the GitHub search API when provided), and paging.
        /// </summary>
        [HttpGet("issues")]
        public async Task<IActionResult> ListIssues(
            [FromQuery] string area,
            [FromQuery] string state = "open",
            [FromQuery] string? labels = null,
            [FromQuery] string? assignee = null,
            [FromQuery] string? q = null,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 30)
        {
            var repo = FindArea(area);
            if (repo == null) return NotFound($"Unknown area '{area}'.");

            try
            {
                if (!string.IsNullOrWhiteSpace(q))
                {
                    var search = await _github.SearchIssuesAsync(repo.Owner, repo.Repo, q!, state, page, perPage);
                    return Ok(new
                    {
                        area = repo.Name,
                        totalCount = search?.TotalCount ?? 0,
                        items = search?.Items ?? new List<GitHubIssue>()
                    });
                }

                var (issues, next, last, prev) = await _github.ListIssuesAsync(
                    repo.Owner, repo.Repo, state, labels, assignee,
                    page: page, perPage: perPage);

                return Ok(new
                {
                    area = repo.Name,
                    page,
                    perPage,
                    nextPage = next,
                    lastPage = last,
                    prevPage = prev,
                    items = issues
                });
            }
            catch (GitHubApiException ex)
            {
                return StatusCode(ex.StatusCode, new
                {
                    error = "GitHub API call failed",
                    githubStatus = ex.StatusCode,
                    message = ex.Message,
                    rateLimit = ex.RateLimit,
                    rateRemaining = ex.RateRemaining
                });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { error = "GitHub API call failed", detail = ex.Message });
            }
        }

        /// <summary>Returns a single issue plus its comments.</summary>
        [HttpGet("issues/{number:int}")]
        public async Task<IActionResult> GetIssue([FromQuery] string area, int number)
        {
            var repo = FindArea(area);
            if (repo == null) return NotFound($"Unknown area '{area}'.");

            try
            {
                var issue = await _github.GetIssueAsync(repo.Owner, repo.Repo, number);
                if (issue == null) return NotFound();

                var comments = await _github.GetIssueCommentsAsync(repo.Owner, repo.Repo, number);
                return Ok(new { issue, comments });
            }
            catch (GitHubApiException ex)
            {
                return StatusCode(ex.StatusCode, new
                {
                    error = "GitHub API call failed",
                    githubStatus = ex.StatusCode,
                    message = ex.Message
                });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { error = "GitHub API call failed", detail = ex.Message });
            }
        }

        /// <summary>
        /// AI summary of a single GitHub issue, including the discussion thread.
        /// </summary>
        [HttpGet("issues/{number:int}/summary")]
        public async Task<IActionResult> SummarizeIssue([FromQuery] string area, int number)
        {
            var repo = FindArea(area);
            if (repo == null) return NotFound($"Unknown area '{area}'.");

            try
            {
                var issue = await _github.GetIssueAsync(repo.Owner, repo.Repo, number);
                if (issue == null) return NotFound();

                var comments = await _github.GetIssueCommentsAsync(repo.Owner, repo.Repo, number);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"GitHub issue #{issue.Number} — {issue.Title}");
                sb.AppendLine($"State: {issue.State} | Author: {issue.User?.Login} | Comments: {issue.Comments}");
                sb.AppendLine($"Labels: {string.Join(", ", issue.Labels?.Select(l => l.Name) ?? Array.Empty<string?>())}");
                sb.AppendLine();
                sb.AppendLine("BODY:");
                sb.AppendLine(Truncate(issue.Body, 4000));
                sb.AppendLine();
                sb.AppendLine("DISCUSSION:");
                foreach (var c in comments.Take(30))
                {
                    sb.AppendLine($"--- {c.User?.Login} @ {c.CreatedAt:u} ---");
                    sb.AppendLine(Truncate(c.Body, 1500));
                }

                var prompt =
                    "You are a triage assistant. Read the GitHub issue below and produce a concise plain-text " +
                    "summary with these sections: 'Problem', 'Root cause / hypotheses', 'Current status', " +
                    "and 'Suggested next step'. Do NOT return JSON or markdown fences.\n\n" + sb;

                var summary = await _ai.GenerateRawCompletion(prompt);
                return Ok(new { number = issue.Number, title = issue.Title, summary });
            }
            catch (GitHubApiException ex)
            {
                return StatusCode(ex.StatusCode, new
                {
                    error = "GitHub API call failed",
                    githubStatus = ex.StatusCode,
                    message = ex.Message
                });
            }
            catch (HttpRequestException ex)
            {
                return StatusCode(502, new { error = "GitHub API call failed", detail = ex.Message });
            }
        }

        private static string Truncate(string? s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
