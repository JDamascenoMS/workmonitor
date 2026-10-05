using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace WorkMonitor.Core.Clients
{
    /// <summary>
    /// Thin HttpClient wrapper around the GitHub REST API used by the
    /// FT-MONITOR GitHub Issues feature. Reads the optional PAT from
    /// configuration key "GitHub:Token".
    /// </summary>
    public class GitHubClient
    {
        private readonly HttpClient _http;
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public GitHubClient(HttpClient http, IConfiguration config)
        {
            _http = http;
            _http.BaseAddress = new Uri("https://api.github.com/");
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WorkMonitor/1.0");
            _http.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            var token = config["GitHub:Token"];
            if (!string.IsNullOrWhiteSpace(token))
            {
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token);
            }
        }

        /// <summary>
        /// Lists issues for a repository. By default this excludes pull requests
        /// (the GitHub API conflates the two in /issues).
        /// </summary>
        public async Task<(List<GitHubIssue> Issues, int? NextPage, int? LastPage, int? PrevPage)> ListIssuesAsync(
            string owner,
            string repo,
            string state = "open",        // open | closed | all
            string? labels = null,        // comma-separated
            string? assignee = null,      // user login, or "*" / "none"
            string sort = "updated",      // created | updated | comments
            string direction = "desc",
            int page = 1,
            int perPage = 30,
            bool excludePullRequests = true,
            CancellationToken cancellationToken = default)
        {
            var qs = new List<string>
            {
                $"state={Uri.EscapeDataString(state)}",
                $"sort={Uri.EscapeDataString(sort)}",
                $"direction={Uri.EscapeDataString(direction)}",
                $"per_page={perPage}",
                $"page={page}"
            };
            if (!string.IsNullOrWhiteSpace(labels))
                qs.Add($"labels={Uri.EscapeDataString(labels)}");
            if (!string.IsNullOrWhiteSpace(assignee))
                qs.Add($"assignee={Uri.EscapeDataString(assignee)}");

            var url = $"repos/{owner}/{repo}/issues?{string.Join("&", qs)}";
            using var resp = await _http.GetAsync(url, cancellationToken);
            await EnsureSuccessOrThrowAsync(resp, cancellationToken);

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            var issues = await JsonSerializer.DeserializeAsync<List<GitHubIssue>>(
                             stream,
                             JsonOpts,
                             cancellationToken)
                         ?? new List<GitHubIssue>();

            if (excludePullRequests)
                issues = issues.Where(i => i.PullRequest == null).ToList();

            var (next, last, prev) = ParseLinkHeader(resp.Headers.TryGetValues("Link", out var v) ? string.Join(",", v) : null);
            return (issues, next, last, prev);
        }

        public async Task<GitHubIssue?> GetIssueAsync(
            string owner,
            string repo,
            int number,
            CancellationToken cancellationToken = default)
        {
            using var resp = await _http.GetAsync(
                $"repos/{owner}/{repo}/issues/{number}",
                cancellationToken);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessOrThrowAsync(resp, cancellationToken);
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<GitHubIssue>(
                stream,
                JsonOpts,
                cancellationToken);
        }

        public async Task<List<GitHubComment>> GetIssueCommentsAsync(
            string owner,
            string repo,
            int number,
            int perPage = 100,
            int page = 1,
            CancellationToken cancellationToken = default)
        {
            using var resp = await _http.GetAsync(
                $"repos/{owner}/{repo}/issues/{number}/comments?per_page={perPage}&page={page}",
                cancellationToken);
            await EnsureSuccessOrThrowAsync(resp, cancellationToken);
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<List<GitHubComment>>(
                       stream,
                       JsonOpts,
                       cancellationToken)
                   ?? new List<GitHubComment>();
        }

        /// <summary>
        /// Searches issues in a single repository using the GitHub search API.
        /// </summary>
        public async Task<GitHubSearchResponse?> SearchIssuesAsync(
            string owner,
            string repo,
            string query,
            string state = "open",
            int page = 1,
            int perPage = 30,
            CancellationToken cancellationToken = default,
            string itemType = "issue")
        {
            var typeQualifier = itemType.ToLowerInvariant() switch
            {
                "issue" => " is:issue",
                "pr" or "pull_request" => " is:pr",
                "all" => string.Empty,
                _ => throw new ArgumentException(
                    "itemType must be issue, pr, or all.",
                    nameof(itemType))
            };
            var q = $"repo:{owner}/{repo}{typeQualifier}";
            if (state == "open" || state == "closed")
                q += $" is:{state}";
            if (!string.IsNullOrWhiteSpace(query))
                q += " " + query;

            var url = $"search/issues?q={Uri.EscapeDataString(q)}&per_page={perPage}&page={page}";
            using var resp = await _http.GetAsync(url, cancellationToken);
            await EnsureSuccessOrThrowAsync(resp, cancellationToken);
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<GitHubSearchResponse>(
                stream,
                JsonOpts,
                cancellationToken);
        }

        // Reads the response body on failure and throws a GitHubApiException
        // carrying the HTTP status code, GitHub error message, and rate-limit
        // headers so the controller can return a useful error to the UI.
        private static async Task EnsureSuccessOrThrowAsync(
            HttpResponseMessage resp,
            CancellationToken cancellationToken)
        {
            if (resp.IsSuccessStatusCode) return;

            string body = "";
            try
            {
                body = await resp.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                body = string.Empty;
            }

            string? ghMessage = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m))
                    ghMessage = m.GetString();
            }
            catch { /* body wasn't JSON */ }

            string? rateRemaining = resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rr)
                ? string.Join(",", rr) : null;
            string? rateLimit = resp.Headers.TryGetValues("X-RateLimit-Limit", out var rl)
                ? string.Join(",", rl) : null;

            throw new GitHubApiException(
                (int)resp.StatusCode,
                ghMessage ?? resp.ReasonPhrase ?? "GitHub API error",
                rateLimit,
                rateRemaining,
                body);
        }

        // Parses GitHub's pagination Link header (RFC 5988) into the page numbers.
        private static (int? Next, int? Last, int? Prev) ParseLinkHeader(string? header)
        {
            if (string.IsNullOrEmpty(header)) return (null, null, null);

            int? Find(string rel)
            {
                foreach (var raw in header.Split(','))
                {
                    var part = raw.Trim();
                    if (!part.Contains($"rel=\"{rel}\"", StringComparison.OrdinalIgnoreCase)) continue;

                    var start = part.IndexOf('<');
                    var end = part.IndexOf('>');
                    if (start < 0 || end <= start) continue;

                    var url = part.Substring(start + 1, end - start - 1);
                    var qIdx = url.IndexOf('?');
                    if (qIdx < 0) continue;

                    foreach (var kv in url[(qIdx + 1)..].Split('&'))
                    {
                        var pair = kv.Split('=', 2);
                        if (pair.Length == 2 && pair[0].Equals("page", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(pair[1], out var n))
                            return n;
                    }
                }
                return null;
            }

            return (Find("next"), Find("last"), Find("prev"));
        }
    }

    // ---------------------------------------------------------------------
    // DTOs (only the fields we actually surface in the UI)
    // ---------------------------------------------------------------------
    public class GitHubIssue
    {
        public long Id { get; set; }
        public int Number { get; set; }
        public string? Title { get; set; }
        public string? State { get; set; }
        public string? Body { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("closed_at")]
        public DateTime? ClosedAt { get; set; }

        public int Comments { get; set; }

        public GitHubUser? User { get; set; }
        public List<GitHubUser>? Assignees { get; set; }
        public List<GitHubLabel>? Labels { get; set; }

        // Present when the item is actually a pull request — used to filter them out.
        [JsonPropertyName("pull_request")]
        public JsonElement? PullRequest { get; set; }
    }

    public class GitHubUser
    {
        public string? Login { get; set; }
        [JsonPropertyName("avatar_url")]
        public string? AvatarUrl { get; set; }
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    public class GitHubLabel
    {
        public string? Name { get; set; }
        public string? Color { get; set; }
        public string? Description { get; set; }
    }

    public class GitHubComment
    {
        public long Id { get; set; }
        public string? Body { get; set; }
        public GitHubUser? User { get; set; }
        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }
        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    public class GitHubSearchResponse
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }
        [JsonPropertyName("incomplete_results")]
        public bool IncompleteResults { get; set; }
        public List<GitHubIssue>? Items { get; set; }
    }

    public class GitHubApiException : Exception
    {
        public int StatusCode { get; }
        public string? RateLimit { get; }
        public string? RateRemaining { get; }
        public string ResponseBody { get; }

        public GitHubApiException(int statusCode, string message, string? rateLimit, string? rateRemaining, string responseBody)
            : base(message)
        {
            StatusCode = statusCode;
            RateLimit = rateLimit;
            RateRemaining = rateRemaining;
            ResponseBody = responseBody;
        }
    }
}
