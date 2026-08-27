using System.Text.Json;
using OpenAI.Chat;

namespace FT_MONITOR.Services.Agent
{
    /// <summary>
    /// Orchestrates one agent run: takes the user's request + an optional skill,
    /// drives the OpenAI function-calling loop against AzureOpenAIService until
    /// the model produces a final answer (or we hit MaxTurns), and returns a
    /// transcript that the UI can render.
    /// </summary>
    public class AgentService
    {
        private const int MaxTurns = 6;

        private readonly AzureOpenAIService _ai;
        private readonly AgentToolRegistry _tools;
        private readonly ILogger<AgentService> _log;

        public AgentService(AzureOpenAIService ai, AgentToolRegistry tools, ILogger<AgentService> log)
        {
            _ai = ai;
            _tools = tools;
            _log = log;
        }

        public async Task<AgentRunResult> RunAsync(
            string userPrompt,
            AgentSkill? skill,
            AgentContext context,
            IReadOnlyList<AgentWorkingItem>? workingSet = null,
            CancellationToken ct = default)
        {
            var transcript = new List<AgentTranscriptEntry>();

            var systemPrompt =
                "You are FT-MONITOR's triage agent for the DevDiv organization. " +
                "You help engineers search Azure DevOps work items, read GitHub issues, summarize them, " +
                "and (when asked) post comments on DevOps work items. " +
                "Always prefer calling a tool to gather facts before answering. " +
                "Do not invent work item ids or content. " +
                $"Current user: {context.UserName} <{context.UserEmail}>. " +
                $"Mode: {(context.DryRun ? "DRY RUN (write tools will not mutate state)" : "LIVE (write tools will mutate state)")}. " +
                "If the user asks to perform a write while in DRY RUN mode, still call the tool — it will return a description of what would be done.";

            if (workingSet is { Count: > 0 })
            {
                // Bind every action and answer to this exact set of items unless the
                // user explicitly tells the agent to look outside it.
                var lines = workingSet
                    .Select(w => $"- id={w.Id}  type={w.Type ?? "WorkItem"}  state={w.State ?? "?"}  priority={w.Priority?.ToString() ?? "?"}  area={w.AreaPath ?? "?"}  assignedTo={w.AssignedTo ?? "?"}  title={w.Title ?? ""}");

                systemPrompt +=
                    "\n\nWORKING_SET — every question and action below applies ONLY to these items unless the user explicitly says otherwise. " +
                    "Treat their ids as authoritative; do not search for similar items unless asked. " +
                    "When you need details (description, comments), call get_work_item with one of these ids.\n" +
                    string.Join("\n", lines);
            }

            if (skill != null && !string.IsNullOrWhiteSpace(skill.SystemPromptAddendum))
            {
                systemPrompt += "\n\nSkill instructions:\n" + skill.SystemPromptAddendum;
            }

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            // Whitelist tools to those declared by the skill, if any.
            var toolList = (skill?.SuggestedTools is { Length: > 0 })
                ? _tools.All.Where(t => skill.SuggestedTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : _tools.All.ToList();

            var chatTools = toolList.Select(BuildChatTool).ToList();

            for (var turn = 1; turn <= MaxTurns; turn++)
            {
                ChatCompletion completion;
                try
                {
                    completion = await _ai.CompleteChatWithToolsAsync(messages, chatTools);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Agent LLM call failed on turn {Turn}", turn);
                    transcript.Add(new AgentTranscriptEntry { Role = "error", Content = $"LLM call failed: {ex.Message}" });
                    return new AgentRunResult { FinalAnswer = "(model error)", Transcript = transcript, AuditLog = context.AuditLog };
                }

                // No tool calls -> we have a final answer.
                if (completion.ToolCalls == null || completion.ToolCalls.Count == 0)
                {
                    var finalText = string.Concat(completion.Content.Select(c => c.Text));
                    transcript.Add(new AgentTranscriptEntry { Role = "assistant", Content = finalText });
                    return new AgentRunResult { FinalAnswer = finalText, Transcript = transcript, AuditLog = context.AuditLog };
                }

                // The assistant's tool-call message must be added to history before tool results.
                messages.Add(new AssistantChatMessage(completion));

                foreach (var call in completion.ToolCalls)
                {
                    var toolName = call.FunctionName;
                    var argsJson = call.FunctionArguments?.ToString() ?? "{}";

                    transcript.Add(new AgentTranscriptEntry
                    {
                        Role = "tool_call",
                        ToolName = toolName,
                        ToolArgs = argsJson
                    });

                    string resultJson;
                    try
                    {
                        if (!_tools.TryGet(toolName, out var tool))
                        {
                            resultJson = JsonSerializer.Serialize(new { error = $"Unknown tool '{toolName}'." });
                        }
                        else
                        {
                            using var argsDoc = JsonDocument.Parse(argsJson);
                            var result = await tool.ExecuteAsync(argsDoc.RootElement, context, ct);
                            resultJson = JsonSerializer.Serialize(result);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Tool {Tool} failed", toolName);
                        // Truncate the message — we don't want to feed full stack traces back to the LLM.
                        var msg = ex.Message;
                        if (msg.Length > 500) msg = msg.Substring(0, 500);
                        resultJson = JsonSerializer.Serialize(new { error = msg });
                    }

                    transcript.Add(new AgentTranscriptEntry
                    {
                        Role = "tool_result",
                        ToolName = toolName,
                        Content = resultJson
                    });

                    messages.Add(new ToolChatMessage(call.Id, resultJson));
                }
            }

            transcript.Add(new AgentTranscriptEntry
            {
                Role = "assistant",
                Content = $"(stopped after {MaxTurns} turns without a final answer)"
            });
            return new AgentRunResult
            {
                FinalAnswer = $"(stopped after {MaxTurns} turns without a final answer)",
                Transcript = transcript,
                AuditLog = context.AuditLog
            };
        }

        // Wraps an IAgentTool as an OpenAI ChatTool that the model can pick.
        private static ChatTool BuildChatTool(IAgentTool tool)
        {
            var schemaJson = tool.ParametersSchema.GetRawText();
            return ChatTool.CreateFunctionTool(
                tool.Name,
                tool.Description,
                BinaryData.FromString(schemaJson));
        }
    }

    public class AgentRunResult
    {
        public string FinalAnswer { get; set; } = "";
        public List<AgentTranscriptEntry> Transcript { get; set; } = new();
        public List<string> AuditLog { get; set; } = new();
    }

    public class AgentTranscriptEntry
    {
        public string Role { get; set; } = ""; // "assistant" | "tool_call" | "tool_result" | "error"
        public string? Content { get; set; }
        public string? ToolName { get; set; }
        public string? ToolArgs { get; set; }
    }

    /// <summary>A reusable agent skill — a named bundle of instructions + suggested tools.</summary>
    public class AgentSkill
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string SystemPromptAddendum { get; set; } = "";
        public string[] SuggestedTools { get; set; } = Array.Empty<string>();
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// One item in the agent's working set — the rows the user picked from a
    /// bugs/feedback page and clicked "Send to Agent" on. Passed verbatim to
    /// the system prompt to ground every subsequent question.
    /// </summary>
    public class AgentWorkingItem
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }       // "Bug" | "Feedback" | etc.
        public string? State { get; set; }
        public int? Priority { get; set; }
        public string? AreaPath { get; set; }
        public string? AssignedTo { get; set; }
    }
}
