using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed class EncryptedMailStore(string databasePath, string key) : IMailStore, IDraftStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
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
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, BackfillThreadIndex, cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "messages", "is_flagged", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "messages", "cc_recipients_json", "TEXT NOT NULL DEFAULT '[]'", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "mail_folders", "parent_provider_id", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "is_html", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "provider_draft_id", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "synced_local_updated_at", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "provider_updated_at", "TEXT", cancellationToken).ConfigureAwait(false);
            await EnsureColumnAsync(connection, "local_drafts", "provider_etag", "TEXT", cancellationToken).ConfigureAwait(false);
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
                "DELETE FROM local_drafts WHERE account_id = $account;",
                "DELETE FROM message_threads WHERE mailbox_id IN (SELECT account_id || ':' || lower(address) FROM mailboxes WHERE account_id = $account);",
                "DELETE FROM messages WHERE mailbox_id IN (SELECT account_id || ':' || lower(address) FROM mailboxes WHERE account_id = $account);",
                "DELETE FROM sync_cursors WHERE mailbox_id LIKE $account || ':%';",
                "DELETE FROM mail_folders WHERE mailbox_id IN (SELECT account_id || ':' || lower(address) FROM mailboxes WHERE account_id = $account);",
                "DELETE FROM mailboxes WHERE account_id = $account;",
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
                    INSERT INTO sync_cursors(mailbox_id, cursor) VALUES($cursorId, $cursor)
                    ON CONFLICT(mailbox_id) DO UPDATE SET cursor = excluded.cursor;
                    """;
                cursorCommand.Parameters.AddWithValue("$cursorId", cursorId);
                cursorCommand.Parameters.AddWithValue("$cursor", page.NextCursor);
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

        return QueryMessagesAsync(filters.Count == 0 ? "" : $"WHERE {string.Join(" AND ", filters)}", limit, cancellationToken, parameters.ToArray());
    }

    public async Task<MailMessage?> GetMessageAsync(
        string mailboxId,
        string providerMessageId,
        CancellationToken cancellationToken = default) =>
        (await QueryMessagesAsync(
            "WHERE mailbox_id = $mailbox AND provider_id = $provider",
            1,
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
            "WHERE rowid IN (SELECT rowid FROM message_search WHERE message_search MATCH $query)",
            limit,
            cancellationToken,
            ("$query", ftsQuery));
    }

    public Task<IReadOnlyList<MailMessage>> GetThreadMessagesAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        QueryMessagesAsync(
            "WHERE EXISTS (SELECT 1 FROM message_threads thread WHERE thread.mailbox_id = messages.mailbox_id AND thread.provider_id = messages.provider_id AND thread.thread_id = $thread)",
            1000,
            cancellationToken,
            ("$thread", threadId));

    public Task<IReadOnlyList<DiscoveredPerson>> GetDiscoveredPeopleAsync(
        string query = "",
        int limit = 500,
        CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<DiscoveredPerson>>(async connection =>
        {
            var groups = new List<(string Email, string Name, string MailboxId, int Count, DateTimeOffset Last)>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH correspondents(email, display_name, mailbox_id, contacted_at) AS (
                    SELECT lower(trim(from_address)), trim(from_name), mailbox_id, received_at
                    FROM messages
                    UNION ALL
                    SELECT lower(trim(json_extract(recipient.value, '$.Address'))),
                           trim(json_extract(recipient.value, '$.Name')),
                           messages.mailbox_id,
                           messages.received_at
                    FROM messages, json_each(messages.recipients_json) AS recipient
                    UNION ALL
                    SELECT lower(trim(json_extract(recipient.value, '$.Address'))),
                           trim(json_extract(recipient.value, '$.Name')),
                           messages.mailbox_id,
                           messages.received_at
                    FROM messages, json_each(messages.cc_recipients_json) AS recipient
                )
                SELECT email, display_name, mailbox_id, count(*), max(contacted_at)
                FROM correspondents
                WHERE email <> ''
                  AND instr(email, '@') > 1
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
                    updated_at, is_html, provider_draft_id, synced_local_updated_at, provider_updated_at, provider_etag)
                VALUES(
                    $id, $account, $mailbox, $to, $cc, $bcc, $subject, $body, $attachments,
                    $updated, $isHtml, $providerDraft, $syncedLocal, $providerUpdated, $providerETag)
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
                       updated_at, is_html, provider_draft_id, synced_local_updated_at, provider_updated_at, provider_etag
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
                    reader.IsDBNull(14) ? null : reader.GetString(14)));
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
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) => WithLockAsync<IReadOnlyList<MailMessage>>(async connection =>
    {
        var messages = new List<MailMessage>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT mailbox_id, provider_id, conversation_id, internet_message_id, folder_id, subject,
                   from_name, from_address, recipients_json, received_at, preview, body, is_html,
                   is_read, has_attachments, importance, categories_json, etag, is_flagged,
                   cc_recipients_json
            FROM messages {where}
            ORDER BY received_at DESC LIMIT $limit;
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
            await action(GetConnection()).ConfigureAwait(false);
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
            return await action(GetConnection()).ConfigureAwait(false);
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
                from_name, from_address, recipients_json, received_at, preview, body, is_html,
                is_read, has_attachments, importance, categories_json, etag, is_flagged,
                cc_recipients_json)
            VALUES(
                $mailbox, $provider, $conversation, $internet, $folder, $subject,
                $fromName, $fromAddress, $recipients, $received, $preview, $body, $isHtml,
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
                body = COALESCE(excluded.body, messages.body),
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
        command.Parameters.AddWithValue("$body", (object?)message.Body ?? DBNull.Value);
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
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.GetBoolean(12),
        reader.GetBoolean(13),
        reader.GetBoolean(14),
        (MailImportance)reader.GetInt32(15),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(16)) ?? [],
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.GetBoolean(18),
        Cc: JsonSerializer.Deserialize<List<MailAddress>>(reader.GetString(19)) ?? []);

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

        CREATE TABLE IF NOT EXISTS message_threads(
            thread_id TEXT NOT NULL,
            mailbox_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            received_at TEXT NOT NULL,
            PRIMARY KEY(mailbox_id, provider_id)
        );
        CREATE INDEX IF NOT EXISTS message_threads_lookup ON message_threads(thread_id, received_at);

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

        CREATE TABLE IF NOT EXISTS sync_cursors(
            mailbox_id TEXT PRIMARY KEY,
            cursor TEXT NOT NULL
        );

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
            provider_etag TEXT
        );
        CREATE INDEX IF NOT EXISTS local_drafts_updated_at ON local_drafts(updated_at DESC);
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
