using Microsoft.Data.Sqlite;
using WorkMonitor.Agent;
using WorkMonitor.Agent.Conversations;

namespace WorkMonitor.Tests;

public sealed class OwnedSessionStoreTests : IDisposable
{
    private const string OwnerA = "tenant-a:object-a";
    private const string OwnerB = "tenant-a:object-b";
    private readonly string _directory = Path.GetFullPath(
        $"session-store-tests-{Guid.NewGuid():N}");

    private string DatabasePath => Path.Combine(_directory, "sessions.db");

    [Fact]
    public async Task OwnersOnlyListAndLoadTheirOwnConversations()
    {
        var store = await CreateStoreAsync();
        var first = await store.CreateForOwnerAsync(OwnerA, "model-a", "First", ct: default);
        var second = await store.CreateForOwnerAsync(OwnerB, "model-b", "Second");
        var otherTenant = await store.CreateForOwnerAsync("tenant-b:object-a", "model-c");
        var cli = await store.CreateAsync("cli-model");
        var message = Message("private message");
        await store.AppendMessageForOwnerAsync(OwnerA, first.Id, message);

        var summary = Assert.Single(await store.ListForOwnerAsync(OwnerA, ct: default));
        Assert.Equal(first.Id, summary.Id);
        Assert.Equal(first.Title, summary.Title);
        Assert.Equal(first.Model, summary.Model);
        Assert.Equal(first.CreatedAt, summary.CreatedAt);
        Assert.Equal(second.Id, Assert.Single(await store.ListForOwnerAsync(OwnerB)).Id);
        Assert.Equal(otherTenant.Id,
            Assert.Single(await store.ListForOwnerAsync("tenant-b:object-a")).Id);
        Assert.Empty(await store.ListForOwnerAsync("unknown:owner"));

        var loaded = await store.LoadForOwnerAsync(OwnerA, first.Id, ct: default);
        Assert.NotNull(loaded);
        Assert.Equal(message, Assert.Single(loaded.Messages));
        Assert.Null(await store.LoadForOwnerAsync(OwnerA, second.Id));
        Assert.Null(await store.LoadForOwnerAsync(OwnerB, first.Id));
        Assert.Null(await store.LoadForOwnerAsync(OwnerA, otherTenant.Id));
        Assert.Null(await store.LoadForOwnerAsync(OwnerA, cli.Id));
        Assert.Null(await store.LoadForOwnerAsync(OwnerB, cli.Id));
        Assert.Null(await store.LoadForOwnerAsync(OwnerA, "missing"));
    }

    [Theory]
    [InlineData("another-owner")]
    [InlineData("cli")]
    [InlineData("missing")]
    public async Task UnauthorizedWritesThrowWithoutChangingStoredData(string targetKind)
    {
        var store = await CreateStoreAsync();
        StoredSession? target = targetKind switch
        {
            "another-owner" => await store.CreateForOwnerAsync(OwnerB, "model", "Original"),
            "cli" => await store.CreateAsync("model", "Original"),
            _ => null
        };
        var sessionId = target?.Id ?? "missing";
        if (target != null)
        {
            await store.AppendMessageAsync(sessionId, Message("original history"));
            await store.ReplaceHistoryAsync(sessionId, "original summary",
                new[] { Message("retained history") });
            await store.AppendAuditEntriesAsync(sessionId, new[] { "original audit" });
        }

        var before = await store.LoadAsync(sessionId);
        var auditBefore = await store.ListAuditEntriesAsync(sessionId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.AppendMessageForOwnerAsync(OwnerA, sessionId, Message("forbidden")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.UpdateTitleForOwnerAsync(OwnerA, sessionId, "Forbidden title"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.ReplaceHistoryForOwnerAsync(OwnerA, sessionId, "Forbidden summary",
                new[] { Message("forbidden history") }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.ReplaceHistoryForOwnerAsync(OwnerA, sessionId, "Forbidden summary",
                Array.Empty<AgentConversationMessage>()));

        var after = await store.LoadAsync(sessionId);
        if (before == null)
        {
            Assert.Null(after);
        }
        else
        {
            Assert.NotNull(after);
            Assert.Equal(before.Title, after.Title);
            Assert.Equal(before.Summary, after.Summary);
            Assert.Equal(before.CreatedAt, after.CreatedAt);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.Equal(before.Messages.ToArray(), after.Messages.ToArray());
        }

        Assert.Equal(auditBefore.ToArray(), (await store.ListAuditEntriesAsync(sessionId)).ToArray());
    }

    [Fact]
    public async Task OwnedMessagesTitlesAndCompactedHistorySurviveRestart()
    {
        var store = await CreateStoreAsync();
        var created = await store.CreateForOwnerAsync(OwnerA, "persistent-model");
        Assert.Equal("New conversation", created.Title);
        await store.AppendMessageForOwnerAsync(OwnerA, created.Id, Message("old message"));
        await store.UpdateTitleForOwnerAsync(OwnerA, created.Id, "Persistent title");
        var retained = Message("retained answer", "assistant");
        await store.ReplaceHistoryForOwnerAsync(OwnerA, created.Id, "Earlier conversation.",
            new[] { retained });
        var latest = Message("new question");
        await store.AppendMessageForOwnerAsync(OwnerA, created.Id, latest);
        var before = await store.LoadForOwnerAsync(OwnerA, created.Id);
        Assert.NotNull(before);

        var restarted = new SessionStore(DatabasePath);
        await restarted.InitializeAsync();
        var loaded = await restarted.LoadForOwnerAsync(OwnerA, created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(created.Id, loaded.Id);
        Assert.Equal("persistent-model", loaded.Model);
        Assert.Equal("Persistent title", loaded.Title);
        Assert.Equal("Earlier conversation.", loaded.Summary);
        Assert.Equal(created.CreatedAt, loaded.CreatedAt);
        Assert.Equal(before.UpdatedAt, loaded.UpdatedAt);
        Assert.Equal(new[] { retained, latest }, loaded.Messages.ToArray());
        Assert.Equal(created.Id, Assert.Single(await restarted.ListForOwnerAsync(OwnerA)).Id);
        Assert.Null(await restarted.LoadForOwnerAsync(OwnerB, created.Id));
        Assert.Empty(await restarted.ListForOwnerAsync(OwnerB));
    }

    [Fact]
    public async Task MigrationPreservesLegacyCliSessionsMessagesAndAuditEntries()
    {
        Directory.CreateDirectory(_directory);
        await using (var connection = OpenRawConnection())
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    model TEXT NOT NULL,
                    summary TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
                );
                CREATE TABLE audit_entries (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    session_id TEXT NOT NULL,
                    entry TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE
                );
                INSERT INTO sessions VALUES (
                    'legacy', 'Legacy title', 'legacy-model', 'Legacy summary',
                    '2025-01-01T00:00:00.0000000+00:00', '2025-01-02T00:00:00.0000000+00:00'
                );
                INSERT INTO messages (session_id, role, content, created_at)
                VALUES ('legacy', 'user', 'Legacy message', '2025-01-01T00:00:00.0000000+00:00');
                INSERT INTO audit_entries (session_id, entry, created_at)
                VALUES ('legacy', 'Legacy audit', '2025-01-01T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = await CreateStoreAsync();
        await store.InitializeAsync();
        var legacy = await store.LoadAsync("legacy");
        Assert.NotNull(legacy);
        Assert.Equal("Legacy title", legacy.Title);
        Assert.Equal("legacy-model", legacy.Model);
        Assert.Equal("Legacy summary", legacy.Summary);
        Assert.Equal(DateTimeOffset.Parse("2025-01-01T00:00:00+00:00"), legacy.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2025-01-02T00:00:00+00:00"), legacy.UpdatedAt);
        Assert.Equal("Legacy message", Assert.Single(legacy.Messages).Content);
        Assert.Equal("Legacy audit", Assert.Single(await store.ListAuditEntriesAsync("legacy")).Entry);
        Assert.Equal("legacy", Assert.Single(await store.ListAsync()).Id);
        Assert.Equal("legacy", (await store.LoadLatestAsync())?.Id);
        Assert.Null(await store.LoadForOwnerAsync(OwnerA, "legacy"));
        Assert.Empty(await store.ListForOwnerAsync(OwnerA));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.UpdateTitleForOwnerAsync(OwnerA, "legacy", "Forbidden"));

        await store.AppendMessageAsync("legacy", Message("CLI continues"));
        await store.UpdateTitleAsync("legacy", "Updated legacy title");
        var owned = await store.CreateForOwnerAsync(OwnerA, "web-model");
        Assert.Equal(owned.Id, Assert.Single(await store.ListForOwnerAsync(OwnerA)).Id);
        Assert.Equal(2, (await store.LoadAsync("legacy"))?.Messages.Count);
        Assert.Equal("Updated legacy title", (await store.LoadAsync("legacy"))?.Title);

        await using var verification = OpenRawConnection();
        await verification.OpenAsync();
        var owner = verification.CreateCommand();
        owner.CommandText = "SELECT owner_id FROM sessions WHERE id = 'legacy';";
        Assert.Equal(DBNull.Value, await owner.ExecuteScalarAsync());
        var columns = verification.CreateCommand();
        columns.CommandText = "PRAGMA table_info(sessions);";
        await using var reader = await columns.ExecuteReaderAsync();
        var ownerColumns = 0;
        while (await reader.ReadAsync())
        {
            if (reader.GetString(1) == "owner_id")
            {
                ownerColumns++;
                Assert.Equal("TEXT", reader.GetString(2));
                Assert.Equal(0, reader.GetInt32(3));
            }
        }

        Assert.Equal(1, ownerColumns);
    }

    [Fact]
    public async Task UnscopedCliApisRemainCompatible()
    {
        var store = await CreateStoreAsync();
        Assert.Null(await store.LoadLatestAsync());
        Assert.Empty(await store.ListAsync());
        var created = await store.CreateAsync("cli-model", "CLI title");
        await store.AppendMessageAsync(created.Id, Message("CLI message"));
        await store.UpdateTitleAsync(created.Id, "Renamed CLI title");
        var retained = Message("retained CLI response", "assistant");
        await store.ReplaceHistoryAsync(created.Id, "CLI summary", new[] { retained });
        await store.AppendAuditEntriesAsync(created.Id, new[] { "CLI audit" });

        var restarted = await CreateStoreAsync();
        var loaded = await restarted.LoadAsync(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Renamed CLI title", loaded.Title);
        Assert.Equal("CLI summary", loaded.Summary);
        Assert.Equal(retained, Assert.Single(loaded.Messages));
        Assert.Equal(created.Id, (await restarted.LoadLatestAsync())?.Id);
        Assert.Equal(created.Id, Assert.Single(await restarted.ListAsync()).Id);
        Assert.Equal("CLI audit", Assert.Single(await restarted.ListAuditEntriesAsync(created.Id)).Entry);
        Assert.Null(await restarted.LoadForOwnerAsync(OwnerA, created.Id));
        Assert.Empty(await restarted.ListForOwnerAsync(OwnerA));

        var owned = await restarted.CreateForOwnerAsync(OwnerA, "owned-model");
        Assert.NotNull(await restarted.LoadAsync(owned.Id));
        Assert.Equal(2, (await restarted.ListAsync()).Count);
    }

    [Fact]
    public async Task OwnerListHonorsLimitsAndUpdatedOrder()
    {
        var store = await CreateStoreAsync();
        var first = await store.CreateForOwnerAsync(OwnerA, "model");
        var second = await store.CreateForOwnerAsync(OwnerA, "model");
        var otherOwner = await store.CreateForOwnerAsync(OwnerB, "model");
        await using (var connection = OpenRawConnection())
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE sessions SET updated_at = '2025-01-01T00:00:00.0000000+00:00' WHERE id = $first;
                UPDATE sessions SET updated_at = '2025-01-02T00:00:00.0000000+00:00' WHERE id = $second;
                UPDATE sessions SET updated_at = '2025-01-03T00:00:00.0000000+00:00' WHERE id = $other;
                """;
            command.Parameters.AddWithValue("$first", first.Id);
            command.Parameters.AddWithValue("$second", second.Id);
            command.Parameters.AddWithValue("$other", otherOwner.Id);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Equal(second.Id, Assert.Single(await store.ListForOwnerAsync(OwnerA, limit: 1)).Id);
        Assert.Equal(new[] { second.Id, first.Id },
            (await store.ListForOwnerAsync(OwnerA)).Select(session => session.Id).ToArray());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListForOwnerAsync(OwnerA, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ListForOwnerAsync(OwnerA, 101));
    }

    [Fact]
    public async Task OwnerIdentifiersAreMatchedExactlyAndParameterized()
    {
        var store = await CreateStoreAsync();
        var owner = "tenant:object' OR 1=1 --";
        var created = await store.CreateForOwnerAsync(owner, "model");
        var other = await store.CreateForOwnerAsync(OwnerA, "model");
        Assert.Equal(created.Id, Assert.Single(await store.ListForOwnerAsync(owner)).Id);
        Assert.Null(await store.LoadForOwnerAsync(owner, other.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.UpdateTitleForOwnerAsync(owner, other.Id, "Forbidden"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EveryOwnerApiRejectsMissingOwner(string? owner)
    {
        var store = await CreateStoreAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.CreateForOwnerAsync(owner!, "model"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.LoadForOwnerAsync(owner!, "session"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ListForOwnerAsync(owner!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.AppendMessageForOwnerAsync(owner!, "session", Message("message")));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.UpdateTitleForOwnerAsync(owner!, "session", "title"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            store.ReplaceHistoryForOwnerAsync(owner!, "session", "summary",
                Array.Empty<AgentConversationMessage>()));
    }

    private async Task<SessionStore> CreateStoreAsync()
    {
        var store = new SessionStore(DatabasePath);
        await store.InitializeAsync();
        return store;
    }

    private SqliteConnection OpenRawConnection()
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Pooling = false
        }.ToString());

    private static AgentConversationMessage Message(string content, string role = "user")
        => new(role, content, DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
