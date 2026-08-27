using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace FT_MONITOR.Services
{
    public class AzureOpenAIService
    {
        private readonly ChatClient _chatClient;

        public AzureOpenAIService(IConfiguration configuration)
        {
            var endpoint = configuration["AzureOpenAI:Endpoint"]
                ?? throw new InvalidOperationException("AzureOpenAI:Endpoint not configured.");
            var apiKey = configuration["AzureOpenAI:ApiKey"]
                ?? throw new InvalidOperationException("AzureOpenAI:ApiKey not configured.");
            var deploymentName = configuration["AzureOpenAI:DeploymentName"]
                ?? throw new InvalidOperationException("AzureOpenAI:DeploymentName not configured.");

            var azureClient = new AzureOpenAIClient(
                new Uri(endpoint),
                new AzureKeyCredential(apiKey));

            _chatClient = azureClient.GetChatClient(deploymentName);
        }

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
    }
}