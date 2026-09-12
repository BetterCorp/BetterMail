using System.Net;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    public AsyncCommand<MailAction> CancelBusyActionCommand { get; }
    public AsyncCommand<MailAction> ReturnUnconfirmedSendCommand { get; }
    public AsyncCommand<MailAction> ConfirmSentCommand { get; }

    private Task ReturnUnconfirmedSendAsync(MailAction action) => ResolveUnconfirmedSendAsync(action, false);
    private Task ConfirmSentAsync(MailAction action) => ResolveUnconfirmedSendAsync(action, true);

    private async Task ResolveUnconfirmedSendAsync(MailAction action, bool sent)
    {
        if (_store is null) return;
        var gate = _draftSyncLocks.GetOrAdd(action.MailboxId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (await _store.GetMailActionAsync(action.Id) is not { NeedsSendReview: true }) return;
            if (sent)
            {
                await _store.MarkOutboxSendAcceptedAsync(action.ItemId);
                await _store.DeleteLocalDraftAsync(action.ItemId);
            }
            else
            {
                var account = Accounts.Single(account => account.AccountId == action.AccountId);
                var mailbox = Mailboxes.Single(mailbox => mailbox.Id == action.MailboxId);
                if (_provider is null) throw new InvalidOperationException("Reconnect the account before returning this message to drafts.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await new OutboxService(_provider, _store).ReturnToDraftAsync(account, mailbox, action.Id, timeout.Token);
            }
            await RefreshDraftsAsync();
            Status = sent ? "Send marked as confirmed" : "Returned to drafts. Check Sent before sending again.";
        }
        catch (Exception error) { Error = error.Message; }
        finally { gate.Release(); }
    }


    private async Task CancelBusyActionAsync(MailAction action)
    {
        if (_store is null) return;
        try
        {
            if (!await _store.CancelMailActionAsync(action.Id))
            {
                await RefreshBusyActionsAsync();
                return;
            }
            await RefreshDraftsAsync();
            await LoadMessagesAsync();
            Status = "Action cancelled";
        }
        catch (Exception exception) { Error = exception.Message; }
    }

    private async Task RefreshMcpChangesAsync()
    {
        await RefreshDraftsAsync();
        await LoadMessagesAsync();
        _ = SyncAsync();
    }

    private async Task RefreshBusyActionsAsync()
    {
        if (_store is null) return;
        CollectionUpdates.Reconcile(BusyActions, await _store.GetMailActionsAsync(), static action => action.Id);
        RaiseDraftState();
    }

    private async Task ProcessMailActionsAsync()
    {
        if (_store is null || _provider is null) return;
        // Confirm previous successes with a current read when folder deltas did not confirm them.
        // This also lets changes from another client replace our completed local projection.
        foreach (var completed in await _store.GetAcceptedMovesAsync())
        {
            var account = Accounts.FirstOrDefault(account => account.AccountId == completed.AccountId);
            var mailbox = Mailboxes.FirstOrDefault(mailbox => mailbox.Id == completed.MailboxId && mailbox.AccountId == completed.AccountId);
            if (account is null || mailbox is null) continue;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                MailMessage? current;
                try { current = await _provider.GetMessageAsync(account, mailbox, completed.ProviderId!, timeout.Token); }
                catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { current = null; }
                await _store.ConfirmMoveAsync(completed, current, timeout.Token);
            }
            catch (Exception) { /* A later sync can confirm the accepted move. */ }
        }
        var blocked = new HashSet<(string Mailbox, string Item)>();
        var batch = await _store.GetMailActionsAsync();
        foreach (var pending in batch.Where(static action => action.Kind != MailActionKind.Send))
        {
            if (blocked.Contains((pending.MailboxId, pending.ItemId))) continue;
            var account = Accounts.FirstOrDefault(account => account.AccountId == pending.AccountId);
            var mailbox = Mailboxes.FirstOrDefault(mailbox => mailbox.Id == pending.MailboxId && mailbox.AccountId == pending.AccountId);
            if (account is null || mailbox is null) continue;
            var action = await _store.StartMailActionAsync(pending.Id);
            if (action is null) continue;
            await RefreshBusyActionsAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                if (action.Kind == MailActionKind.Move)
                {
                    var message = await _store.GetMessageAsync(mailbox.Id, action.ProviderId!, timeout.Token)
                        ?? throw new InvalidOperationException("The local message is unavailable.");
                    if (action.SourceWasUnread)
                        await _provider.MarkReadAsync(account, mailbox, action.ProviderId!, true, timeout.Token);
                    var moved = await _provider.MoveMessageWithResultAsync(account, mailbox,
                        action.ProviderId!, action.DestinationId!, timeout.Token);
                    await _store.CompleteMoveAsync(action, message with { ProviderId = moved.ProviderId, FolderId = moved.FolderId });
                    var displayed = Messages.FirstOrDefault(candidate => SameMessage(candidate, message));
                    if (displayed is not null)
                    {
                        var local = await _store.GetMessageAsync(mailbox.Id, moved.ProviderId);
                        if (local is not null) ApplyMessageUpdate(displayed, local);
                    }
                }
                else
                {
                    var mailboxLock = _draftSyncLocks.GetOrAdd(mailbox.Id, static _ => new SemaphoreSlim(1, 1));
                    await mailboxLock.WaitAsync(timeout.Token);
                    try
                    {
                        // A cloud draft creation already in flight can finish after local deletion was queued.
                        var draft = await _store.GetLocalDraftAsync(action.ItemId, timeout.Token);
                        var providerId = draft?.ProviderDraftId ?? action.ProviderId;
                        if (providerId is not null && _provider.SupportsCloudDraftsFor(account) &&
                            !await _provider.IsDraftSentAsync(account, mailbox, providerId, timeout.Token))
                        {
                            try { await _provider.DeleteDraftAsync(account, mailbox, providerId, timeout.Token); }
                            catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { }
                        }
                        await _store.CompleteDraftDeletionAsync(action, providerId);
                    }
                    finally { mailboxLock.Release(); }
                }
            }
            catch (Exception exception)
            {
                await _store.FailMailActionAsync(action.Id, exception.Message);
                blocked.Add((action.MailboxId, action.ItemId));
            }
            await RefreshBusyActionsAsync();
        }
    }
}
