using Azure.Core;
using Azure.Identity;

namespace WorkMonitor.Core.Services
{
    /// <summary>
    /// Acquires Azure DevOps access tokens using the app's own identity
    /// (managed identity in Azure, Visual Studio / Azure CLI locally).
    /// No user_impersonation permission or admin consent required —
    /// just add the identity as a user in the Azure DevOps organization.
    /// </summary>
    public class DevOpsTokenService
    {
        private readonly TokenCredential _credential;

        // Azure DevOps resource ID
        private static readonly string[] Scopes =
            new[] { "499b84ac-1321-427f-aa17-267ca6975798/.default" };

        public DevOpsTokenService()
            : this(new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeInteractiveBrowserCredential = false
            }))
        {
        }

        public DevOpsTokenService(TokenCredential credential)
        {
            _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(Scopes), cancellationToken);
            return token.Token;
        }
    }
}
