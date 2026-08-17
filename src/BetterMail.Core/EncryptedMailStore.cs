using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed class EncryptedMailStore(string databasePath, string key) : IMailStore, IDraftStore, IProviderTokenStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _optimizedSearch;
    private volatile bool _correspondentsReady;

    public bool HasIndexedCorrespondents => _correspondentsReady;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => InitializeCoreAsync(cancellationToken), cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());

            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, $"PRAGMA key = '{ValidateHexKey(key)}';", cancellationToken).ConfigureAwait(false);
            connection.CreateFunction<byte[]?, string?>("decode_body", DecodeBody);
            connection.CreateFunction<string?, byte[]?>("encode_body", EncodeBody);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "messages", "is_flagged", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "messages", "cc_recipients_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "messages", "body_blob", "BLOB", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "mail_folders", "parent_provider_id", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "sync_cursors", "is_complete", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "is_html", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "provider_draft_id", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "synced_local_updated_at", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "provider_updated_at", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "provider_etag", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "conversation_identity", "TEXT", cancellationToken).ConfigureAwait(false);
            await RunOnceAsync(connection, "thread-index-v1", BackfillThreadIndex, cancellationToken).ConfigureAwait(false);
            _optimizedSearch = await MigrationCompleteAsync(connection, "message-search-v2", cancellationToken).ConfigureAwait(false);
            _correspondentsReady = await MigrationCompleteAsync(connection, "message-correspondents-v1", cancellationToken).ConfigureAwait(false);
            if (!_optimizedSearch)
            {
                await ExecuteAsync(connection, LegacySearchSchema, cancellationToken).ConfigureAwait(false);
            }
            _connection = connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAccountAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        await WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO accounts(provider_id, account_id, tenant_id, email, display_name, capabilities)
                VALUES($provider, $account, $tenant, $email, $name, $capabilities)
                ON CONFLICT(provider_id, account_id) DO UPDATE SET
                    tenant_id = excluded.tenant_id,
                    email = excluded.email,
                    display_name = excluded.display_name,
                    capabilities = excluded.capabilities;
                """;
            command.Parameters.AddWithValue("$provider", account.ProviderId);
            command.Parameters.AddWithValue("$account", account.AccountId);
            command.Parameters.AddWithValue("$tenant", account.TenantId);
            command.Parameters.AddWithValue("$email", account.EmailAddress);
            command.Parameters.AddWithValue("$name", account.DisplayName);
            command.Parameters.AddWithValue("$capabilities", (long)account.Capabilities);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveProviderTokenAsync(ProviderToken token, CancellationToken cancellationToken = default)
    {
        await WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO provider_tokens(provider_id, account_id, access_token, refresh_token, expires_at, scopes)
                VALUES($provider, $account, $access, $refresh, $expires, $scopes)
                ON CONFLICT(provider_id, account_id) DO UPDATE SET
                    access_token = excluded.access_token,
                    refresh_token = excluded.refresh_token,
                    expires_at = excluded.expires_at,
                    scopes = excluded.scopes;
                """;
            command.Parameters.AddWithValue("$provider", token.ProviderId);
            command.Parameters.AddWithValue("$account", token.AccountId);
            command.Parameters.AddWithValue("$access", token.AccessToken);
            command.Parameters.AddWithValue("$refresh", token.RefreshToken);
            command.Parameters.AddWithValue("$expires", token.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$scopes", token.Scopes);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<ProviderToken?> GetProviderTokenAsync(
        string providerId,
        string accountId,
        CancellationToken cancellationToken = default) =>
        WithLockAsync<ProviderToken?>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT access_token, refresh_token, expires_at, scopes
                FROM provider_tokens
                WHERE provider_id = $provider AND account_id = $account;
                """;
            command.Parameters.AddWithValue("$provider", providerId);
            command.Parameters.AddWithValue("$account", accountId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new ProviderToken(
                    providerId,
                    accountId,
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.GetString(3))
                : null;
        }, cancellationToken);

    public async Task DeleteProviderTokenAsync(
        string providerId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        await WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM provider_tokens WHERE provider_id = $provider AND account_id = $account;";
            command.Parameters.AddWithValue("$provider", providerId);
            command.Parameters.AddWithValue("$account", accountId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<MailAccount>>(async connection =>
        {
            var accounts = new List<MailAccount>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT provider_id, account_id, tenant_id, email, display_name, capabilities FROM accounts ORDER BY display_name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                accounts.Add(new MailAccount(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), (ProviderCapabilities)reader.GetInt64(5)));
            }

            return accounts;
        }, cancellationToken);

    public async Task DeleteAccountAsync(
        string providerId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        await WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var sql in new[]
            {
                "DELETE FROM provider_tokens WHERE provider_id = $provider AND account_id = $account;",
                "DELETE FROM local_drafts WHERE account_id = $account;",
                "DELETE FROM message_threads WHERE mailbox_id IN (SELECT account_id || ':' || lower(address) FROM mailboxes WHERE account_id = $account);",
                "DELETE FROM messages WHERE mailbox_id IN (SELECT account_id || ':' || lower(address) FROM mailboxes WHERE account_id = $account);",
                "DELETE FROM sync_cursors WHERE mailbox_id LIKE $account || ':%';",
                "DELETE FROM mail_folders WHERE mailbox_id IN (SELECT account_id || ':' || lower(address) FROM mailboxes WHERE account_id = $account);",
                "DELETE FROM mailboxes WHERE account_id = $account;",
                "DELETE FROM workspace_items WHERE account_id = $account;",
                "DELETE FROM workspace_snapshots WHERE account_id = $account;",
                "DELETE FROM accounts WHERE provider_id = $provider AND account_id = $account;"
            })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$provider", providerId);
                command.Parameters.AddWithValue("$account", accountId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveMailboxAsync(Mailbox mailbox, CancellationToken cancellationToken = default)
    {
        await WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO mailboxes(account_id, address, display_name, is_shared, can_send_as, can_send_on_behalf)
                VALUES($account, $address, $name, $shared, $sendAs, $sendOnBehalf)
                ON CONFLICT(account_id, address) DO UPDATE SET
                    display_name = excluded.display_name,
                    is_shared = excluded.is_shared,
                    can_send_as = excluded.can_send_as,
                    can_send_on_behalf = excluded.can_send_on_behalf;
                """;
            command.Parameters.AddWithValue("$account", mailbox.AccountId);
            command.Parameters.AddWithValue("$address", mailbox.Address.ToLowerInvariant());
            command.Parameters.AddWithValue("$name", mailbox.DisplayName);
            command.Parameters.AddWithValue("$shared", mailbox.IsShared);
            command.Parameters.AddWithValue("$sendAs", mailbox.CanSendAs);
            command.Parameters.AddWithValue("$sendOnBehalf", mailbox.CanSendOnBehalf);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Mailbox>> GetMailboxesAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<Mailbox>>(async connection =>
        {
            var mailboxes = new List<Mailbox>();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT account_id, address, display_name, is_shared, can_send_as, can_send_on_behalf FROM mailboxes ORDER BY is_shared, display_name;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                mailboxes.Add(new Mailbox(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)));
            }

            return mailboxes;
        }, cancellationToken);

    public async Task SaveFoldersAsync(string mailboxId, IReadOnlyList<MailFolder> folders, CancellationToken cancellationToken = default)
    {
        await WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM mail_folders WHERE mailbox_id = $mailbox;";
                delete.Parameters.AddWithValue("$mailbox", mailboxId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (var folder in folders)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO mail_folders(mailbox_id, provider_id, display_name, unread_count, total_count, well_known_name, parent_provider_id)
                    VALUES($mailbox, $provider, $name, $unread, $total, $wellKnown, $parent);
                    """;
                command.Parameters.AddWithValue("$mailbox", mailboxId);
                command.Parameters.AddWithValue("$provider", folder.ProviderId);
                command.Parameters.AddWithValue("$name", folder.DisplayName);
                command.Parameters.AddWithValue("$unread", folder.UnreadCount);
                command.Parameters.AddWithValue("$total", folder.TotalCount);
                command.Parameters.AddWithValue("$wellKnown", (object?)folder.WellKnownName ?? DBNull.Value);
                command.Parameters.AddWithValue("$parent", (object?)folder.ParentProviderId ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var deleteOrphanedMessages = connection.CreateCommand())
            {
                deleteOrphanedMessages.Transaction = transaction;
                deleteOrphanedMessages.CommandText = """
                    DELETE FROM messages
                    WHERE mailbox_id = $mailbox
                      AND $hasFolders = 1
                      AND NOT EXISTS (
                          SELECT 1
                          FROM mail_folders
                          WHERE mail_folders.mailbox_id = messages.mailbox_id
                            AND mail_folders.provider_id = messages.folder_id
                      );
                    """;
                deleteOrphanedMessages.Parameters.AddWithValue("$mailbox", mailboxId);
                deleteOrphanedMessages.Parameters.AddWithValue("$hasFolders", folders.Count > 0);
                await deleteOrphanedMessages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var deleteOrphanedThreads = connection.CreateCommand())
            {
                deleteOrphanedThreads.Transaction = transaction;
                deleteOrphanedThreads.CommandText = """
                    DELETE FROM message_threads
                    WHERE mailbox_id = $mailbox
                      AND $hasFolders = 1
                      AND NOT EXISTS (
                          SELECT 1
                          FROM messages
                          WHERE messages.mailbox_id = message_threads.mailbox_id
                            AND messages.provider_id = message_threads.provider_id
                      );
                    """;
                deleteOrphanedThreads.Parameters.AddWithValue("$mailbox", mailboxId);
                deleteOrphanedThreads.Parameters.AddWithValue("$hasFolders", folders.Count > 0);
                await deleteOrphanedThreads.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(string? mailboxId = null, CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<MailFolder>>(async connection =>
        {
            var folders = new List<MailFolder>();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT mailbox_id, provider_id, display_name, unread_count, total_count, well_known_name, parent_provider_id
                FROM mail_folders {(mailboxId is null ? "" : "WHERE mailbox_id = $mailbox")}
                ORDER BY mailbox_id, CASE WHEN well_known_name = 'inbox' THEN 0 ELSE 1 END, display_name;
                """;
            if (mailboxId is not null)
            {
                command.Parameters.AddWithValue("$mailbox", mailboxId);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                folders.Add(new MailFolder(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            }

            return folders;
        }, cancellationToken);

    public async Task ApplySyncPageAsync(string cursorId, MailSyncPage page, CancellationToken cancellationToken = default)
    {
        await WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var message in page.Messages)
            {
                if (message.IsDeleted)
                {
                    await DeleteMessageAsync(connection, transaction, message.MailboxId, message.ProviderId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await UpsertMessageAsync(connection, transaction, message, cancellationToken).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrWhiteSpace(page.NextCursor))
            {
                await using var cursorCommand = connection.CreateCommand();
                cursorCommand.Transaction = transaction;
                cursorCommand.CommandText = """
                    INSERT INTO sync_cursors(mailbox_id, cursor, is_complete)
                    VALUES($cursorId, $cursor, $isComplete)
                    ON CONFLICT(mailbox_id) DO UPDATE SET
                        cursor = excluded.cursor,
                        is_complete = excluded.is_complete;
                    """;
                cursorCommand.Parameters.AddWithValue("$cursorId", cursorId);
                cursorCommand.Parameters.AddWithValue("$cursor", page.NextCursor);
                cursorCommand.Parameters.AddWithValue("$isComplete", page.HasMore ? 0 : 1);
                await cursorCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> GetSyncCursorAsync(string cursorId, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT cursor FROM sync_cursors WHERE mailbox_id = $cursorId;";
            command.Parameters.AddWithValue("$cursorId", cursorId);
            return (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<MailSyncState> GetSyncStateAsync(string cursorId, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT cursor, is_complete FROM sync_cursors WHERE mailbox_id = $cursorId;";
            command.Parameters.AddWithValue("$cursorId", cursorId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new MailSyncState(reader.GetString(0), reader.GetInt32(1) == 1)
                : new MailSyncState(null, false);
        }, cancellationToken);

    public Task<IReadOnlyList<MailMessage>> GetMessagesAsync(string? mailboxId = null, string? folderId = null, int limit = 5000, CancellationToken cancellationToken = default)
    {
        var filters = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        if (mailboxId is not null)
        {
            filters.Add("mailbox_id = $mailbox");
            parameters.Add(("$mailbox", mailboxId));
        }
        if (folderId is not null)
        {
            filters.Add("folder_id = $folder");
            parameters.Add(("$folder", folderId));
        }

        return QueryMessagesAsync(filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}", limit, true, cancellationToken, parameters.ToArray());
    }


    public Task<MailPage> GetMessagesPageAsync(
        IReadOnlyList<MailFolderKey> folders,
        MailPageCursor? cursor = null,
        int pageSize = 200,
        CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var messages = new List<MailMessage>();
            long lastRowId = 0;
            var filters = new List<string>();
            await using var command = connection.CreateCommand();
            if (folders.Count > 0)
            {
                var folderFilters = new List<string>(folders.Count);
                for (var index = 0; index < folders.Count; index++)
                {
                    folderFilters.Add($"(mailbox_id = $mailbox{index} AND folder_id = $folder{index})");
                    command.Parameters.AddWithValue($"$mailbox{index}", folders[index].MailboxId);
                    command.Parameters.AddWithValue($"$folder{index}", folders[index].FolderId);
                }
                filters.Add($"({string.Join(" OR ", folderFilters)})");
            }
            if (cursor is not null)
            {
                filters.Add("(received_at < $received OR (received_at = $received AND rowid < $rowid))");
                command.Parameters.AddWithValue("$received", cursor.ReceivedAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$rowid", cursor.RowId);
            }

            var limit = Math.Clamp(pageSize, 1, 500);
            command.Parameters.AddWithValue("$limit", limit);
            command.CommandText = $"""
                SELECT mailbox_id, provider_id, conversation_id, internet_message_id, folder_id, subject,
                       from_name, from_address, recipients_json, received_at, preview, NULL, is_html,
                       is_read, has_attachments, importance, categories_json, etag, is_flagged,
                       cc_recipients_json, NULL, rowid
                FROM messages {(filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}")}
                ORDER BY received_at DESC, rowid DESC LIMIT $limit;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                messages.Add(ReadMessage(reader));
                lastRowId = reader.GetInt64(21);
            }

            return new MailPage(
                messages,
                messages.Count == limit ? new(messages[^1].ReceivedAt, lastRowId) : null);
        }, cancellationToken);

    public async Task<int> PruneMessagesBeforeAsync(
        string mailboxId,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        var deleted = 0;
        while (true)
        {
            var batch = await WithLockAsync(async connection =>
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (var threads = connection.CreateCommand())
                {
                    threads.Transaction = transaction;
                    threads.CommandText = """
                        DELETE FROM message_threads
                        WHERE (mailbox_id, provider_id) IN (
                            SELECT mailbox_id, provider_id FROM messages
                            WHERE mailbox_id = $mailbox AND received_at < $cutoff
                            ORDER BY rowid LIMIT 500
                        );
                        """;
                    threads.Parameters.AddWithValue("$mailbox", mailboxId);
                    threads.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
                    await threads.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await using var messages = connection.CreateCommand();
                messages.Transaction = transaction;
                messages.CommandText = """
                    DELETE FROM messages WHERE rowid IN (
                        SELECT rowid FROM messages
                        WHERE mailbox_id = $mailbox AND received_at < $cutoff
                        ORDER BY rowid LIMIT 500
                    );
                    """;
                messages.Parameters.AddWithValue("$mailbox", mailboxId);
                messages.Parameters.AddWithValue("$cutoff", cutoff.ToString("O", CultureInfo.InvariantCulture));
                var count = await messages.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return count;
            }, cancellationToken).ConfigureAwait(false);
            deleted += batch;
            if (batch < 500)
            {
                return deleted;
            }
            await Task.Yield();
        }
    }
    public async Task<MailMessage?> GetMessageAsync(
        string mailboxId,
        string providerMessageId,
        CancellationToken cancellationToken = default) =>
        (await QueryMessagesAsync(
            "WHERE mailbox_id = $mailbox AND provider_id = $provider",
            1,
            true,
            cancellationToken,
            ("$mailbox", mailboxId),
            ("$provider", providerMessageId)).ConfigureAwait(false)).SingleOrDefault();

    public Task<MailStoreCounts> GetMessageCountsAsync(
        string? mailboxId = null,
        CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT count(*),
                       coalesce(sum(CASE WHEN is_read = 0 THEN 1 ELSE 0 END), 0),
                       coalesce(sum(CASE WHEN is_flagged = 1 THEN 1 ELSE 0 END), 0)
                FROM messages {(mailboxId is null ? "" : "WHERE mailbox_id = $mailbox")};
                """;
            if (mailboxId is not null)
            {
                command.Parameters.AddWithValue("$mailbox", mailboxId);
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new MailStoreCounts(
                checked((int)reader.GetInt64(0)),
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)));
        }, cancellationToken);

    public Task<IReadOnlyList<MailMessage>> SearchAsync(string query, int limit = 200, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetMessagesAsync(limit: limit, cancellationToken: cancellationToken);
        }

        var ftsQuery = string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static term => $"\"{term.Replace("\"", "\"\"")}\"*"));
        return QueryMessagesAsync(
            _optimizedSearch
                ? "WHERE rowid IN (SELECT rowid FROM message_search_v2 WHERE message_search_v2 MATCH $query)"
                : "WHERE rowid IN (SELECT rowid FROM message_search WHERE message_search MATCH $query UNION SELECT rowid FROM message_search_v2 WHERE message_search_v2 MATCH $query)",
            limit,
            false,
            cancellationToken,
            ("$query", ftsQuery));
    }

    public Task<IReadOnlyList<MailMessage>> GetThreadMessagesAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        QueryMessagesAsync(
            "WHERE EXISTS (SELECT 1 FROM message_threads thread WHERE thread.mailbox_id = messages.mailbox_id AND thread.provider_id = messages.provider_id AND thread.thread_id = $thread)",
            1000,
            false,
            cancellationToken,
            ("$thread", threadId));

    public Task UpdateMessageStateAsync(
        string mailboxId,
        string providerMessageId,
        bool? isRead = null,
        bool? isFlagged = null,
        CancellationToken cancellationToken = default)
    {
        if (isRead is null && isFlagged is null)
        {
            return Task.CompletedTask;
        }

        return WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE messages SET
                    is_read = CASE WHEN $hasRead = 1 THEN $isRead ELSE is_read END,
                    is_flagged = CASE WHEN $hasFlagged = 1 THEN $isFlagged ELSE is_flagged END
                WHERE mailbox_id = $mailbox AND provider_id = $provider;
                """;
            command.Parameters.AddWithValue("$hasRead", isRead is null ? 0 : 1);
            command.Parameters.AddWithValue("$isRead", isRead == true ? 1 : 0);
            command.Parameters.AddWithValue("$hasFlagged", isFlagged is null ? 0 : 1);
            command.Parameters.AddWithValue("$isFlagged", isFlagged == true ? 1 : 0);
            command.Parameters.AddWithValue("$mailbox", mailboxId);
            command.Parameters.AddWithValue("$provider", providerMessageId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DiscoveredPerson>> GetDiscoveredPeopleAsync(
        string query = "",
        int limit = 500,
        CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<DiscoveredPerson>>(async connection =>
        {
            var groups = new List<(string Email, string Name, string MailboxId, int Count, DateTimeOffset Last)>();
            await using var command = connection.CreateCommand();
            command.CommandText = _correspondentsReady
                ? """
                    SELECT email, display_name, mailbox_id, count(*), max(contacted_at)
                    FROM message_correspondents
                    WHERE instr(email, '@') > 1
                      AND ($query = '' OR email LIKE $pattern OR display_name LIKE $pattern)
                    GROUP BY email, display_name, mailbox_id
                    ORDER BY max(contacted_at) DESC;
                    """
                : """
                    WITH correspondents(email, display_name, mailbox_id, contacted_at) AS (
                        SELECT lower(trim(from_address)), trim(from_name), mailbox_id, received_at FROM messages
                        UNION ALL
                        SELECT lower(trim(json_extract(recipient.value, '$.Address'))),
                               trim(json_extract(recipient.value, '$.Name')), messages.mailbox_id, messages.received_at
                        FROM messages, json_each(messages.recipients_json) AS recipient
                        UNION ALL
                        SELECT lower(trim(json_extract(recipient.value, '$.Address'))),
                               trim(json_extract(recipient.value, '$.Name')), messages.mailbox_id, messages.received_at
                        FROM messages, json_each(messages.cc_recipients_json) AS recipient
                    )
                    SELECT email, display_name, mailbox_id, count(*), max(contacted_at)
                    FROM correspondents
                    WHERE email <> '' AND instr(email, '@') > 1
                      AND ($query = '' OR email LIKE $pattern OR display_name LIKE $pattern)
                    GROUP BY email, display_name, mailbox_id
                    ORDER BY max(contacted_at) DESC;
                    """;
            command.Parameters.AddWithValue("$query", query.Trim());
            command.Parameters.AddWithValue("$pattern", $"%{query.Trim()}%");
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                groups.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }

            return groups
                .GroupBy(static item => item.Email, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var bestName = group
                        .Where(item => !string.IsNullOrWhiteSpace(item.Name) &&
                            !string.Equals(item.Name, item.Email, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(static item => item.Count)
                        .ThenByDescending(static item => item.Last)
                        .Select(static item => item.Name)
                        .FirstOrDefault() ?? group.Key;
                    return new DiscoveredPerson(
                        group.Key,
                        bestName,
                        group.Select(static item => item.MailboxId).Distinct(StringComparer.Ordinal).ToArray(),
                        group.Sum(static item => item.Count),
                        group.Max(static item => item.Last));
                })
                .OrderByDescending(static person => person.Frequency)
                .ThenByDescending(static person => person.LastContactedAt)
                .Take(Math.Clamp(limit, 1, 5000))
                .ToArray();
        }, cancellationToken);

    public async Task ReplaceWorkspaceItemsAsync<T>(
        string kind,
        string accountId,
        string scopeId,
        IReadOnlyList<T> items,
        Func<T, string> providerId,
        Func<T, string> searchText,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceKey(kind, accountId, scopeId);
        await WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM workspace_items WHERE kind = $kind AND account_id = $account AND scope_id = $scope;";
                delete.Parameters.AddWithValue("$kind", kind);
                delete.Parameters.AddWithValue("$account", accountId);
                delete.Parameters.AddWithValue("$scope", scopeId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var item in items)
            {
                await UpsertWorkspaceItemAsync(
                    connection, transaction, kind, accountId, scopeId,
                    providerId(item), item, searchText(item), null, null, cancellationToken).ConfigureAwait(false);
            }
            await MarkWorkspaceSnapshotAsync(
                connection, transaction, kind, accountId, scopeId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertWorkspaceItemsAsync<T>(
        string kind,
        string accountId,
        string scopeId,
        IReadOnlyList<T> items,
        Func<T, string> providerId,
        Func<T, string> searchText,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkspaceKey(kind, accountId, scopeId);
        await WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                await UpsertWorkspaceItemAsync(
                    connection, transaction, kind, accountId, scopeId,
                    providerId(item), item, searchText(item), null, null, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<T>> GetWorkspaceItemsAsync<T>(
        string kind,
        string accountId,
        string? scopeId = null,
        CancellationToken cancellationToken = default) =>
        QueryWorkspaceItemsAsync<T>(kind, accountId, scopeId, null, 10000, cancellationToken);

    public Task<IReadOnlyList<T>> SearchWorkspaceItemsAsync<T>(
        string kind,
        string query,
        int limit = 200,
        string? accountId = null,
        CancellationToken cancellationToken = default) =>
        QueryWorkspaceItemsAsync<T>(kind, accountId, null, query, limit, cancellationToken);

    public Task GarbageCollectWorkspaceAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var sql in new[]
            {
                """
                DELETE FROM workspace_items
                WHERE kind = 'calendar-event' AND account_id = $account
                  AND EXISTS (
                      SELECT 1 FROM workspace_snapshots
                      WHERE kind = 'calendar' AND account_id = $account AND scope_id = 'all'
                  )
                  AND scope_id NOT IN (
                      SELECT provider_id FROM workspace_items
                      WHERE kind = 'calendar' AND account_id = $account
                  );
                """,
                """
                DELETE FROM workspace_items
                WHERE kind = 'task' AND account_id = $account
                  AND EXISTS (
                      SELECT 1 FROM workspace_snapshots
                      WHERE kind = 'task-list' AND account_id = $account AND scope_id = 'all'
                  )
                  AND scope_id NOT IN (
                      SELECT provider_id FROM workspace_items
                      WHERE kind = 'task-list' AND account_id = $account
                  );
                """,
                """
                DELETE FROM workspace_items
                WHERE kind = 'note-section' AND account_id = $account
                  AND EXISTS (
                      SELECT 1 FROM workspace_snapshots
                      WHERE kind = 'note-notebook' AND account_id = $account AND scope_id = 'all'
                  )
                  AND scope_id NOT IN (
                      SELECT provider_id FROM workspace_items
                      WHERE kind = 'note-notebook' AND account_id = $account
                  );
                """,
                """
                DELETE FROM workspace_items
                WHERE kind = 'note-page' AND account_id = $account
                  AND EXISTS (
                      SELECT 1 FROM workspace_snapshots
                      WHERE kind = 'note-section' AND account_id = $account
                  )
                  AND scope_id NOT IN (
                      SELECT provider_id FROM workspace_items
                      WHERE kind = 'note-section' AND account_id = $account
                  );
                """,
                """
                DELETE FROM workspace_items
                WHERE kind = 'note-content' AND account_id = $account
                  AND EXISTS (
                      SELECT 1 FROM workspace_snapshots
                      WHERE kind = 'note-page' AND account_id = $account
                  )
                  AND scope_id NOT IN (
                      SELECT provider_id FROM workspace_items
                      WHERE kind = 'note-page' AND account_id = $account
                  );
                """,
                """
                DELETE FROM workspace_items
                WHERE kind = 'drive-directory' AND account_id = $account AND scope_id <> 'root'
                  AND EXISTS (
                      SELECT 1 FROM workspace_snapshots
                      WHERE kind = 'drive-directory' AND account_id = $account AND scope_id = 'root'
                  )
                  AND scope_id NOT IN (
                      SELECT provider_id FROM workspace_items
                      WHERE kind = 'drive-directory' AND account_id = $account
                        AND json_extract(payload_json, '$.IsFolder') = 1
                  );
                """
            })
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$account", accountId);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public async Task ReplaceDriveDirectoryFilesAsync(
        string accountId,
        string parentPath,
        IReadOnlyList<CloudFile> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        await WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM workspace_items
                    WHERE kind = 'drive-file'
                      AND account_id = $account
                      AND scope_id = 'index'
                      AND coalesce(json_extract(payload_json, '$.ParentPath'), '') = $parent;
                    """;
                delete.Parameters.AddWithValue("$account", accountId);
                delete.Parameters.AddWithValue("$parent", parentPath);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var file in files)
            {
                await UpsertWorkspaceItemAsync(
                    connection, transaction, "drive-file", accountId, "index",
                    file.ProviderId, file, $"{file.Name} {file.Path}",
                    null, null, cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceCalendarEventsAsync(
        string accountId,
        string calendarId,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<CalendarEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(calendarId);
        if (to <= from)
        {
            throw new ArgumentOutOfRangeException(nameof(to));
        }

        await WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM workspace_items
                    WHERE kind = 'calendar-event'
                      AND account_id = $account
                      AND scope_id = $calendar
                      AND period_start < $to
                      AND period_end > $from;
                    """;
                delete.Parameters.AddWithValue("$account", accountId);
                delete.Parameters.AddWithValue("$calendar", calendarId);
                delete.Parameters.AddWithValue("$from", from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                delete.Parameters.AddWithValue("$to", to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var calendarEvent in events)
            {
                await UpsertWorkspaceItemAsync(
                    connection, transaction, "calendar-event", accountId, calendarId,
                    calendarEvent.ProviderId, calendarEvent,
                    $"{calendarEvent.Subject} {calendarEvent.Location}",
                    calendarEvent.StartsAt, calendarEvent.EndsAt, cancellationToken).ConfigureAwait(false);
            }
            await MarkWorkspaceSnapshotAsync(
                connection, transaction, "calendar-event", accountId, calendarId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CalendarEvent>> GetCalendarEventsAsync(
        string accountId,
        string calendarId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default) =>
        QueryWorkspaceItemsAsync<CalendarEvent>(
            "calendar-event", accountId, calendarId, null, 10000, cancellationToken,
            "AND period_start < $to AND period_end > $from",
            ("$from", from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ("$to", to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

    public Task<IReadOnlyList<CalendarInfo>> GetCalendarInfosAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        QueryWorkspaceItemsAsync<CalendarInfo>(
            "calendar", accountId, "all", null, 1000, cancellationToken);

    public Task<CalendarEvent?> GetCalendarEventAsync(
        string accountId,
        string calendarId,
        string providerEventId,
        CancellationToken cancellationToken = default) =>
        WithLockAsync<CalendarEvent?>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_json FROM workspace_items
                WHERE kind = 'calendar-event' AND account_id = $account
                  AND scope_id = $calendar AND provider_id = $event
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$calendar", calendarId);
            command.Parameters.AddWithValue("$event", providerEventId);
            var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            return payload is null ? null : JsonSerializer.Deserialize<CalendarEvent>(payload);
        }, cancellationToken);

    public Task<CalendarEvent?> GetNextCalendarEventAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default) =>
        WithLockAsync<CalendarEvent?>(async connection =>
        {
            var candidates = new List<CalendarEvent>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_json FROM workspace_items
                WHERE kind = 'calendar-event' AND period_end > $now
                ORDER BY period_start
                LIMIT 200;
                """;
            command.Parameters.AddWithValue("$now", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (JsonSerializer.Deserialize<CalendarEvent>(reader.GetString(0)) is { IsCancelled: false } item)
                {
                    candidates.Add(item);
                }
            }

            var localNow = now.ToLocalTime();
            var today = DateOnly.FromDateTime(localNow.Date);
            return candidates
                .OrderBy(item => NextEventRank(item, localNow, today))
                .ThenBy(item => item.StartsAt)
                .FirstOrDefault();
        }, cancellationToken);

    private static int NextEventRank(CalendarEvent item, DateTimeOffset now, DateOnly today)
    {
        var startsToday = DateOnly.FromDateTime(item.StartsAt.ToLocalTime().Date) == today;
        if (!item.IsAllDay && item.StartsAt <= now)
        {
            return 0;
        }
        if (!item.IsAllDay && startsToday)
        {
            return 1;
        }
        if (item.IsAllDay && startsToday)
        {
            return 2;
        }
        return 3;
    }

    public async Task SaveLocalDraftAsync(LocalDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.AccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.MailboxId);
        await WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO local_drafts(
                    id, account_id, mailbox_id, recipients, cc, bcc, subject, body, attachments_json,
                    updated_at, is_html, provider_draft_id, synced_local_updated_at, provider_updated_at, provider_etag, conversation_identity)
                VALUES(
                    $id, $account, $mailbox, $to, $cc, $bcc, $subject, $body, $attachments,
                    $updated, $isHtml, $providerDraft, $syncedLocal, $providerUpdated, $providerETag, $conversationIdentity)
                ON CONFLICT(id) DO UPDATE SET
                    account_id = excluded.account_id,
                    mailbox_id = excluded.mailbox_id,
                    recipients = excluded.recipients,
                    cc = excluded.cc,
                    bcc = excluded.bcc,
                    subject = excluded.subject,
                    body = excluded.body,
                    attachments_json = excluded.attachments_json,
                    updated_at = excluded.updated_at,
                    is_html = excluded.is_html,
                    provider_draft_id = CASE
                        WHEN excluded.account_id = local_drafts.account_id AND excluded.mailbox_id = local_drafts.mailbox_id
                        THEN COALESCE(excluded.provider_draft_id, local_drafts.provider_draft_id)
                        ELSE excluded.provider_draft_id
                    END,
                    synced_local_updated_at = CASE
                        WHEN excluded.account_id = local_drafts.account_id AND excluded.mailbox_id = local_drafts.mailbox_id
                        THEN COALESCE(excluded.synced_local_updated_at, local_drafts.synced_local_updated_at)
                        ELSE excluded.synced_local_updated_at
                    END,
                    provider_updated_at = CASE
                        WHEN excluded.account_id = local_drafts.account_id AND excluded.mailbox_id = local_drafts.mailbox_id
                        THEN COALESCE(excluded.provider_updated_at, local_drafts.provider_updated_at)
                        ELSE excluded.provider_updated_at
                    END,
                    conversation_identity = excluded.conversation_identity,
                    provider_etag = CASE
                        WHEN excluded.account_id = local_drafts.account_id AND excluded.mailbox_id = local_drafts.mailbox_id
                        THEN COALESCE(excluded.provider_etag, local_drafts.provider_etag)
                        ELSE excluded.provider_etag
                    END;
                """;
            command.Parameters.AddWithValue("$id", draft.Id);
            command.Parameters.AddWithValue("$account", draft.AccountId);
            command.Parameters.AddWithValue("$mailbox", draft.MailboxId);
            command.Parameters.AddWithValue("$to", draft.To);
            command.Parameters.AddWithValue("$cc", draft.Cc);
            command.Parameters.AddWithValue("$bcc", draft.Bcc);
            command.Parameters.AddWithValue("$subject", draft.Subject);
            command.Parameters.AddWithValue("$body", draft.Body);
            command.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(draft.Attachments));
            command.Parameters.AddWithValue("$updated", draft.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$isHtml", draft.IsHtml);
            command.Parameters.AddWithValue("$providerDraft", (object?)draft.ProviderDraftId ?? DBNull.Value);
            command.Parameters.AddWithValue("$syncedLocal", FormatNullable(draft.SyncedLocalUpdatedAt));
            command.Parameters.AddWithValue("$providerUpdated", FormatNullable(draft.ProviderUpdatedAt));
            command.Parameters.AddWithValue("$providerETag", (object?)draft.ProviderETag ?? DBNull.Value);
            command.Parameters.AddWithValue("$conversationIdentity", (object?)draft.ConversationIdentity ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<LocalDraft>> GetLocalDraftsAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<LocalDraft>>(async connection =>
        {
            var drafts = new List<LocalDraft>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, account_id, mailbox_id, recipients, cc, bcc, subject, body, attachments_json,
                       updated_at, is_html, provider_draft_id, synced_local_updated_at, provider_updated_at, provider_etag, conversation_identity
                FROM local_drafts ORDER BY updated_at DESC;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                drafts.Add(new LocalDraft(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetString(6), reader.GetString(7),
                    JsonSerializer.Deserialize<List<DraftAttachment>>(reader.GetString(8)) ?? [],
                    ParseTimestamp(reader.GetString(9)),
                    reader.GetBoolean(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : ParseTimestamp(reader.GetString(12)),
                    reader.IsDBNull(13) ? null : ParseTimestamp(reader.GetString(13)),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15)));
            }
            return drafts;
        }, cancellationToken);

    public Task DeleteLocalDraftAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM local_drafts WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task UpdateLocalDraftSyncMetadataAsync(
        string id,
        string providerDraftId,
        DateTimeOffset syncedLocalUpdatedAt,
        DateTimeOffset providerUpdatedAt,
        string? providerETag,
        CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE local_drafts
                SET provider_draft_id = $providerDraft,
                    synced_local_updated_at = $syncedLocal,
                    provider_updated_at = $providerUpdated,
                    provider_etag = $providerETag
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$providerDraft", providerDraftId);
            command.Parameters.AddWithValue("$syncedLocal", syncedLocalUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$providerUpdated", providerUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$providerETag", (object?)providerETag ?? DBNull.Value);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new InvalidOperationException("The local draft no longer exists.");
            }
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                var connection = _connection;
                _connection = null;
                await connection.DisposeAsync().ConfigureAwait(false);
                SqliteConnection.ClearPool(connection);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private Task<IReadOnlyList<MailMessage>> QueryMessagesAsync(
        string where,
        int limit,
        bool includeBody,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) => WithLockAsync<IReadOnlyList<MailMessage>>(async connection =>
    {
        var messages = new List<MailMessage>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT mailbox_id, provider_id, conversation_id, internet_message_id, folder_id, subject,
                   from_name, from_address, recipients_json, received_at, preview, {(includeBody ? "body" : "NULL")}, is_html,
                   is_read, has_attachments, importance, categories_json, etag, is_flagged,
                   cc_recipients_json, {(includeBody ? "body_blob" : "NULL")}
            FROM messages {where}
            ORDER BY received_at DESC, rowid DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(ReadMessage(reader));
        }

        return messages;
    }, cancellationToken);

    private async Task WithLockAsync(Func<SqliteConnection, Task> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => action(GetConnection()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> WithLockAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => action(GetConnection()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection GetConnection() => _connection ?? throw new InvalidOperationException("Initialize the mail store before use.");

    private static string ValidateHexKey(string value)
    {
        if (value.Length < 32 || value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("The database key must be a hexadecimal value of at least 128 bits.", nameof(value));
        }

        return value;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var query = connection.CreateCommand();
        query.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};", cancellationToken).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<T>> QueryWorkspaceItemsAsync<T>(
        string kind,
        string? accountId,
        string? scopeId,
        string? query,
        int limit,
        CancellationToken cancellationToken,
        string extraWhere = "",
        params (string Name, object Value)[] extraParameters) =>
        WithLockAsync<IReadOnlyList<T>>(async connection =>
        {
            var values = new List<T>();
            await using var command = connection.CreateCommand();
            var filters = new List<string> { "kind = $kind" };
            if (!string.IsNullOrWhiteSpace(accountId))
            {
                filters.Add("account_id = $account");
                command.Parameters.AddWithValue("$account", accountId);
            }
            if (!string.IsNullOrWhiteSpace(scopeId))
            {
                filters.Add("scope_id = $scope");
                command.Parameters.AddWithValue("$scope", scopeId);
            }
            if (!string.IsNullOrWhiteSpace(query))
            {
                filters.Add("rowid IN (SELECT rowid FROM workspace_search WHERE workspace_search MATCH $query)");
                command.Parameters.AddWithValue("$query", BuildFtsQuery(query));
            }
            command.CommandText = $"""
                SELECT payload_json
                FROM workspace_items
                WHERE {string.Join(" AND ", filters)} {extraWhere}
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10000));
            foreach (var (name, value) in extraParameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = JsonSerializer.Deserialize<T>(reader.GetString(0));
                if (item is not null)
                {
                    values.Add(item);
                }
            }
            return values;
        }, cancellationToken);

    private static async Task UpsertWorkspaceItemAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        string accountId,
        string scopeId,
        string providerId,
        T item,
        string searchText,
        DateTimeOffset? periodStart,
        DateTimeOffset? periodEnd,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workspace_items(
                kind, account_id, scope_id, provider_id, payload_json, search_text,
                period_start, period_end, updated_at)
            VALUES($kind, $account, $scope, $provider, $payload, $search, $start, $end, $updated)
            ON CONFLICT(kind, account_id, scope_id, provider_id) DO UPDATE SET
                payload_json = excluded.payload_json,
                search_text = excluded.search_text,
                period_start = excluded.period_start,
                period_end = excluded.period_end,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$scope", scopeId);
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item));
        command.Parameters.AddWithValue("$search", searchText ?? "");
        command.Parameters.AddWithValue("$start", periodStart is null
            ? DBNull.Value
            : periodStart.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$end", periodEnd is null
            ? DBNull.Value
            : periodEnd.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkWorkspaceSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        string accountId,
        string scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO workspace_snapshots(kind, account_id, scope_id, updated_at)
            VALUES($kind, $account, $scope, $updated)
            ON CONFLICT(kind, account_id, scope_id) DO UPDATE SET
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$scope", scopeId);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildFtsQuery(string query) =>
        string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static term => $"\"{term.Replace("\"", "\"\"")}\"*"));

    private static void ValidateWorkspaceKey(string kind, string accountId, string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
    }

    private static async Task DeleteMessageAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string mailboxId, string providerId, CancellationToken cancellationToken)
    {
        await using (var threadCommand = connection.CreateCommand())
        {
            threadCommand.Transaction = (SqliteTransaction)transaction;
            threadCommand.CommandText = "DELETE FROM message_threads WHERE mailbox_id = $mailbox AND provider_id = $provider;";
            threadCommand.Parameters.AddWithValue("$mailbox", mailboxId);
            threadCommand.Parameters.AddWithValue("$provider", providerId);
            await threadCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM messages WHERE mailbox_id = $mailbox AND provider_id = $provider;";
        command.Parameters.AddWithValue("$mailbox", mailboxId);
        command.Parameters.AddWithValue("$provider", providerId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertMessageAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, MailMessage message, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO messages(
                mailbox_id, provider_id, conversation_id, internet_message_id, folder_id, subject,
                from_name, from_address, recipients_json, received_at, preview, body, body_blob, is_html,
                is_read, has_attachments, importance, categories_json, etag, is_flagged,
                cc_recipients_json)
            VALUES(
                $mailbox, $provider, $conversation, $internet, $folder, $subject,
                $fromName, $fromAddress, $recipients, $received, $preview, $body, $bodyBlob, $isHtml,
                $isRead, $attachments, $importance, $categories, $etag, $flagged, $ccRecipients)
            ON CONFLICT(mailbox_id, provider_id) DO UPDATE SET
                conversation_id = excluded.conversation_id,
                internet_message_id = excluded.internet_message_id,
                folder_id = excluded.folder_id,
                subject = excluded.subject,
                from_name = excluded.from_name,
                from_address = excluded.from_address,
                recipients_json = excluded.recipients_json,
                cc_recipients_json = CASE
                    WHEN $hasCcRecipients = 1 THEN excluded.cc_recipients_json
                    ELSE messages.cc_recipients_json
                END,
                received_at = excluded.received_at,
                preview = excluded.preview,
                body = CASE WHEN excluded.body_blob IS NULL THEN messages.body ELSE NULL END,
                body_blob = COALESCE(excluded.body_blob, messages.body_blob),
                is_html = excluded.is_html,
                is_read = excluded.is_read,
                has_attachments = excluded.has_attachments,
                importance = excluded.importance,
                categories_json = excluded.categories_json,
                etag = excluded.etag,
                is_flagged = excluded.is_flagged;
            """;
        command.Parameters.AddWithValue("$mailbox", message.MailboxId);
        command.Parameters.AddWithValue("$provider", message.ProviderId);
        command.Parameters.AddWithValue("$conversation", (object?)message.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$internet", (object?)message.InternetMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$folder", message.FolderId);
        command.Parameters.AddWithValue("$subject", message.Subject);
        command.Parameters.AddWithValue("$fromName", message.From.Name);
        command.Parameters.AddWithValue("$fromAddress", message.From.Address);
        command.Parameters.AddWithValue("$recipients", JsonSerializer.Serialize(message.To));
        command.Parameters.AddWithValue("$ccRecipients", JsonSerializer.Serialize(message.Cc ?? []));
        command.Parameters.AddWithValue("$hasCcRecipients", message.Cc is not null);
        command.Parameters.AddWithValue("$received", message.ReceivedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$preview", message.Preview);
        command.Parameters.AddWithValue("$body", DBNull.Value);
        command.Parameters.AddWithValue("$bodyBlob", (object?)EncodeBody(message.Body) ?? DBNull.Value);
        command.Parameters.AddWithValue("$isHtml", message.IsHtml);
        command.Parameters.AddWithValue("$isRead", message.IsRead);
        command.Parameters.AddWithValue("$attachments", message.HasAttachments);
        command.Parameters.AddWithValue("$importance", (int)message.Importance);
        command.Parameters.AddWithValue("$categories", JsonSerializer.Serialize(message.Categories));
        command.Parameters.AddWithValue("$etag", (object?)message.ETag ?? DBNull.Value);
        command.Parameters.AddWithValue("$flagged", message.IsFlagged);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var threadCommand = connection.CreateCommand();
        threadCommand.Transaction = (SqliteTransaction)transaction;
        threadCommand.CommandText = """
            INSERT INTO message_threads(thread_id, mailbox_id, provider_id, received_at)
            VALUES($thread, $mailbox, $provider, $received)
            ON CONFLICT(mailbox_id, provider_id) DO UPDATE SET
                thread_id = excluded.thread_id,
                received_at = excluded.received_at;
            """;
        threadCommand.Parameters.AddWithValue("$thread", ConversationThread.ThreadIdentity(message));
        threadCommand.Parameters.AddWithValue("$mailbox", message.MailboxId);
        threadCommand.Parameters.AddWithValue("$provider", message.ProviderId);
        threadCommand.Parameters.AddWithValue("$received", message.ReceivedAt.ToString("O", CultureInfo.InvariantCulture));
        await threadCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static MailMessage ReadMessage(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        new MailAddress(reader.GetString(6), reader.GetString(7)),
        JsonSerializer.Deserialize<List<MailAddress>>(reader.GetString(8)) ?? [],
        DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.GetString(10),
        reader.IsDBNull(11) ? DecodeBody(reader.IsDBNull(20) ? null : (byte[])reader[20]) : reader.GetString(11),
        reader.GetBoolean(12),
        reader.GetBoolean(13),
        reader.GetBoolean(14),
        (MailImportance)reader.GetInt32(15),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(16)) ?? [],
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.GetBoolean(18),
        Cc: JsonSerializer.Deserialize<List<MailAddress>>(reader.GetString(19)) ?? []);



    public Task<bool> VacuumIfUsefulAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            static async Task<long> PragmaAsync(
                SqliteConnection connection,
                string pragma,
                CancellationToken cancellationToken)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = pragma;
                return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            }

            var pageSize = await PragmaAsync(connection, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false);
            var pageCount = await PragmaAsync(connection, "PRAGMA page_count;", cancellationToken).ConfigureAwait(false);
            var freePages = await PragmaAsync(connection, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false);
            var reclaimable = pageSize * freePages;
            if (reclaimable < 64L * 1024 * 1024 && freePages * 10 < pageCount)
            {
                return false;
            }

            var database = new FileInfo(Path.GetFullPath(databasePath));
            var root = Path.GetPathRoot(database.FullName);
            if (root is null || new DriveInfo(root).AvailableFreeSpace < database.Length * 2)
            {
                return false;
            }

            await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "VACUUM;", cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    public Task<bool> RunMaintenanceBatchAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            if (!_optimizedSearch)
            {
                var cursor = await MigrationCursorAsync(connection, "message-search-v2", cancellationToken).ConfigureAwait(false);
                await using var nextCommand = connection.CreateCommand();
                nextCommand.CommandText = """
                    SELECT max(rowid) FROM (
                        SELECT rowid FROM messages WHERE rowid > $cursor ORDER BY rowid LIMIT 250
                    );
                    """;
                nextCommand.Parameters.AddWithValue("$cursor", cursor);
                var nextValue = await nextCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (nextValue is not long next)
                {
                    await ExecuteAsync(connection, """
                        INSERT INTO store_migrations(name, cursor, completed)
                        VALUES('message-search-v2', 0, 1)
                        ON CONFLICT(name) DO UPDATE SET completed = 1;
                        DROP TRIGGER IF EXISTS messages_ai;
                        DROP TRIGGER IF EXISTS messages_ad;
                        DROP TRIGGER IF EXISTS messages_au;
                        DROP TABLE IF EXISTS message_search;
                        """, cancellationToken).ConfigureAwait(false);
                    _optimizedSearch = true;
                    return true;
                }

                await using var migrate = connection.CreateCommand();
                migrate.CommandText = """
                    INSERT OR REPLACE INTO message_search_v2(
                        rowid, subject, from_name, from_address, preview, body)
                    SELECT rowid, subject, from_name, from_address, preview,
                           coalesce(body, decode_body(body_blob))
                    FROM messages WHERE rowid > $cursor AND rowid <= $next;
                    INSERT INTO store_migrations(name, cursor, completed)
                    VALUES('message-search-v2', $next, 0)
                    ON CONFLICT(name) DO UPDATE SET cursor = excluded.cursor;
                    """;
                migrate.Parameters.AddWithValue("$cursor", cursor);
                migrate.Parameters.AddWithValue("$next", next);
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }


            if (!_correspondentsReady)
            {
                var cursor = await MigrationCursorAsync(connection, "message-correspondents-v1", cancellationToken).ConfigureAwait(false);
                await using var nextCommand = connection.CreateCommand();
                nextCommand.CommandText = """
                    SELECT max(rowid) FROM (
                        SELECT rowid FROM messages WHERE rowid > $cursor ORDER BY rowid LIMIT 250
                    );
                    """;
                nextCommand.Parameters.AddWithValue("$cursor", cursor);
                var nextValue = await nextCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (nextValue is not long next)
                {
                    await ExecuteAsync(connection, """
                        INSERT INTO store_migrations(name, cursor, completed)
                        VALUES('message-correspondents-v1', 0, 1)
                        ON CONFLICT(name) DO UPDATE SET completed = 1;
                        """, cancellationToken).ConfigureAwait(false);
                    _correspondentsReady = true;
                    return true;
                }

                await using var migrate = connection.CreateCommand();
                migrate.CommandText = """
                    INSERT OR IGNORE INTO message_correspondents
                    SELECT rowid, mailbox_id, lower(trim(from_address)), trim(from_name), received_at
                    FROM messages
                    WHERE rowid > $cursor AND rowid <= $next AND trim(from_address) <> '';
                    INSERT OR IGNORE INTO message_correspondents
                    SELECT messages.rowid, messages.mailbox_id,
                           lower(trim(json_extract(recipient.value, '$.Address'))),
                           trim(coalesce(json_extract(recipient.value, '$.Name'), '')), messages.received_at
                    FROM messages, json_each(messages.recipients_json) AS recipient
                    WHERE messages.rowid > $cursor AND messages.rowid <= $next
                      AND trim(coalesce(json_extract(recipient.value, '$.Address'), '')) <> '';
                    INSERT OR IGNORE INTO message_correspondents
                    SELECT messages.rowid, messages.mailbox_id,
                           lower(trim(json_extract(recipient.value, '$.Address'))),
                           trim(coalesce(json_extract(recipient.value, '$.Name'), '')), messages.received_at
                    FROM messages, json_each(messages.cc_recipients_json) AS recipient
                    WHERE messages.rowid > $cursor AND messages.rowid <= $next
                      AND trim(coalesce(json_extract(recipient.value, '$.Address'), '')) <> '';
                    INSERT INTO store_migrations(name, cursor, completed)
                    VALUES('message-correspondents-v1', $next, 0)
                    ON CONFLICT(name) DO UPDATE SET cursor = excluded.cursor;
                    """;
                migrate.Parameters.AddWithValue("$cursor", cursor);
                migrate.Parameters.AddWithValue("$next", next);
                await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            if (await MigrationCompleteAsync(connection, "message-bodies-v1", cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await using var compress = connection.CreateCommand();
            compress.CommandText = """
                UPDATE messages
                SET body_blob = encode_body(body), body = NULL
                WHERE rowid IN (
                    SELECT rowid FROM messages WHERE body IS NOT NULL ORDER BY rowid LIMIT 100
                );
                """;
            var changed = await compress.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 0)
            {
                await ExecuteAsync(connection, """
                    INSERT INTO store_migrations(name, cursor, completed)
                    VALUES('message-bodies-v1', 0, 1)
                    ON CONFLICT(name) DO UPDATE SET completed = 1;
                    """, cancellationToken).ConfigureAwait(false);
                return false;
            }
            return true;
        }, cancellationToken);

    private static async Task RunOnceAsync(
        SqliteConnection connection,
        string name,
        string sql,
        CancellationToken cancellationToken)
    {
        if (await MigrationCompleteAsync(connection, name, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO store_migrations(name, cursor, completed) VALUES($name, 0, 1)
            ON CONFLICT(name) DO UPDATE SET completed = 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> MigrationCompleteAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed FROM store_migrations WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0) == 1;
    }

    private static async Task<long> MigrationCursorAsync(
        SqliteConnection connection,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cursor FROM store_migrations WHERE name = $name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0);
    }

    private static byte[]? EncodeBody(string? body)
    {
        if (body is null)
        {
            return null;
        }
        var raw = Encoding.UTF8.GetBytes(body);
        using var output = new MemoryStream();
        output.WriteByte(1);
        using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(raw);
        }
        if (output.Length < raw.Length + 1)
        {
            return output.ToArray();
        }
        var stored = new byte[raw.Length + 1];
        raw.CopyTo(stored, 1);
        return stored;
    }

    private static string? DecodeBody(byte[]? stored)
    {
        if (stored is null)
        {
            return null;
        }
        if (stored.Length == 0 || stored[0] == 0)
        {
            return Encoding.UTF8.GetString(stored.AsSpan(Math.Min(1, stored.Length)));
        }
        if (stored[0] != 1)
        {
            throw new InvalidDataException("Unknown cached mail body encoding.");
        }
        using var input = new MemoryStream(stored, 1, stored.Length - 1, writable: false);
        using var decompressor = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decompressor.CopyTo(output);
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
    private static object FormatNullable(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS accounts(
            provider_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            tenant_id TEXT NOT NULL,
            email TEXT NOT NULL,
            display_name TEXT NOT NULL,
            capabilities INTEGER NOT NULL,
            PRIMARY KEY(provider_id, account_id)
        );

        CREATE TABLE IF NOT EXISTS provider_tokens(
            provider_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            access_token TEXT NOT NULL,
            refresh_token TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            scopes TEXT NOT NULL,
            PRIMARY KEY(provider_id, account_id)
        );

        CREATE TABLE IF NOT EXISTS messages(
            rowid INTEGER PRIMARY KEY,
            mailbox_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            conversation_id TEXT,
            internet_message_id TEXT,
            folder_id TEXT NOT NULL,
            subject TEXT NOT NULL,
            from_name TEXT NOT NULL,
            from_address TEXT NOT NULL,
            recipients_json TEXT NOT NULL,
            cc_recipients_json TEXT NOT NULL DEFAULT '[]',
            received_at TEXT NOT NULL,
            preview TEXT NOT NULL,
            body TEXT,
            body_blob BLOB,
            is_html INTEGER NOT NULL,
            is_read INTEGER NOT NULL,
            has_attachments INTEGER NOT NULL,
            importance INTEGER NOT NULL,
            categories_json TEXT NOT NULL,
            etag TEXT,
            is_flagged INTEGER NOT NULL DEFAULT 0,
            UNIQUE(mailbox_id, provider_id)
        );

        CREATE TABLE IF NOT EXISTS mailboxes(
            account_id TEXT NOT NULL,
            address TEXT NOT NULL,
            display_name TEXT NOT NULL,
            is_shared INTEGER NOT NULL,
            can_send_as INTEGER NOT NULL,
            can_send_on_behalf INTEGER NOT NULL,
            PRIMARY KEY(account_id, address)
        );

        CREATE TABLE IF NOT EXISTS mail_folders(
            mailbox_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            unread_count INTEGER NOT NULL,
            total_count INTEGER NOT NULL,
            well_known_name TEXT,
            parent_provider_id TEXT,
            PRIMARY KEY(mailbox_id, provider_id)
        );

        CREATE INDEX IF NOT EXISTS messages_received_at ON messages(received_at DESC);
        CREATE INDEX IF NOT EXISTS messages_mailbox ON messages(mailbox_id, received_at DESC);
        CREATE INDEX IF NOT EXISTS messages_folder
            ON messages(mailbox_id, folder_id, received_at DESC, rowid DESC);
        CREATE INDEX IF NOT EXISTS messages_counts
            ON messages(mailbox_id, is_read, is_flagged);

        CREATE TABLE IF NOT EXISTS message_threads(
            thread_id TEXT NOT NULL,
            mailbox_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            received_at TEXT NOT NULL,
            PRIMARY KEY(mailbox_id, provider_id)
        );
        CREATE INDEX IF NOT EXISTS message_threads_lookup ON message_threads(thread_id, received_at);

        CREATE VIRTUAL TABLE IF NOT EXISTS message_search_v2 USING fts5(
            subject, from_name, from_address, preview, body,
            content='', contentless_delete=1
        );
        CREATE TRIGGER IF NOT EXISTS messages_v2_ai AFTER INSERT ON messages BEGIN
            INSERT INTO message_search_v2(rowid, subject, from_name, from_address, preview, body)
            VALUES (new.rowid, new.subject, new.from_name, new.from_address, new.preview,
                    coalesce(new.body, decode_body(new.body_blob)));
        END;
        CREATE TRIGGER IF NOT EXISTS messages_v2_ad AFTER DELETE ON messages BEGIN
            DELETE FROM message_search_v2 WHERE rowid = old.rowid;
        END;
        CREATE TRIGGER IF NOT EXISTS messages_v2_au AFTER UPDATE ON messages BEGIN
            UPDATE message_search_v2
            SET subject = new.subject,
                from_name = new.from_name,
                from_address = new.from_address,
                preview = new.preview,
                body = coalesce(new.body, decode_body(new.body_blob))
            WHERE rowid = old.rowid;
        END;

        CREATE TABLE IF NOT EXISTS store_migrations(
            name TEXT PRIMARY KEY,
            cursor INTEGER NOT NULL DEFAULT 0,
            completed INTEGER NOT NULL DEFAULT 0
        );


        CREATE TABLE IF NOT EXISTS message_correspondents(
            message_rowid INTEGER NOT NULL,
            mailbox_id TEXT NOT NULL,
            email TEXT NOT NULL,
            display_name TEXT NOT NULL,
            contacted_at TEXT NOT NULL,
            PRIMARY KEY(message_rowid, email, display_name)
        );
        CREATE INDEX IF NOT EXISTS message_correspondents_lookup
            ON message_correspondents(email, contacted_at DESC);
        DROP TRIGGER IF EXISTS message_correspondents_ai;
        DROP TRIGGER IF EXISTS message_correspondents_ad;
        DROP TRIGGER IF EXISTS message_correspondents_au;
        CREATE TRIGGER message_correspondents_ai AFTER INSERT ON messages BEGIN
            INSERT OR IGNORE INTO message_correspondents
            SELECT new.rowid, new.mailbox_id, correspondent.email, correspondent.display_name, new.received_at
            FROM (
                SELECT lower(trim(new.from_address)) AS email, trim(new.from_name) AS display_name
                WHERE trim(new.from_address) <> ''
                UNION
                SELECT lower(trim(json_extract(value, '$.Address'))),
                       trim(coalesce(json_extract(value, '$.Name'), ''))
                FROM json_each(new.recipients_json)
                WHERE trim(coalesce(json_extract(value, '$.Address'), '')) <> ''
                UNION
                SELECT lower(trim(json_extract(value, '$.Address'))),
                       trim(coalesce(json_extract(value, '$.Name'), ''))
                FROM json_each(new.cc_recipients_json)
                WHERE trim(coalesce(json_extract(value, '$.Address'), '')) <> ''
            ) AS correspondent;
        END;
        CREATE TRIGGER message_correspondents_ad AFTER DELETE ON messages BEGIN
            DELETE FROM message_correspondents WHERE message_rowid = old.rowid;
        END;
        CREATE TRIGGER message_correspondents_au
        AFTER UPDATE OF mailbox_id, from_name, from_address, recipients_json, cc_recipients_json, received_at ON messages BEGIN
            DELETE FROM message_correspondents WHERE message_rowid = old.rowid;
            INSERT OR IGNORE INTO message_correspondents
            SELECT new.rowid, new.mailbox_id, correspondent.email, correspondent.display_name, new.received_at
            FROM (
                SELECT lower(trim(new.from_address)) AS email, trim(new.from_name) AS display_name
                WHERE trim(new.from_address) <> ''
                UNION
                SELECT lower(trim(json_extract(value, '$.Address'))),
                       trim(coalesce(json_extract(value, '$.Name'), ''))
                FROM json_each(new.recipients_json)
                WHERE trim(coalesce(json_extract(value, '$.Address'), '')) <> ''
                UNION
                SELECT lower(trim(json_extract(value, '$.Address'))),
                       trim(coalesce(json_extract(value, '$.Name'), ''))
                FROM json_each(new.cc_recipients_json)
                WHERE trim(coalesce(json_extract(value, '$.Address'), '')) <> ''
            ) AS correspondent;
        END;
        CREATE TABLE IF NOT EXISTS sync_cursors(
            mailbox_id TEXT PRIMARY KEY,
            cursor TEXT NOT NULL,
            is_complete INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS workspace_items(
            rowid INTEGER PRIMARY KEY,
            kind TEXT NOT NULL,
            account_id TEXT NOT NULL,
            scope_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            search_text TEXT NOT NULL,
            period_start TEXT,
            period_end TEXT,
            updated_at TEXT NOT NULL,
            UNIQUE(kind, account_id, scope_id, provider_id)
        );
        CREATE TABLE IF NOT EXISTS workspace_snapshots(
            kind TEXT NOT NULL,
            account_id TEXT NOT NULL,
            scope_id TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(kind, account_id, scope_id)
        );
        CREATE INDEX IF NOT EXISTS workspace_items_lookup
            ON workspace_items(kind, account_id, scope_id);
        CREATE INDEX IF NOT EXISTS workspace_items_period
            ON workspace_items(kind, account_id, scope_id, period_start, period_end);

        CREATE VIRTUAL TABLE IF NOT EXISTS workspace_search USING fts5(
            search_text,
            content='workspace_items', content_rowid='rowid'
        );
        CREATE TRIGGER IF NOT EXISTS workspace_items_ai AFTER INSERT ON workspace_items BEGIN
            INSERT INTO workspace_search(rowid, search_text) VALUES (new.rowid, new.search_text);
        END;
        CREATE TRIGGER IF NOT EXISTS workspace_items_ad AFTER DELETE ON workspace_items BEGIN
            INSERT INTO workspace_search(workspace_search, rowid, search_text)
            VALUES ('delete', old.rowid, old.search_text);
        END;
        CREATE TRIGGER IF NOT EXISTS workspace_items_au AFTER UPDATE ON workspace_items BEGIN
            INSERT INTO workspace_search(workspace_search, rowid, search_text)
            VALUES ('delete', old.rowid, old.search_text);
            INSERT INTO workspace_search(rowid, search_text) VALUES (new.rowid, new.search_text);
        END;

        CREATE TABLE IF NOT EXISTS local_drafts(
            id TEXT PRIMARY KEY,
            account_id TEXT NOT NULL,
            mailbox_id TEXT NOT NULL,
            recipients TEXT NOT NULL,
            cc TEXT NOT NULL,
            bcc TEXT NOT NULL,
            subject TEXT NOT NULL,
            body TEXT NOT NULL,
            attachments_json TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            is_html INTEGER NOT NULL DEFAULT 0,
            provider_draft_id TEXT,
            synced_local_updated_at TEXT,
            provider_updated_at TEXT,
            provider_etag TEXT,
            conversation_identity TEXT
        );
        CREATE INDEX IF NOT EXISTS local_drafts_updated_at ON local_drafts(updated_at DESC);
        """;


    private const string LegacySearchSchema = """
        CREATE VIRTUAL TABLE IF NOT EXISTS message_search USING fts5(
            subject, from_name, from_address, preview, body,
            content='messages', content_rowid='rowid'
        );
        CREATE TRIGGER IF NOT EXISTS messages_ai AFTER INSERT ON messages BEGIN
            INSERT INTO message_search(rowid, subject, from_name, from_address, preview, body)
            VALUES (new.rowid, new.subject, new.from_name, new.from_address, new.preview, new.body);
        END;
        CREATE TRIGGER IF NOT EXISTS messages_ad AFTER DELETE ON messages BEGIN
            INSERT INTO message_search(message_search, rowid, subject, from_name, from_address, preview, body)
            VALUES ('delete', old.rowid, old.subject, old.from_name, old.from_address, old.preview, old.body);
        END;
        CREATE TRIGGER IF NOT EXISTS messages_au AFTER UPDATE ON messages BEGIN
            INSERT INTO message_search(message_search, rowid, subject, from_name, from_address, preview, body)
            VALUES ('delete', old.rowid, old.subject, old.from_name, old.from_address, old.preview, old.body);
            INSERT INTO message_search(rowid, subject, from_name, from_address, preview, body)
            VALUES (new.rowid, new.subject, new.from_name, new.from_address, new.preview, new.body);
        END;
        """;
    private const string BackfillThreadIndex = """
        INSERT OR IGNORE INTO message_threads(thread_id, mailbox_id, provider_id, received_at)
        SELECT mailbox_id || CASE
                   WHEN trim(coalesce(conversation_id, '')) <> ''
                       THEN ':conversation:' || trim(conversation_id)
                   WHEN trim(coalesce(internet_message_id, '')) <> ''
                       THEN ':internet:' || lower(trim(internet_message_id, ' <>'))
                   ELSE ':message:' || mailbox_id || ':' || trim(provider_id)
               END,
               mailbox_id,
               provider_id,
               received_at
        FROM messages;
        """;
}
