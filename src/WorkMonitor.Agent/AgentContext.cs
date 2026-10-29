namespace WorkMonitor.Agent
{
    /// <summary>
    /// Per-request context passed to every tool invocation. Holds the caller's
    /// identity (for audit), the DevOps token (so tools can call DevOps without
    /// re-acquiring it), and the dryRun flag that controls whether write tools
    /// actually mutate state or only describe what they would do.
    /// </summary>
    public class AgentContext
    {
        private readonly SemaphoreSlim _tokenLock = new(1, 1);
        private string? _resolvedDevOpsToken;

        public string UserName { get; init; } = "unknown";
        public string UserEmail { get; init; } = "";

        public string? SessionId { get; init; }

        /// <summary>Azure DevOps access token (may be null if acquisition failed).</summary>
        public string? DevOpsToken { get; init; }

        /// <summary>Optional lazy token provider used by interactive hosts.</summary>
        public Func<CancellationToken, Task<string>>? DevOpsTokenProvider { get; init; }

        /// <summary>If true, write tools must NOT mutate state; they should return a description of the intended change.</summary>
        public bool DryRun { get; init; } = true;

        /// <summary>Append-only log of side-effects for the UI transcript.</summary>
        public List<string> AuditLog { get; } = new();

        internal HashSet<int> UnconfirmedBugCreations { get; } = new();

        public async ValueTask<string> GetRequiredDevOpsTokenAsync(
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(DevOpsToken))
            {
                return DevOpsToken;
            }

            if (!string.IsNullOrWhiteSpace(_resolvedDevOpsToken))
            {
                return _resolvedDevOpsToken;
            }

            if (DevOpsTokenProvider == null)
            {
                throw new InvalidOperationException(
                    "Azure DevOps authentication is not available for this host.");
            }

            await _tokenLock.WaitAsync(cancellationToken);
            try
            {
                _resolvedDevOpsToken ??= await DevOpsTokenProvider(cancellationToken);
                return string.IsNullOrWhiteSpace(_resolvedDevOpsToken)
                    ? throw new InvalidOperationException(
                        "Azure DevOps authentication returned an empty token.")
                    : _resolvedDevOpsToken;
            }
            finally
            {
                _tokenLock.Release();
            }
        }
    }
}
