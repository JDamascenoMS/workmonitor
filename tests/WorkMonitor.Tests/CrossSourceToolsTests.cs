using WorkMonitor.Agent.Tools;

namespace WorkMonitor.Tests;

public sealed class CrossSourceToolsTests
{
    [Theory]
    [InlineData("https://dev.azure.com/devdiv/devdiv/_workitems/edit/42", true)]
    [InlineData("https://devdiv.visualstudio.com/DevDiv/_workitems/edit/42", true)]
    [InlineData("https://dev.azure.com/other-org/project/_workitems/edit/42", false)]
    [InlineData("https://other-org.visualstudio.com/project/_workitems/edit/42", false)]
    [InlineData("https://dev.azure.com/devdiv/devdiv/_workitems/edit/420", false)]
    public void WorkItemReferencesMustMatchOrganizationAndExactId(
        string reference,
        bool expected)
    {
        Assert.Equal(
            expected,
            CorrelateWorkItemWithGitHubTool.HasDevOpsReference(reference, 42));
    }

    [Fact]
    public void ExtractsExplicitIssueAndPullRequestUrls()
    {
        var references = CorrelateWorkItemWithGitHubTool.ExtractGitHubReferences(
            """
            Related: https://github.com/microsoft/vscode-dotnettools/issues/123
            Fix: https://github.com/owner/repo/pull/456.
            """);

        Assert.Equal(2, references.Count);
        Assert.Contains(
            references,
            reference =>
                reference.Owner == "microsoft" &&
                reference.Repo == "vscode-dotnettools" &&
                reference.Kind == "issues" &&
                reference.Number == 123);
        Assert.Contains(
            references,
            reference =>
                reference.Owner == "owner" &&
                reference.Repo == "repo" &&
                reference.Kind == "pull" &&
                reference.Number == 456);
    }
}
