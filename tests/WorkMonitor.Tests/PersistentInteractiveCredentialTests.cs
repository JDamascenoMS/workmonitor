using WorkMonitor.Core.Services;
using Azure.Core;

namespace WorkMonitor.Tests;

public sealed class PersistentInteractiveCredentialTests
{
    [Fact]
    public async Task MissingAuthenticationRecordDoesNotPromptDuringStatusCheck()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "workmonitor-auth-tests",
            Guid.NewGuid().ToString("N"));
        var recordPath = Path.Combine(directory, "authentication-record.json");

        try
        {
            var credential = new PersistentInteractiveCredential(
                new PersistentInteractiveCredentialOptions(
                    "72f988bf-86f1-41af-91ab-2d7cd011db47",
                    "user@example.com",
                    "WorkMonitor.Tests",
                    recordPath));

            var username = await credential.GetCachedUsernameAsync(
                CancellationToken.None);

            Assert.Null(username);
            Assert.False(File.Exists(recordPath));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void RecoveryKeysDistinguishResourceAndClaimsContexts()
    {
        var openAi = new TokenRequestContext(
            new[] { "https://cognitiveservices.azure.com/.default" });
        var devOps = new TokenRequestContext(
            new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" });
        var challengedOpenAi = new TokenRequestContext(
            new[] { "https://cognitiveservices.azure.com/.default" },
            claims: """{"access_token":{"xms_cc":{"values":["cp1"]}}}""");

        Assert.NotEqual(
            PersistentInteractiveCredential.BuildRecoveryKey(openAi),
            PersistentInteractiveCredential.BuildRecoveryKey(devOps));
        Assert.NotEqual(
            PersistentInteractiveCredential.BuildRecoveryKey(openAi),
            PersistentInteractiveCredential.BuildRecoveryKey(challengedOpenAi));
    }
}
