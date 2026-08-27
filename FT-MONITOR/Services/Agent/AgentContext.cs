namespace FT_MONITOR.Services.Agent
{
    /// <summary>
    /// Per-request context passed to every tool invocation. Holds the caller's
    /// identity (for audit), the DevOps token (so tools can call DevOps without
    /// re-acquiring it), and the dryRun flag that controls whether write tools
    /// actually mutate state or only describe what they would do.
    /// </summary>
    public class AgentContext
    {
        public string UserName { get; init; } = "unknown";
        public string UserEmail { get; init; } = "";

        /// <summary>Azure DevOps access token (may be null if acquisition failed).</summary>
        public string? DevOpsToken { get; init; }

        /// <summary>If true, write tools must NOT mutate state; they should return a description of the intended change.</summary>
        public bool DryRun { get; init; } = true;

        /// <summary>Append-only log of side-effects for the UI transcript.</summary>
        public List<string> AuditLog { get; } = new();
    }
}
