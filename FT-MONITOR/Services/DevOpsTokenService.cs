using Azure.Core;
using Azure.Identity;

namespace FT_MONITOR.Services
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
        {
            // In Azure: uses the App Service system-assigned managed identity.
            // Locally: falls back to Visual Studio or Azure CLI credentials.
            _credential = new DefaultAzureCredential();
        }

        public async Task<string> GetTokenAsync()
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(Scopes), CancellationToken.None);
            return token.Token;
        }
    }
}
