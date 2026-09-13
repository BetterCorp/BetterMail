using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    public Task<MailAction?> ClaimAutomaticRecoveryAsync(string id, CancellationToken token = default) =>
        WithLockAsync<MailAction?>(async connection =>
        {
            var actions = await ReadActionsAsync(connection, null, token).ConfigureAwait(false);
            var action = actions.FirstOrDefault(action => action.Id == id);
            if (action is not { CanAutomaticallyRecover: true } || actions.TakeWhile(item => item.Id != id).Any(item =>
                item.MailboxId == action.MailboxId && item.ItemId == action.ItemId && !item.Accepted)) return null;
            action = action with { AutomaticRecoveryAttempted = true,
                AutomaticRecoveryDetails = "Automatic recovery was attempted. Check status if it did not complete." };
            await WriteActionAsync(connection, null, action, token).ConfigureAwait(false);
            return action;
        }, token);

    public Task RecordAutomaticRecoveryAsync(string id, string details, CancellationToken token = default) =>
        WithLockAsync(async connection =>
        {
            var action = (await ReadActionsAsync(connection, null, token).ConfigureAwait(false)).FirstOrDefault(action => action.Id == id);
            if (action is { AutomaticRecoveryAttempted: true })
                await WriteActionAsync(connection, null, action with { AutomaticRecoveryDetails = details }, token).ConfigureAwait(false);
        }, token);

    public Task<bool> RecoverMailActionAsync(MailAction expected, MailMessage verified, string expectedIdentity,
        CancellationToken token = default) => WithLockAsync(async connection =>
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        var actions = await ReadActionsAsync(connection, transaction, token).ConfigureAwait(false);
        var current = actions.FirstOrDefault(action => action.Id == expected.Id);
        // Lookup happens outside the store lock. Reject cancellation, edits or execution since it began.
        if (current is not { CanRecover: true } || JsonSerializer.Serialize(current) != JsonSerializer.Serialize(expected) ||
            verified.MailboxId != current.MailboxId || verified.IsDeleted ||
            string.IsNullOrWhiteSpace(expectedIdentity) || verified.InternetMessageId != expectedIdentity ||
            string.IsNullOrWhiteSpace(verified.ProviderId) || string.IsNullOrWhiteSpace(verified.FolderId)) return false;
        var related = actions.Where(action => action.MailboxId == current.MailboxId && action.ItemId == current.ItemId &&
            action.Kind is MailActionKind.Move or MailActionKind.UpdateState).ToArray();
        if (related.Any(action => action.Running) || related.TakeWhile(action => action.Id != current.Id).Any(action => !action.Accepted)) return false;
        var alreadyMoved = current.Kind == MailActionKind.Move && verified.FolderId == current.DestinationId;
        foreach (var action in related)
            await WriteActionAsync(connection, transaction, action with
            {
                ProviderId = verified.ProviderId,
                PreviousProviderIds = (action.PreviousProviderIds ?? []).Append(current.ProviderId!).Distinct().ToArray(),
                SourceFolderId = action.Kind == MailActionKind.Move && !action.Accepted ? verified.FolderId : action.SourceFolderId,
                SourceWasUnread = action.Kind == MailActionKind.Move && !action.Accepted ? verified.IsUnread : action.SourceWasUnread,
                Accepted = action.Accepted || action.Id == current.Id && alreadyMoved,
                RecoveredAtFailureCount = action.Id == current.Id ? action.FailureCount : action.RecoveredAtFailureCount,
                RetryAuthorizedAtFailureCount = action.Id == current.Id ? action.FailureCount : action.RetryAuthorizedAtFailureCount
            }, token).ConfigureAwait(false);
        var desired = related.LastOrDefault(action => action.Kind == MailActionKind.Move && !action.Accepted &&
            !(action.Id == current.Id && alreadyMoved));
        await UpsertMessageAsync(connection, transaction, verified with
        {
            FolderId = desired?.DestinationId ?? verified.FolderId,
            IsRead = desired is not null || verified.IsRead
        }, token).ConfigureAwait(false);
        foreach (var state in related.Where(action => action.Kind == MailActionKind.UpdateState && !action.Accepted))
            await SetActionStateAsync(connection, transaction, state with { ProviderId = verified.ProviderId }, false, token).ConfigureAwait(false);
        if (verified.ProviderId != current.ProviderId)
            await DeleteMessageAsync(connection, transaction, current.MailboxId, current.ProviderId!, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }, token);
}
