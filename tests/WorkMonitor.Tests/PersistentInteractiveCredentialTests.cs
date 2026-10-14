using WorkMonitor.Core.Services;
using Azure.Core;
using Azure.Identity;

namespace WorkMonitor.Tests;

public sealed class PersistentInteractiveCredentialTests
{
    [Fact]
    public async Task MissingAuthenticationRecordDoesNotPromptDuringStatusCheck()
    {
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "persistent-credential-test-data",
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
    public void InteractiveAuthenticationRemainsEnabledByDefaultForCliCallers()
    {
        var options = new PersistentInteractiveCredentialOptions(
            "72f988bf-86f1-41af-91ab-2d7cd011db47",
            "user@example.com", "WorkMonitor.Tests", "authentication-record.json");

        Assert.True(options.AllowInteractiveAuthentication);
    }

    [Fact]
    public async Task CachedOnlyMissingRecordCannotLaunchInteractiveAuthentication()
    {
        using var fixture = new CachedOnlyFixture();

        var error = await Assert.ThrowsAsync<CredentialUnavailableException>(
            () => fixture.Credential.GetTokenAsync(Request(), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("--auth-login", error.Message);
        Assert.Null(fixture.Credential.AuthenticatedUsername);
        Assert.False(File.Exists(fixture.RecordPath));
        Assert.Empty(Directory.GetFileSystemEntries(fixture.DirectoryPath));
    }

    [Fact]
    public void SynchronousCachedOnlyMissingRecordCannotLaunchInteractiveAuthentication()
    {
        using var fixture = new CachedOnlyFixture();

        Assert.Throws<CredentialUnavailableException>(
            () => fixture.Credential.GetToken(Request(), CancellationToken.None));

        Assert.False(File.Exists(fixture.RecordPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedOnlyExplicitAuthenticateDoesNotResetExistingRecordOrLaunchBrowser(bool force)
    {
        using var fixture = new CachedOnlyFixture();
        await File.WriteAllTextAsync(fixture.RecordPath, "preserve-existing-cli-record");

        var error = await Assert.ThrowsAsync<CredentialUnavailableException>(
            () => fixture.Credential.AuthenticateAsync(force).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("--auth-login", error.Message);
        Assert.Equal("preserve-existing-cli-record", await File.ReadAllTextAsync(fixture.RecordPath));
    }

    [Fact]
    public async Task CachedOnlyTokenAcquisitionHonorsPreCancellationWithoutCreatingRecord()
    {
        using var fixture = new CachedOnlyFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Credential.GetTokenAsync(Request(), cancellation.Token).AsTask());
        Assert.ThrowsAny<OperationCanceledException>(
            () => fixture.Credential.GetToken(Request(), cancellation.Token));

        Assert.False(File.Exists(fixture.RecordPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedOnlyExplicitAuthenticateHonorsCancellationBeforeReset(bool force)
    {
        using var fixture = new CachedOnlyFixture();
        await File.WriteAllTextAsync(fixture.RecordPath, "preserve-existing-cli-record");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Credential.AuthenticateAsync(force, cancellation.Token));

        Assert.Equal("preserve-existing-cli-record", await File.ReadAllTextAsync(fixture.RecordPath));
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

    private static TokenRequestContext Request()
        => new(new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" });

    private sealed class CachedOnlyFixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            AppContext.BaseDirectory, "persistent-credential-test-data", Guid.NewGuid().ToString("N"));
        public string RecordPath => Path.Combine(DirectoryPath, "authentication-record.json");
        public PersistentInteractiveCredential Credential { get; }

        public CachedOnlyFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Credential = new PersistentInteractiveCredential(new PersistentInteractiveCredentialOptions(
                "72f988bf-86f1-41af-91ab-2d7cd011db47",
                "user@example.com", "WorkMonitor.Tests", RecordPath,
                AllowInteractiveAuthentication: false));
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
