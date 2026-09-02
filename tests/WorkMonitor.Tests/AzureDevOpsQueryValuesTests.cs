using WorkMonitor.Core.Helpers;

namespace WorkMonitor.Tests;

public sealed class AzureDevOpsQueryValuesTests
{
    [Theory]
    [InlineData("Bug", "Bug")]
    [InlineData("Task", "Task")]
    [InlineData("Feature", "Feature")]
    [InlineData("UserStory", "User Story")]
    [InlineData("Feedback", "FeedbackTicket")]
    [InlineData("FeedbackTicket", "FeedbackTicket")]
    public void NormalizesAllowedWorkItemTypes(string input, string expected)
    {
        Assert.Equal(expected, AzureDevOpsQueryValues.NormalizeWorkItemType(input));
    }

    [Fact]
    public void RejectsInjectedWorkItemType()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureDevOpsQueryValues.NormalizeWorkItemType(
                "Bug' OR [System.WorkItemType] = 'Bug"));
    }

    [Fact]
    public void AgentBoundaryRejectsNonTriageWorkItemTypes()
    {
        Assert.Throws<ArgumentException>(() =>
            AzureDevOpsQueryValues.NormalizeAgentSearchWorkItemType("Task"));
    }
}
