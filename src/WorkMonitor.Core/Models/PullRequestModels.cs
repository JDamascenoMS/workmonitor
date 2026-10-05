using Newtonsoft.Json;

namespace WorkMonitor.Core.Models;

public sealed class AzureDevOpsConnectionData
{
    [JsonProperty("authenticatedUser")]
    public AzureDevOpsIdentity? AuthenticatedUser { get; set; }
}

public sealed class AzureDevOpsIdentity
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("providerDisplayName")]
    public string? ProviderDisplayName { get; set; }

    [JsonProperty("customDisplayName")]
    public string? CustomDisplayName { get; set; }

    [JsonProperty("displayName")]
    public string? DisplayName { get; set; }
}

public sealed class AzureDevOpsPullRequestResponse
{
    [JsonProperty("count")]
    public int Count { get; set; }

    [JsonProperty("value")]
    public List<AzureDevOpsPullRequest> Value { get; set; } = new();
}

public sealed class AzureDevOpsPullRequest
{
    [JsonProperty("pullRequestId")]
    public int PullRequestId { get; set; }

    [JsonProperty("title")]
    public string? Title { get; set; }

    [JsonProperty("status")]
    public string? Status { get; set; }

    [JsonProperty("creationDate")]
    public DateTimeOffset CreationDate { get; set; }

    [JsonProperty("sourceRefName")]
    public string? SourceRefName { get; set; }

    [JsonProperty("targetRefName")]
    public string? TargetRefName { get; set; }

    [JsonProperty("repository")]
    public AzureDevOpsRepository? Repository { get; set; }

    [JsonProperty("createdBy")]
    public AzureDevOpsIdentity? CreatedBy { get; set; }

    [JsonProperty("reviewers")]
    public List<AzureDevOpsReviewer> Reviewers { get; set; } = new();
}

public sealed class AzureDevOpsRepository
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }
}

public sealed class AzureDevOpsReviewer
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("displayName")]
    public string? DisplayName { get; set; }

    [JsonProperty("vote")]
    public int Vote { get; set; }

    [JsonProperty("isRequired")]
    public bool IsRequired { get; set; }
}

public sealed record PullRequestReviewItem(
    int PullRequestId,
    string Title,
    string Repository,
    string SourceBranch,
    string TargetBranch,
    string Author,
    DateTimeOffset CreatedAt,
    int? MyVote,
    bool IsRequired,
    string WebUrl);
