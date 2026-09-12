using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    private static async Task InitializeMailActionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS mail_actions (
                id TEXT PRIMARY KEY, account_id TEXT NOT NULL, mailbox_id TEXT NOT NULL,
                item_id TEXT NOT NULL, kind INTEGER NOT NULL, payload_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS mail_actions_item ON mail_actions(mailbox_id, item_id);
            CREATE INDEX IF NOT EXISTS mail_actions_draft ON mail_actions(item_id, kind);
            """, cancellationToken).ConfigureAwait(false);
        var actions = await ReadActionsAsync(connection, null, cancellationToken, includeTombstones: true).ConfigureAwait(false);
        foreach (var action in actions.Where(static action => action.Running))
            await WriteActionAsync(connection, null, action with { Running = false }, cancellationToken).ConfigureAwait(false);

        // Existing outbox payloads and acceptance markers remain in local_drafts.
        var sends = new List<MailAction>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, account_id, mailbox_id, subject, updated_at, send_accepted, provider_draft_id FROM local_drafts WHERE is_queued = 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                sends.Add(new("send:" + reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(0), MailActionKind.Send, reader.GetString(3), ParseTimestamp(reader.GetString(4)),
                    ProviderId: reader.IsDBNull(6) ? null : reader.GetString(6), Accepted: reader.GetBoolean(5)));
        }
        foreach (var send in sends.Where(send => actions.All(action => action.Id != send.Id)))
            await WriteActionAsync(connection, null, send, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<MailAction>> GetMailActionsAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<MailAction>>(async connection =>
            (await ReadActionsAsync(connection, null, cancellationToken).ConfigureAwait(false))
                .Where(static action => !action.Accepted).ToArray(), cancellationToken);

    public Task<IReadOnlyList<MailAction>> GetAcceptedMovesAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<MailAction>>(async connection =>
            (await ReadActionsAsync(connection, null, cancellationToken).ConfigureAwait(false))
                .Where(static action => action.Kind == MailActionKind.Move)
                .GroupBy(static action => (action.MailboxId, action.ItemId))
                .Where(static group => group.All(static action => action.Accepted))
                .Select(static group => group.Last()).ToArray(), cancellationToken);

    public Task ConfirmMoveAsync(MailAction action, MailMessage? current, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            if (current is not null && (current.MailboxId != action.MailboxId || current.ProviderId != action.ProviderId))
                throw new InvalidOperationException("The provider returned a different message.");
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var actions = await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (actions.Any(candidate => candidate.MailboxId == action.MailboxId && candidate.ItemId == action.ItemId && !candidate.Accepted)) return;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM mail_actions WHERE mailbox_id = $mailbox AND item_id = $item AND kind = 0;";
            command.Parameters.AddWithValue("$mailbox", action.MailboxId);
            command.Parameters.AddWithValue("$item", action.ItemId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
                await DeleteMessageAsync(connection, transaction, action.MailboxId, action.ProviderId!, cancellationToken).ConfigureAwait(false);
            else
                await UpsertMessageAsync(connection, transaction, current, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<bool> IsDraftPendingDeletionAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM mail_actions WHERE kind = 1 AND item_id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }, cancellationToken);

    public Task QueueDraftDeletionAsync(LocalDraft draft, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var actions = await ReadActionsAsync(connection, null, cancellationToken, includeTombstones: true).ConfigureAwait(false);
            if (actions.Any(action => action.ItemId == draft.Id && action.Kind is MailActionKind.DeleteDraft or MailActionKind.Send))
                return;
            await WriteActionAsync(connection, null, new MailAction("delete:" + draft.Id,
                draft.AccountId, draft.MailboxId, draft.Id, MailActionKind.DeleteDraft,
                draft.Subject, DateTimeOffset.UtcNow, draft.ProviderDraftId), cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<MailAction> QueueMoveAsync(MailAccount account, MailMessage message, MailFolder destination,
        CancellationToken cancellationToken = default) => WithLockAsync(async connection =>
    {
        if (destination.MailboxId != message.MailboxId)
            throw new InvalidOperationException("The destination belongs to another mailbox.");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var actions = await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var related = actions.Where(action => action.Kind == MailActionKind.Move && MatchesMessage(action, message)).ToArray();
        var last = related.LastOrDefault();
        if (last is { Error: null } && last.DestinationId == destination.ProviderId) return last;
        var action = last is { Running: false, Accepted: false }
            ? last with { DestinationId = destination.ProviderId, DestinationName = destination.DisplayName, Error = null }
            : new MailAction(Guid.NewGuid().ToString("N"), account.AccountId, message.MailboxId,
                last?.ItemId ?? message.ProviderId, MailActionKind.Move, message.Subject, DateTimeOffset.UtcNow,
                last?.ProviderId ?? message.ProviderId, destination.ProviderId, destination.DisplayName,
                PreviousProviderIds: last?.PreviousProviderIds, SourceFolderId: message.FolderId, SourceWasUnread: message.IsUnread);
        await WriteActionAsync(connection, transaction, action, cancellationToken).ConfigureAwait(false);
        await UpsertMessageAsync(connection, transaction,
            message with { ProviderId = action.ProviderId!, FolderId = destination.ProviderId, IsRead = true }, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return action;
    }, cancellationToken);

    public Task<bool> CancelMailActionAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var actions = await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var action = actions.FirstOrDefault(action => action.Id == id);
            if (action is null || !action.CanCancel) return false;
            var related = actions.Where(candidate => candidate.Kind == action.Kind && candidate.MailboxId == action.MailboxId && candidate.ItemId == action.ItemId).ToArray();
            if (related[^1].Id != id) return false;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (action.Kind == MailActionKind.Move)
            {
                var previous = related.Length > 1 ? related[^2] : null;
                var failed = related.FirstOrDefault(candidate => candidate.Id != action.Id && !candidate.Accepted && candidate.Error is not null);
                var folder = failed?.SourceFolderId ?? previous?.DestinationId ?? action.SourceFolderId;
                if (folder is null) return false;
                command.CommandText = "UPDATE messages SET folder_id = $folder, is_read = $read WHERE mailbox_id = $mailbox AND provider_id = $provider;";
                command.Parameters.AddWithValue("$folder", folder);
                command.Parameters.AddWithValue("$read", previous is not null || !action.SourceWasUnread);
                command.Parameters.AddWithValue("$mailbox", action.MailboxId);
                command.Parameters.AddWithValue("$provider", action.ProviderId!);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (action.Kind == MailActionKind.UpdateState)
            {
                await SetActionStateAsync(connection, transaction, action, true, cancellationToken).ConfigureAwait(false);
            }
            else if (action.Kind == MailActionKind.Send)
            {
                command.CommandText = "UPDATE local_drafts SET is_queued = 0 WHERE id = $item AND send_accepted = 0;";
                command.Parameters.AddWithValue("$item", action.ItemId);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return false;
            }
            command.CommandText = "DELETE FROM mail_actions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<MailAction?> StartMailActionAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync<MailAction?>(async connection =>
        {
            var actions = await ReadActionsAsync(connection, null, cancellationToken).ConfigureAwait(false);
            var action = actions.FirstOrDefault(action => action.Id == id && !action.Accepted && !action.Running && !action.SendAttempted);
            if (action is null) return null;
            action = action with { Running = true, Error = null };
            await WriteActionAsync(connection, null, action, cancellationToken).ConfigureAwait(false);
            return action;
        }, cancellationToken);

    public Task FailMailActionAsync(string id, string error, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var action = (await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(action => action.Id == id && !action.Accepted);
            if (action is not null)
            {
                await WriteActionAsync(connection, transaction, action with { Running = false, Error = error }, cancellationToken).ConfigureAwait(false);
                if (action.Kind == MailActionKind.Move && action.SourceFolderId is not null)
                {
                    // Follow-up moves cannot run until this failure is recovered. Restore
                    // the actual source even if a newer destination was optimistically shown.
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE messages SET folder_id = $folder WHERE mailbox_id = $mailbox AND provider_id = $provider;";
                    command.Parameters.AddWithValue("$folder", action.SourceFolderId);
                    command.Parameters.AddWithValue("$mailbox", action.MailboxId);
                    command.Parameters.AddWithValue("$provider", action.ProviderId!);
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task MarkSendAttemptedAsync(string draftId, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var action = (await ReadActionsAsync(connection, null, cancellationToken).ConfigureAwait(false))
                .Single(action => action.Id == "send:" + draftId && action.Running && !action.Accepted);
            await WriteActionAsync(connection, null, action with { SendAttempted = true }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task MarkSendRejectedAsync(string draftId, string error, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var action = (await ReadActionsAsync(connection, null, cancellationToken).ConfigureAwait(false))
                .Single(action => action.Id == "send:" + draftId && !action.Accepted);
            await WriteActionAsync(connection, null, action with { SendAttempted = false, Running = false, Error = error }, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<bool> ReturnUnconfirmedSendToDraftAsync(string actionId, bool clearProviderMapping = false, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var action = (await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(action => action.Id == actionId && action.NeedsSendReview);
            if (action is null) return false;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE local_drafts SET is_queued = 0,
                    provider_draft_id = CASE WHEN $clear THEN NULL ELSE provider_draft_id END,
                    synced_local_updated_at = CASE WHEN $clear THEN NULL ELSE synced_local_updated_at END,
                    provider_updated_at = CASE WHEN $clear THEN NULL ELSE provider_updated_at END,
                    provider_etag = CASE WHEN $clear THEN NULL ELSE provider_etag END,
                    sync_status = NULL, sync_error = NULL
                WHERE id = $item AND send_accepted = 0;
                DELETE FROM mail_actions WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$item", action.ItemId);
            command.Parameters.AddWithValue("$id", action.Id);
            command.Parameters.AddWithValue("$clear", clearProviderMapping);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task CompleteMoveAsync(MailAction completed, MailMessage result, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            if (result.MailboxId != completed.MailboxId)
                throw new InvalidOperationException("The provider returned a message owned by another mailbox.");
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var related = (await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                .Where(action => action.Kind is MailActionKind.Move or MailActionKind.UpdateState && action.MailboxId == completed.MailboxId && action.ItemId == completed.ItemId).ToArray();
            foreach (var action in related)
                await WriteActionAsync(connection, transaction, action with
                {
                    ProviderId = result.ProviderId,
                    PreviousProviderIds = (action.PreviousProviderIds ?? []).Append(completed.ProviderId!).Distinct().ToArray(),
                    Accepted = action.Id == completed.Id || action.Accepted,
                    Running = action.Id == completed.Id ? false : action.Running,
                    DestinationId = action.Id == completed.Id ? result.FolderId : action.DestinationId,
                    SourceFolderId = action.Kind == MailActionKind.Move && action.Id != completed.Id && !action.Accepted ? result.FolderId : action.SourceFolderId
                }, cancellationToken).ConfigureAwait(false);
            var desired = related.LastOrDefault(action => action.Kind == MailActionKind.Move && action.Id != completed.Id && !action.Accepted);
            await UpsertMessageAsync(connection, transaction, result with
            {
                FolderId = desired?.DestinationId ?? result.FolderId,
                IsRead = true
            }, cancellationToken).ConfigureAwait(false);
            foreach (var state in related.Where(action => action.Kind == MailActionKind.UpdateState))
                await SetActionStateAsync(connection, transaction, state with { ProviderId = result.ProviderId }, false, cancellationToken).ConfigureAwait(false);
            if (completed.ProviderId != result.ProviderId)
                await DeleteMessageAsync(connection, transaction, completed.MailboxId, completed.ProviderId!, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<bool> TryRemoveConfirmedSentDraftAsync(LocalDraft expected, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            if (expected.IsQueued || expected.ProviderDraftId is null || expected.SyncedLocalUpdatedAt != expected.UpdatedAt) return false;
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM local_drafts WHERE id = $id AND account_id = $account AND mailbox_id = $mailbox
                AND provider_draft_id = $provider AND updated_at = $updated AND synced_local_updated_at = $updated
                AND is_queued = 0;
                """;
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", expected.AccountId);
            command.Parameters.AddWithValue("$mailbox", expected.MailboxId);
            command.Parameters.AddWithValue("$provider", expected.ProviderDraftId);
            command.Parameters.AddWithValue("$updated", expected.UpdatedAt.ToString("O"));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return false;
            await WriteActionAsync(connection, transaction, new MailAction("delete:" + expected.Id,
                expected.AccountId, expected.MailboxId, expected.Id, MailActionKind.DeleteDraft,
                expected.Subject, DateTimeOffset.UtcNow, expected.ProviderDraftId, Accepted: true), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task CompleteDraftDeletionAsync(MailAction action, string? providerId, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Keep the small tombstone: a late autosave or remote snapshot must not recreate this draft.
            await WriteActionAsync(connection, transaction, action with
            { Accepted = true, Running = false, ProviderId = providerId }, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM local_drafts WHERE id = $id;";
            command.Parameters.AddWithValue("$id", action.ItemId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static bool MatchesMessage(MailAction action, MailMessage message) =>
        action.MailboxId == message.MailboxId &&
        (action.ProviderId == message.ProviderId || action.ItemId == message.ProviderId ||
         (action.PreviousProviderIds ?? []).Contains(message.ProviderId));

    private static async Task<List<MailAction>> ReadActionsAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CancellationToken cancellationToken, bool includeTombstones = false)
    {
        var actions = new List<MailAction>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_json FROM mail_actions" +
            (includeTombstones ? "" : " WHERE kind = 0 OR json_extract(payload_json, '$.Accepted') = 0") + " ORDER BY rowid;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            actions.Add(JsonSerializer.Deserialize<MailAction>(reader.GetString(0))!);
        return actions;
    }

    private static async Task WriteActionAsync(SqliteConnection connection, SqliteTransaction? transaction,
        MailAction action, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mail_actions(id, account_id, mailbox_id, item_id, kind, payload_json)
            VALUES($id, $account, $mailbox, $item, $kind, $payload)
            ON CONFLICT(id) DO UPDATE SET payload_json = excluded.payload_json;
            """;
        command.Parameters.AddWithValue("$id", action.Id);
        command.Parameters.AddWithValue("$account", action.AccountId);
        command.Parameters.AddWithValue("$mailbox", action.MailboxId);
        command.Parameters.AddWithValue("$item", action.ItemId);
        command.Parameters.AddWithValue("$kind", (int)action.Kind);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(action));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
