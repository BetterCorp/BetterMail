using System.Net;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<string, string> _busyChecks = [];
    public AsyncCommand<MailAction> RecoverBusyActionCommand { get; }

    private async Task RecoverBusyActionAsync(MailAction action)
    {
        if (_store is null || _provider is null) return;
        _busyChecks[action.Id] = "Verifying message identity…";
        await RefreshBusyActionsAsync();
        try
        {
            var account = Accounts.Single(account => account.AccountId == action.AccountId);
            var mailbox = Mailboxes.Single(mailbox => mailbox.Id == action.MailboxId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            Status = await new MailActionDiagnostics(_store, _provider).RecoverAsync(account, mailbox, action.Id, timeout.Token);
            _busyChecks[action.Id] = Status;
            await RefreshBusyActionsAsync();
            await LoadMessagesAsync();
            _ = SyncAsync();
        }
        catch (Exception error) { _busyChecks[action.Id] = error.Message; await RefreshBusyActionsAsync(); }
    }

    public AsyncCommand<MailAction> RetryBusyActionCommand { get; }
    public AsyncCommand<MailAction> CheckBusyActionCommand { get; }

    private async Task RetryBusyActionAsync(MailAction action)
    {
        if (_store is null) return;
        try
        {
            Status = await _store.RetryMailActionAsync(action.Id)
                ? "Retry queued. Previous failure history is kept."
                : "Cannot retry yet. Resolve any earlier action for this message first.";
            await RefreshBusyActionsAsync();
            _ = SyncAsync();
        }
        catch (Exception error) { Error = error.Message; }
    }

    private async Task CheckBusyActionAsync(MailAction action)
    {
        if (_store is null || _provider is null) return;
        _busyChecks[action.Id] = "Checking server status…";
        await RefreshBusyActionsAsync();
        try
        {
            var account = Accounts.Single(account => account.AccountId == action.AccountId);
            var mailbox = Mailboxes.Single(mailbox => mailbox.Id == action.MailboxId);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await new MailActionDiagnostics(_store, _provider).CheckAsync(account, mailbox, action.Id, timeout.Token);
            _busyChecks[action.Id] = result;
        }
        catch (Exception error) { _busyChecks[action.Id] = "Status check failed: " + error.Message; }
        await RefreshBusyActionsAsync();
    }

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
        var actions = await _store.GetMailActionsAsync();
        foreach (var id in _busyChecks.Keys.Except(actions.Select(action => action.Id)).ToArray()) _busyChecks.Remove(id);
        CollectionUpdates.Reconcile(BusyActions, actions.Select(action => action with { StatusCheckDetails = _busyChecks.GetValueOrDefault(action.Id) }).ToArray(), static action => action.Id);
        RaiseDraftState();
        MailActionStateChanged();
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
        var queue = new Queue<MailAction>(batch.Where(static action => action.Kind != MailActionKind.Send));
        while (queue.TryDequeue(out var pending))
        {
            if (blocked.Contains((pending.MailboxId, pending.ItemId))) continue;
            var account = Accounts.FirstOrDefault(account => account.AccountId == pending.AccountId);
            var mailbox = Mailboxes.FirstOrDefault(mailbox => mailbox.Id == pending.MailboxId && mailbox.AccountId == pending.AccountId);
            if (account is null || mailbox is null) continue;
            // Also recover legacy paused items once, without requiring a manual Retry.
            if (pending.CanAutomaticallyRecover)
            {
                using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await new MailActionDiagnostics(_store, _provider).TryAutomaticRecoveryAsync(account, mailbox, pending.Id, recoveryTimeout.Token);
                await RefreshBusyActionsAsync();
            }
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
                else if (action.Kind == MailActionKind.UpdateState)
                {
                    if (action.ReadValue is { } read) await _provider.MarkReadAsync(account, mailbox, action.ProviderId!, read, timeout.Token);
                    if (action.FlagValue is { } flagged) await _provider.SetFlaggedAsync(account, mailbox, action.ProviderId!, flagged, timeout.Token);
                    await _store.CompleteMessageStateAsync(action, timeout.Token);
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
                var failed = await _store.GetMailActionAsync(action.Id);
                if (failed is { CanAutomaticallyRecover: true })
                {
                    using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    if (await new MailActionDiagnostics(_store, _provider).TryAutomaticRecoveryAsync(account, mailbox, action.Id, recoveryTimeout.Token))
                    {
                        blocked.Remove((action.MailboxId, action.ItemId));
                        if (await _store.GetMailActionAsync(action.Id) is { Accepted: false } repaired) queue.Enqueue(repaired);
                    }
                }
                // A failed action remains pending at its intended destination. Do not
                // reinsert it in the source list; Busy provides failure/recovery details.
            }
            await RefreshBusyActionsAsync();
        }
    }
}
