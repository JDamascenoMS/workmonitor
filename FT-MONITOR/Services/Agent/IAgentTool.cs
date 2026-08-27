using System.Text.Json;

namespace FT_MONITOR.Services.Agent
{
    /// <summary>
    /// A single capability the agent can invoke. Tools are deterministic, typed,
    /// and registered with DI so the LLM can never invent code paths — it can
    /// only pick from the registered list.
    /// </summary>
    public interface IAgentTool
    {
        /// <summary>Stable name surfaced to the LLM (snake_case, no spaces).</summary>
        string Name { get; }

        /// <summary>Plain-language description shown to the LLM. Be specific about when to use it.</summary>
        string Description { get; }

        /// <summary>JSON-schema describing the tool's parameters object.</summary>
        JsonElement ParametersSchema { get; }

        /// <summary>Whether this tool mutates state (DevOps comment, GitHub label, etc.).</summary>
        bool IsWriteTool { get; }

        /// <summary>
        /// Executes the tool. The argument JSON is whatever the LLM produced; the
        /// implementation is responsible for validating + parsing it. The return
        /// value is serialized back to the LLM as the tool result.
        /// </summary>
        Task<object> ExecuteAsync(JsonElement arguments, AgentContext context, CancellationToken ct);
    }
}
