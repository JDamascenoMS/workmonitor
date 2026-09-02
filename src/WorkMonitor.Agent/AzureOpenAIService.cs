using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using System.Runtime.CompilerServices;
using System.Text;

namespace WorkMonitor.Agent
{
    public sealed class AzureOpenAIService : IAgentModel
    {
        private readonly ChatClient _chatClient;

        public AzureOpenAIService(IConfiguration configuration)
            : this(
                configuration,
                new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ExcludeInteractiveBrowserCredential = false
                }))
        {
        }

        public AzureOpenAIService(
            IConfiguration configuration,
            TokenCredential credential)
        {
            var endpoint = GetRequiredSetting(configuration, "AzureOpenAI:Endpoint");
            ModelName = GetRequiredSetting(configuration, "AzureOpenAI:DeploymentName");
            var apiKey = configuration["AzureOpenAI:ApiKey"];

            AzureOpenAIClient azureClient;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                azureClient = new AzureOpenAIClient(
                    new Uri(endpoint),
                    credential ?? throw new ArgumentNullException(nameof(credential)));
            }
            else
            {
                azureClient = new AzureOpenAIClient(
                    new Uri(endpoint),
                    new AzureKeyCredential(apiKey));
            }

            _chatClient = azureClient.GetChatClient(ModelName);
        }

        public string ModelName { get; }

        public async Task<string> SummarizeBug(string title, string description, string state, List<string> comments)
        {
            var commentsText = comments != null && comments.Any()
                ? string.Join("\n", comments.Select((c, i) => $"Comment {i + 1}: {c}"))
                : "No comments.";

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "You are a bug triage assistant. Summarize the bug concisely, " +
                    "highlight the key issue, current state, and suggest next steps. " +
                    "Keep the summary under 200 words."),
                new UserChatMessage(
                    $"Title: {title}\nState: {state}\nDescription: {description}\n\nComments:\n{commentsText}")
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }

        public async Task<string> GenerateCustomerResponse(string title, string description, string state, List<string> customerComments, List<string> internalComments)
        {
            var customerText = customerComments != null && customerComments.Any()
                ? string.Join("\n", customerComments.Select((c, i) => $"Customer comment {i + 1}: {c}"))
                : "No customer comments.";

            var internalText = internalComments != null && internalComments.Any()
                ? string.Join("\n", internalComments.Select((c, i) => $"Internal note {i + 1}: {c}"))
                : "No internal notes.";

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "You are a professional support engineer responding to a customer bug report. " +
                    "Write a response that follows this structure:\n" +
                    "1. Thank the customer for their report and any details they provided.\n" +
                    "2. Acknowledge the issue and briefly describe what you understand the problem to be.\n" +
                    "3. If possible, suggest a workaround based on the bug description and comments.\n" +
                    "4. Politely ask for any additional information, logs, or reproduction steps that would help investigate further.\n\n" +
                    "Keep the tone professional, empathetic, and helpful. " +
                    "Use the internal notes for context but do NOT reveal internal comments to the customer. " +
                    "Format the response in plain text, ready to be posted as a reply. " +
                    "Keep it under 300 words."),
                new UserChatMessage(
                    $"Bug Title: {title}\n" +
                    $"Current State: {state}\n" +
                    $"Description: {description}\n\n" +
                    $"Customer Comments:\n{customerText}\n\n" +
                    $"Internal Notes (DO NOT share with customer):\n{internalText}")
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }

        public async Task<string> GenerateRawCompletion(string prompt)
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("You are a code generator. Return only valid JSON, no markdown fences or extra text."),
                new UserChatMessage(prompt)
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }

        public async Task<string> ClassifyFeedbackThemes(string feedbackJson)
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "You are a feedback analyst. Given a JSON array of feedback items, " +
                    "group them into common themes. Return a JSON array where each element has: " +
                    "\"theme\" (short label), \"description\" (one sentence), \"workItemIds\" (array of matching item Id numbers), " +
                    "and \"count\" (number of items in that theme). " +
                    "Return only valid JSON, no markdown fences or extra text."),
                new UserChatMessage(feedbackJson)
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }

        public async Task<string> AssessBugProbability(string title, string description, int priority, bool isInTopTheme, List<string> comments)
        {
            var commentsText = comments != null && comments.Any()
                ? string.Join("\n", comments.Select((c, i) => $"Comment {i + 1}: {c}"))
                : "No comments.";

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "You are a software quality analyst. Evaluate the probability that the following feedback item is an actual bug. " +
                    "Consider these factors:\n" +
                    "1. Clarity of reproduction steps in the description or comments (clear steps increase probability).\n" +
                    "2. Priority level (1 is highest; higher priority items are more likely bugs).\n" +
                    "3. Whether the item belongs to a top-5 recurring theme (top themes are more likely real bugs).\n\n" +
                    "Return ONLY a JSON object with these fields:\n" +
                    "- \"probability\": a number between 0 and 100 representing the percentage chance this is a real bug.\n" +
                    "- \"confidence\": \"High\", \"Medium\", or \"Low\".\n" +
                    "- \"reasoning\": a brief explanation (under 80 words) of why you assigned that probability.\n" +
                    "Return only valid JSON, no markdown fences or extra text."),
                new UserChatMessage(
                    $"Title: {title}\n" +
                    $"Priority: {priority}\n" +
                    $"Is in a top-5 theme: {(isInTopTheme ? "Yes" : "No")}\n" +
                    $"Description: {description}\n\n" +
                    $"Comments:\n{commentsText}")
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }

        public async Task<string> RankMostRelevantTickets(string feedbackJson, string themesJson)
        {
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "You are a product triage analyst. You are given two JSON inputs:\n" +
                    "1) An array of feedback items. Each item has: Id, Title, Description, State, Priority, CreatedDate (ISO-8601).\n" +
                    "2) An array of themes previously identified across those items. Each theme has: theme, description, workItemIds, count.\n\n" +
                    "Identify the TOP 5 most relevant tickets using a weighted ranking with these criteria, in order of importance:\n" +
                    "FIRST  - score: an importance score from 0-100 you compute based on the item's signals " +
                    "(priority where 1 is highest, clarity/impact in title and description, customer pain). Higher score = more relevant.\n" +
                    "SECOND - theme weight: tickets that belong to a larger theme (higher 'count') are more relevant. " +
                    "Tickets that don't belong to any theme are less relevant.\n" +
                    "THIRD  - recent similarity: how many other tickets created in the LAST 30 DAYS (relative to the most recent CreatedDate in the input) are similar to it (same theme or near-duplicate topic). Higher count = more relevant.\n\n" +
                    "Return ONLY a JSON array with EXACTLY 5 elements (or fewer if the input has fewer items), sorted from most to least relevant. " +
                    "Each element must have these fields:\n" +
                    "- \"id\" (number): the work item id.\n" +
                    "- \"title\" (string): the ticket title.\n" +
                    "- \"score\" (number 0-100): your computed importance score.\n" +
                    "- \"themeName\" (string): the theme this ticket belongs to, or \"\" if none.\n" +
                    "- \"themeCount\" (number): the size of that theme, or 0 if none.\n" +
                    "- \"similarLastMonth\" (number): count of similar tickets created in the last 30 days (excluding this one).\n" +
                    "- \"reasoning\" (string, under 50 words): brief justification of the ranking.\n" +
                    "Return only valid JSON, no markdown fences or extra text."),
                new UserChatMessage(
                    $"FEEDBACK_ITEMS:\n{feedbackJson}\n\nTHEMES:\n{themesJson}")
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }

        public async Task<string> AnalyzeBugTrends(List<object> bugs)
        {
            var bugsText = System.Text.Json.JsonSerializer.Serialize(bugs);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(
                    "You are a software engineering analyst. Analyze the following list of bugs " +
                    "and identify patterns, common themes, and priority recommendations. " +
                    "Be concise and actionable."),
                new UserChatMessage(bugsText)
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }

        /// <summary>
        /// Performs a single chat-completion turn with tool/function calling enabled.
        /// The caller owns the message history and the tool-execution loop; this method
        /// just makes one round-trip to the model and returns its raw response.
        /// </summary>
        public async Task<ChatCompletion> CompleteChatWithToolsAsync(
            IList<ChatMessage> messages,
            IEnumerable<ChatTool> tools)
        {
            var options = new ChatCompletionOptions();
            foreach (var t in tools) options.Tools.Add(t);

            var response = await _chatClient.CompleteChatAsync(messages, options);
            return response.Value;
        }

        public async IAsyncEnumerable<AgentModelUpdate> StreamAsync(
            IReadOnlyList<AgentModelMessage> messages,
            IReadOnlyList<AgentToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var nativeMessages = messages.Select(ToChatMessage).ToList();
            var options = new ChatCompletionOptions();
            foreach (var tool in tools)
            {
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    tool.Name,
                    tool.Description,
                    BinaryData.FromString(tool.ParametersJson)));
            }

            var content = new StringBuilder();
            var toolCalls = new Dictionary<int, StreamingToolCallBuilder>();
            var updates = _chatClient.CompleteChatStreamingAsync(
                nativeMessages,
                options,
                cancellationToken);

            await foreach (var update in updates.WithCancellation(cancellationToken))
            {
                foreach (var part in update.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text))
                    {
                        continue;
                    }

                    content.Append(part.Text);
                    yield return new AgentModelUpdate(TextDelta: part.Text);
                }

                foreach (var toolUpdate in update.ToolCallUpdates)
                {
                    if (!toolCalls.TryGetValue(toolUpdate.Index, out var builder))
                    {
                        builder = new StreamingToolCallBuilder();
                        toolCalls.Add(toolUpdate.Index, builder);
                    }

                    if (!string.IsNullOrWhiteSpace(toolUpdate.ToolCallId))
                    {
                        builder.Id = toolUpdate.ToolCallId;
                    }

                    if (!string.IsNullOrWhiteSpace(toolUpdate.FunctionName))
                    {
                        builder.Name.Append(toolUpdate.FunctionName);
                    }

                    var argumentFragment = DecodeFunctionArgumentsUpdate(
                        toolUpdate.FunctionArgumentsUpdate);
                    if (!string.IsNullOrEmpty(argumentFragment))
                    {
                        builder.Arguments.Append(argumentFragment);
                    }
                }
            }

            var completedCalls = toolCalls
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.Build())
                .ToList();

            yield return new AgentModelUpdate(
                CompletedResponse: new AgentModelResponse(
                    content.ToString(),
                    completedCalls));
        }

        internal static string? DecodeFunctionArgumentsUpdate(BinaryData? update)
        {
            if (update == null)
            {
                return null;
            }

            var bytes = update.ToMemory();
            return bytes.IsEmpty
                ? null
                : Encoding.UTF8.GetString(bytes.Span);
        }

        private static ChatMessage ToChatMessage(AgentModelMessage message)
        {
            return message.Role switch
            {
                AgentModelMessageRole.System => new SystemChatMessage(message.Content),
                AgentModelMessageRole.User => new UserChatMessage(message.Content),
                AgentModelMessageRole.Assistant when message.ToolCalls is { Count: > 0 } =>
                    CreateAssistantToolMessage(message),
                AgentModelMessageRole.Assistant => new AssistantChatMessage(message.Content),
                AgentModelMessageRole.Tool => new ToolChatMessage(
                    message.ToolCallId
                        ?? throw new InvalidOperationException("Tool messages require a tool-call id."),
                    message.Content),
                _ => throw new ArgumentOutOfRangeException(nameof(message), message.Role, null)
            };
        }

        private static AssistantChatMessage CreateAssistantToolMessage(
            AgentModelMessage message)
        {
            var assistant = new AssistantChatMessage(message.ToolCalls!.Select(call =>
                ChatToolCall.CreateFunctionToolCall(
                    call.Id,
                    call.Name,
                    BinaryData.FromString(call.ArgumentsJson))));

            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                assistant.Content.Add(ChatMessageContentPart.CreateTextPart(message.Content));
            }

            return assistant;
        }

        private static string GetRequiredSetting(
            IConfiguration configuration,
            string key)
        {
            var value = configuration[key];
            return string.IsNullOrWhiteSpace(value)
                ? throw new InvalidOperationException($"{key} is not configured.")
                : value;
        }

        private sealed class StreamingToolCallBuilder
        {
            public string? Id { get; set; }
            public StringBuilder Name { get; } = new();
            public StringBuilder Arguments { get; } = new();

            public AgentToolCall Build()
            {
                if (string.IsNullOrWhiteSpace(Id))
                {
                    throw new InvalidOperationException(
                        "The model returned a tool call without an id.");
                }

                if (Name.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"The model returned tool call '{Id}' without a function name.");
                }

                return new AgentToolCall(
                    Id,
                    Name.ToString(),
                    Arguments.Length == 0 ? "{}" : Arguments.ToString());
            }
        }
    }
}