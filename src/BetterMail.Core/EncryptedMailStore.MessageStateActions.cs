using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    public Task<MailAction> QueueMessageStateAsync(MailAccount account, MailMessage message,
        bool? isRead = null, bool? isFlagged = null, bool? isPinned = null, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var related = (await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                .Where(action => action.Kind is MailActionKind.Move or MailActionKind.UpdateState && MatchesMessage(action, message)).ToArray();
            var previous = related.LastOrDefault(action => action.Kind == MailActionKind.UpdateState);
            var action = previous is { Running: false, Accepted: false } ? previous
                : new MailAction(Guid.NewGuid().ToString("N"), account.AccountId, message.MailboxId, related.FirstOrDefault()?.ItemId ?? message.ProviderId,
                    MailActionKind.UpdateState, message.Subject, DateTimeOffset.UtcNow, related.LastOrDefault()?.ProviderId ?? message.ProviderId,
                    PreviousRead: message.IsRead, PreviousFlagged: message.IsFlagged, PreviousPinned: message.IsPinned);
            action = action with { ReadValue = isRead ?? action.ReadValue, FlagValue = isFlagged ?? action.FlagValue,
                PinValue = isPinned ?? action.PinValue, Error = null };
            await WriteActionAsync(connection, transaction, action, cancellationToken).ConfigureAwait(false);
            await SetActionStateAsync(connection, transaction, action, false, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return action;
        }, cancellationToken);

    public Task CompleteMessageStateAsync(MailAction action, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await SetActionStateAsync(connection, transaction, action, false, cancellationToken).ConfigureAwait(false);
            var later = (await ReadActionsAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
                .Where(item => item.Kind == MailActionKind.UpdateState && item.Id != action.Id && item.MailboxId == action.MailboxId && item.ItemId == action.ItemId);
            foreach (var pending in later) await SetActionStateAsync(connection, transaction, pending, false, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM mail_actions WHERE id = $id AND kind = 3;";
            command.Parameters.AddWithValue("$id", action.Id);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task SetActionStateAsync(SqliteConnection connection, SqliteTransaction? transaction,
        MailAction action, bool restore, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE messages SET is_read = coalesce($read, is_read), is_flagged = coalesce($flag, is_flagged), is_pinned = coalesce($pin, is_pinned) WHERE mailbox_id = $mailbox AND provider_id = $provider;";
        command.Parameters.AddWithValue("$read", (object?)(restore && action.ReadValue is not null ? action.PreviousRead : action.ReadValue) ?? DBNull.Value);
        command.Parameters.AddWithValue("$flag", (object?)(restore && action.FlagValue is not null ? action.PreviousFlagged : action.FlagValue) ?? DBNull.Value);
        command.Parameters.AddWithValue("$pin", (object?)(restore && action.PinValue is not null ? action.PreviousPinned : action.PinValue) ?? DBNull.Value);
        command.Parameters.AddWithValue("$mailbox", action.MailboxId);
        command.Parameters.AddWithValue("$provider", action.ProviderId!);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
