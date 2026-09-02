using Microsoft.Data.Sqlite;
using WorkMonitor.Agent;

namespace WorkMonitor.Cli;

public sealed record SessionSummary(
    string Id,
    string Title,
    string Model,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StoredSession(
    string Id,
    string Title,
    string Model,
    string? Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AgentConversationMessage> Messages);

public sealed record AuditEntry(string Entry, DateTimeOffset CreatedAt);

public sealed class SessionStore
{
    private readonly string _connectionString;

    public SessionStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("Session database directory could not be determined.");
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                model TEXT NOT NULL,
                summary TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_messages_session_id
                ON messages(session_id, id);

            CREATE TABLE IF NOT EXISTS audit_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT NOT NULL,
                entry TEXT NOT NULL,
                created_at TEXT NOT NULL,
                FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredSession> CreateAsync(
        string model,
        string title = "New conversation",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions (id, title, model, summary, created_at, updated_at)
            VALUES ($id, $title, $model, NULL, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new StoredSession(
            id,
            title,
            model,
            null,
            now,
            now,
            Array.Empty<AgentConversationMessage>());
    }

    public async Task<StoredSession?> LoadAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var sessionCommand = connection.CreateCommand();
        sessionCommand.CommandText =
            """
            SELECT id, title, model, summary, created_at, updated_at
            FROM sessions
            WHERE id = $id;
            """;
        sessionCommand.Parameters.AddWithValue("$id", id);

        await using var reader = await sessionCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var sessionId = reader.GetString(0);
        var title = reader.GetString(1);
        var model = reader.GetString(2);
        var summary = reader.IsDBNull(3) ? null : reader.GetString(3);
        var createdAt = DateTimeOffset.Parse(reader.GetString(4));
        var updatedAt = DateTimeOffset.Parse(reader.GetString(5));
        await reader.DisposeAsync();

        var messages = await LoadMessagesAsync(connection, sessionId, cancellationToken);
        return new StoredSession(
            sessionId,
            title,
            model,
            summary,
            createdAt,
            updatedAt,
            messages);
    }

    public async Task<StoredSession?> LoadLatestAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id
            FROM sessions
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        var id = await command.ExecuteScalarAsync(cancellationToken) as string;
        return id == null
            ? null
            : await LoadAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, title, model, created_at, updated_at
            FROM sessions
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var sessions = new List<SessionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SessionSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return sessions;
    }

    public async Task AppendMessageAsync(
        string sessionId,
        AgentConversationMessage message,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText =
            """
            INSERT INTO messages (session_id, role, content, created_at)
            VALUES ($sessionId, $role, $content, $createdAt);
            """;
        insert.Parameters.AddWithValue("$sessionId", sessionId);
        insert.Parameters.AddWithValue("$role", message.Role);
        insert.Parameters.AddWithValue("$content", message.Content);
        insert.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);

        var touch = connection.CreateCommand();
        touch.Transaction = (SqliteTransaction)transaction;
        touch.CommandText =
            "UPDATE sessions SET updated_at = $updatedAt WHERE id = $sessionId;";
        touch.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        touch.Parameters.AddWithValue("$sessionId", sessionId);
        await touch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpdateTitleAsync(
        string sessionId,
        string title,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE sessions
            SET title = $title, updated_at = $updatedAt
            WHERE id = $sessionId;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$sessionId", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceHistoryAsync(
        string sessionId,
        string summary,
        IReadOnlyList<AgentConversationMessage> retainedMessages,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var delete = connection.CreateCommand();
        delete.Transaction = (SqliteTransaction)transaction;
        delete.CommandText = "DELETE FROM messages WHERE session_id = $sessionId;";
        delete.Parameters.AddWithValue("$sessionId", sessionId);
        await delete.ExecuteNonQueryAsync(cancellationToken);

        foreach (var message in retainedMessages)
        {
            var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText =
                """
                INSERT INTO messages (session_id, role, content, created_at)
                VALUES ($sessionId, $role, $content, $createdAt);
                """;
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            insert.Parameters.AddWithValue("$role", message.Role);
            insert.Parameters.AddWithValue("$content", message.Content);
            insert.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        var update = connection.CreateCommand();
        update.Transaction = (SqliteTransaction)transaction;
        update.CommandText =
            """
            UPDATE sessions
            SET summary = $summary, updated_at = $updatedAt
            WHERE id = $sessionId;
            """;
        update.Parameters.AddWithValue("$summary", summary);
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$sessionId", sessionId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AppendAuditEntriesAsync(
        string sessionId,
        IReadOnlyList<string> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var entry in entries)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO audit_entries (session_id, entry, created_at)
                VALUES ($sessionId, $entry, $createdAt);
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            command.Parameters.AddWithValue("$entry", entry);
            command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditEntriesAsync(
        string sessionId,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT entry, created_at
            FROM audit_entries
            WHERE session_id = $sessionId
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$limit", limit);

        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AuditEntry(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1))));
        }

        return entries;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<IReadOnlyList<AgentConversationMessage>> LoadMessagesAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT role, content, created_at
            FROM messages
            WHERE session_id = $sessionId
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var messages = new List<AgentConversationMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new AgentConversationMessage(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2))));
        }

        return messages;
    }
}
