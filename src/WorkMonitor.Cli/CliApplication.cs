using Microsoft.Extensions.Configuration;
using WorkMonitor.Agent;
using WorkMonitor.Core.Services;

namespace WorkMonitor.Cli;

public sealed class CliApplication
{
    private readonly SessionStore _sessions;
    private readonly AgentService _agent;
    private readonly ConversationCompactor _compactor;
    private readonly DevOpsTokenService _devOpsTokenService;
    private readonly IAgentModel _model;
    private readonly AgentToolRegistry _tools;
    private readonly int _maxContextCharacters;
    private readonly int _retainRecentMessages;
    private readonly List<AgentWorkingItem> _workingSet = new();
    private StoredSession? _currentSession;
    private bool _dryRun = true;
    private CancellationTokenSource? _activeTurn;

    public CliApplication(
        SessionStore sessions,
        AgentService agent,
        ConversationCompactor compactor,
        DevOpsTokenService devOpsTokenService,
        IAgentModel model,
        AgentToolRegistry tools,
        IConfiguration configuration)
    {
        _sessions = sessions;
        _agent = agent;
        _compactor = compactor;
        _devOpsTokenService = devOpsTokenService;
        _model = model;
        _tools = tools;
        _maxContextCharacters =
            configuration.GetValue("WorkMonitor:MaxContextCharacters", 60000);
        _retainRecentMessages =
            configuration.GetValue("WorkMonitor:RetainRecentMessages", 12);
    }

    public async Task<int> RunAsync(string[] args)
    {
        await _sessions.InitializeAsync();
        _currentSession = await _sessions.LoadLatestAsync()
            ?? await _sessions.CreateAsync(_model.ModelName);

        Console.CancelKeyPress += OnCancelKeyPress;
        try
        {
            PrintBanner();

            var promptIndex = Array.FindIndex(
                args,
                argument => argument.Equals("--prompt", StringComparison.OrdinalIgnoreCase));
            if (promptIndex >= 0)
            {
                if (promptIndex + 1 >= args.Length)
                {
                    Console.Error.WriteLine("--prompt requires text.");
                    return 2;
                }

                var prompt = string.Join(' ', args.Skip(promptIndex + 1));
                return await RunTurnAsync(prompt) ? 0 : 1;
            }

            while (true)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("you> ");
                Console.ResetColor();

                var input = Console.ReadLine();
                if (input == null)
                {
                    return 0;
                }

                input = input.Trim();
                if (input.Length == 0)
                {
                    continue;
                }

                if (input.StartsWith('/'))
                {
                    if (!await HandleCommandAsync(input))
                    {
                        return 0;
                    }

                    continue;
                }

                await RunTurnAsync(input);
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            _activeTurn?.Dispose();
        }
    }

    private async Task<bool> RunTurnAsync(string prompt)
    {
        if (_currentSession == null)
        {
            throw new InvalidOperationException("No active session.");
        }

        using var turnCts = new CancellationTokenSource();
        _activeTurn = turnCts;
        AgentContext? context = null;
        var finalAnswer = string.Empty;
        var succeeded = false;
        var sessionId = _currentSession.Id;

        try
        {
            if (prompt.Length > _maxContextCharacters / 2)
            {
                WriteStatus(
                    $"Prompt is too large ({prompt.Length} characters). " +
                    $"Limit it to {_maxContextCharacters / 2} characters.",
                    ConsoleColor.Red);
                return false;
            }

            var session = await _sessions.LoadAsync(sessionId, turnCts.Token)
                ?? throw new InvalidOperationException(
                    $"Session '{sessionId}' no longer exists.");
            var fixedContextCharacters =
                prompt.Length +
                20000 +
                _workingSet.Sum(item =>
                    (item.Title?.Length ?? 0) +
                    (item.AreaPath?.Length ?? 0) +
                    (item.AssignedTo?.Length ?? 0) +
                    100);

            if (_compactor.NeedsCompaction(
                session.Summary,
                session.Messages,
                _maxContextCharacters,
                fixedContextCharacters))
            {
                if (session.Messages.Count <= 2)
                {
                    WriteStatus(
                        "The conversation context is too large to compact safely. " +
                        "Use /new or shorten the current prompt.",
                        ConsoleColor.Red);
                    return false;
                }

                try
                {
                    var retainCount = Math.Min(
                        _retainRecentMessages,
                        Math.Max(2, session.Messages.Count / 2));
                    var compacted = await _compactor.CompactAsync(
                        session.Summary,
                        session.Messages,
                        retainCount,
                        turnCts.Token);
                    await _sessions.ReplaceHistoryAsync(
                        session.Id,
                        compacted.Summary,
                        compacted.RetainedMessages,
                        turnCts.Token);
                    session = await _sessions.LoadAsync(session.Id, turnCts.Token)
                        ?? throw new InvalidOperationException(
                            $"Session '{session.Id}' disappeared after compaction.");
                    WriteStatus("Context compacted.", ConsoleColor.DarkGray);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    WriteStatus(
                        $"Context compaction failed; the full history was preserved: {ex.Message}",
                        ConsoleColor.Yellow);
                }

                if (_compactor.NeedsCompaction(
                    session.Summary,
                    session.Messages,
                    _maxContextCharacters,
                    fixedContextCharacters))
                {
                    WriteStatus(
                        "The conversation still exceeds the configured context budget. " +
                        "Use /new or shorten the prompt.",
                        ConsoleColor.Red);
                    return false;
                }
            }

            var now = DateTimeOffset.UtcNow;
            await _sessions.AppendMessageAsync(
                session.Id,
                new AgentConversationMessage("user", prompt, now),
                turnCts.Token);

            if (session.Messages.Count == 0)
            {
                var title = prompt.Length <= 60 ? prompt : prompt[..57] + "...";
                await _sessions.UpdateTitleAsync(session.Id, title, turnCts.Token);
            }

            context = new AgentContext
            {
                UserName = Environment.UserName,
                SessionId = session.Id,
                DryRun = _dryRun,
                DevOpsTokenProvider = cancellationToken =>
                    _devOpsTokenService.GetTokenAsync(cancellationToken)
            };

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("agent> ");
            Console.ResetColor();

            await foreach (var agentEvent in _agent.RunStreamingAsync(
                prompt,
                skill: null,
                context,
                _workingSet,
                session.Messages,
                session.Summary,
                turnCts.Token))
            {
                switch (agentEvent.Kind)
                {
                    case AgentEventKind.TextDelta:
                        Console.Write(agentEvent.Content);
                        break;

                    case AgentEventKind.ToolStarted:
                        Console.WriteLine();
                        WriteStatus(
                            $"tool -> {agentEvent.ToolName} {agentEvent.ToolArguments}",
                            ConsoleColor.DarkCyan);
                        break;

                    case AgentEventKind.ApprovalRequested:
                        WriteStatus(
                            $"approval -> {agentEvent.ToolName}",
                            ConsoleColor.Yellow);
                        break;

                    case AgentEventKind.ToolCompleted:
                        WriteStatus(
                            $"tool <- {agentEvent.ToolName}",
                            ConsoleColor.DarkGreen);
                        await PersistToolOutcomeAsync(
                            session.Id,
                            agentEvent,
                            CancellationToken.None);
                        break;

                    case AgentEventKind.Warning:
                        WriteStatus(agentEvent.Content ?? "Agent warning.", ConsoleColor.Yellow);
                        break;

                    case AgentEventKind.Error:
                        WriteStatus(agentEvent.Content ?? "Agent error.", ConsoleColor.Red);
                        break;

                    case AgentEventKind.Completed:
                        finalAnswer = agentEvent.Content ?? string.Empty;
                        succeeded = true;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            WriteStatus("Turn cancelled. The session is still available.", ConsoleColor.Yellow);
        }
        catch (Exception ex)
        {
            WriteStatus($"Turn failed: {ex.Message}", ConsoleColor.Red);
        }
        finally
        {
            _activeTurn = null;
            Console.WriteLine();
        }

        if (succeeded && !string.IsNullOrWhiteSpace(finalAnswer))
        {
            await _sessions.AppendMessageAsync(
                sessionId,
                new AgentConversationMessage(
                    "assistant",
                    finalAnswer,
                    DateTimeOffset.UtcNow),
                CancellationToken.None);
        }

        if (context != null)
        {
            await _sessions.AppendAuditEntriesAsync(
                sessionId,
                context.AuditLog,
                CancellationToken.None);
        }

        _currentSession = await _sessions.LoadAsync(
            sessionId,
            CancellationToken.None);
        return succeeded;
    }

    private async Task PersistToolOutcomeAsync(
        string sessionId,
        AgentEvent agentEvent,
        CancellationToken cancellationToken)
    {
        var toolName = agentEvent.ToolName ?? "unknown";
        var arguments = LimitStoredText(agentEvent.ToolArguments, 12000);
        var result = LimitStoredText(agentEvent.Content, 16000);
        var content =
            $"Tool '{toolName}' completed.\n" +
            $"Arguments: {arguments}\n" +
            $"Result: {result}";

        await _sessions.AppendMessageAsync(
            sessionId,
            new AgentConversationMessage(
                "tool",
                content,
                DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private static string LimitStoredText(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength] + "\n[truncated]";
    }

    private async Task<bool> HandleCommandAsync(string input)
    {
        var parts = input.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();

        switch (command)
        {
            case "/exit":
            case "/quit":
                return false;

            case "/help":
                PrintInteractiveHelp();
                return true;

            case "/new":
                _currentSession = await _sessions.CreateAsync(_model.ModelName);
                _workingSet.Clear();
                WriteStatus($"Started session {_currentSession.Id}.", ConsoleColor.Green);
                return true;

            case "/sessions":
                await ListSessionsAsync();
                return true;

            case "/resume":
                if (parts.Length != 2)
                {
                    WriteStatus("Usage: /resume <session-id>", ConsoleColor.Yellow);
                    return true;
                }

                var session = await _sessions.LoadAsync(parts[1]);
                if (session == null)
                {
                    WriteStatus($"Session '{parts[1]}' was not found.", ConsoleColor.Red);
                    return true;
                }

                _currentSession = session;
                _workingSet.Clear();
                WriteStatus($"Resumed '{session.Title}' ({session.Id}).", ConsoleColor.Green);
                return true;

            case "/model":
                Console.WriteLine($"Deployment: {_model.ModelName}");
                return true;

            case "/tools":
                foreach (var tool in _tools.All.OrderBy(tool => tool.Name))
                {
                    Console.WriteLine(
                        $"{tool.Name,-28} {(tool.IsWriteTool ? "[write] " : string.Empty)}{tool.Description}");
                }
                return true;

            case "/audit":
                if (_currentSession == null)
                {
                    WriteStatus("No active session.", ConsoleColor.Red);
                    return true;
                }

                var entries = await _sessions.ListAuditEntriesAsync(_currentSession.Id);
                if (entries.Count == 0)
                {
                    Console.WriteLine("No write audit entries for this session.");
                    return true;
                }

                foreach (var entry in entries.Reverse())
                {
                    Console.WriteLine($"{entry.CreatedAt.LocalDateTime:g}  {entry.Entry}");
                }
                return true;

            case "/dry-run":
                _dryRun = true;
                WriteStatus("Dry-run mode enabled.", ConsoleColor.Green);
                return true;

            case "/live":
                _dryRun = false;
                WriteStatus(
                    "Live mode enabled. Every write still requires explicit approval.",
                    ConsoleColor.Yellow);
                return true;

            case "/working-set":
                HandleWorkingSet(parts);
                return true;

            default:
                WriteStatus($"Unknown command '{parts[0]}'. Use /help.", ConsoleColor.Yellow);
                return true;
        }
    }

    private async Task ListSessionsAsync()
    {
        var sessions = await _sessions.ListAsync();
        if (sessions.Count == 0)
        {
            Console.WriteLine("No saved sessions.");
            return;
        }

        foreach (var session in sessions)
        {
            var current = session.Id == _currentSession?.Id ? "*" : " ";
            Console.WriteLine(
                $"{current} {session.Id}  {session.UpdatedAt.LocalDateTime:g}  {session.Title}");
        }
    }

    private void HandleWorkingSet(IReadOnlyList<string> parts)
    {
        if (parts.Count == 1)
        {
            if (_workingSet.Count == 0)
            {
                Console.WriteLine("Working set is empty.");
                return;
            }

            foreach (var item in _workingSet)
            {
                Console.WriteLine($"{item.Id}  {item.Type ?? "WorkItem"}  {item.Title ?? string.Empty}");
            }
            return;
        }

        if (parts.Count == 2 &&
            parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _workingSet.Clear();
            WriteStatus("Working set cleared.", ConsoleColor.Green);
            return;
        }

        if (parts.Count is 3 or 4 &&
            parts[1].Equals("add", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(parts[2], out var id))
        {
            if (_workingSet.All(item => item.Id != id))
            {
                _workingSet.Add(new AgentWorkingItem
                {
                    Id = id,
                    Type = parts.Count == 4 ? parts[3] : "WorkItem"
                });
            }
            WriteStatus($"Pinned work item {id}.", ConsoleColor.Green);
            return;
        }

        WriteStatus(
            "Usage: /working-set, /working-set add <id> [type], or /working-set clear",
            ConsoleColor.Yellow);
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        if (_activeTurn == null)
        {
            return;
        }

        eventArgs.Cancel = true;
        _activeTurn.Cancel();
    }

    private void PrintBanner()
    {
        Console.WriteLine($"WorkMonitor {Program.Version}");
        Console.WriteLine($"Model deployment: {_model.ModelName}");
        Console.WriteLine($"Session: {_currentSession?.Id} ({_currentSession?.Title})");
        Console.WriteLine("Mode: dry-run. Use /live to enable approved writes.");
        Console.WriteLine("Type /help for commands.");
        Console.WriteLine();
    }

    private static void PrintInteractiveHelp()
    {
        Console.WriteLine(
            """
            /new                         start a new conversation
            /sessions                    list saved conversations
            /resume <session-id>         resume a conversation
            /model                       show the active deployment
            /tools                       list agent tools
            /audit                       show write audit entries
            /working-set                 show pinned work items
            /working-set add <id> [type] pin a work item
            /working-set clear           clear pinned work items
            /dry-run                     prevent writes
            /live                        allow individually approved writes
            /exit                        exit
            Ctrl+C                       cancel the active turn
            """);
    }

    private static void WriteStatus(string message, ConsoleColor color)
    {
        var previousColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }
}
