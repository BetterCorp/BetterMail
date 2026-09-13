using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<string> _deletingDrafts = new(StringComparer.Ordinal);
    public bool IsDraftDeletionPending(LocalDraft draft) => _deletingDrafts.Contains(draft.Id) ||
        BusyActions.Any(action => action.ItemId == draft.Id && action.Kind is MailActionKind.DeleteDraft or MailActionKind.Send);
    internal async Task DeleteDraftQuickAsync(LocalDraft draft)
    {
        if (_store is null || draft.IsQueued || !_deletingDrafts.Add(draft.Id)) return;
        MailActionStateChanged();
        var feedback = Task.Delay(350);
        try
        {
            var current = await _store.GetLocalDraftAsync(draft.Id);
            if (current is null || current.IsQueued) return;
            await _store.QueueDraftDeletionAsync(current);
            // A refresh started before this commit must not resurrect its stale snapshot.
            Interlocked.Increment(ref _draftRefreshVersion);
            var action = (await _store.GetMailActionsAsync()).FirstOrDefault(item => item.Kind == MailActionKind.DeleteDraft && item.ItemId == current.Id);
            if (action is not null) ShowQueuedAction(action);
            await feedback;
            foreach (var item in Drafts.Where(item => item.Id == draft.Id).ToArray()) Drafts.Remove(item);
            RebuildVisibleDrafts();
            RaiseDraftState();
            _ = SyncAsync();
        }
        catch (Exception error) { Error = error.Message; }
        finally { _deletingDrafts.Remove(draft.Id); MailActionStateChanged(); }
    }

    private readonly Dictionary<string, int> _visualMailActions = new(StringComparer.Ordinal);
    private int _mailActionVersion;
    public int MailActionVersion => _mailActionVersion;
    public bool IsMessageActionPending(MailMessage message) => _visualMailActions.ContainsKey(MessageKey(message)) ||
        BusyActions.Any(action => action.Kind is MailActionKind.Move or MailActionKind.UpdateState && ActionMatches(action, message));
    private static bool ActionMatches(MailAction action, MailMessage message) => action.MailboxId == message.MailboxId &&
        (action.ProviderId == message.ProviderId || action.ItemId == message.ProviderId || (action.PreviousProviderIds ?? []).Contains(message.ProviderId));
    private bool _busyOutcomeInitialized;
    internal void InitializeBusyOutcome(IReadOnlyList<MailAction> actions)
    {
        if (_busyOutcomeInitialized) return;
        _busyOutcomeInitialized = true;
        var failed = actions.Where(action => action.Error is not null || action.IsRetryPaused || action.NeedsSendReview).ToArray();
        if (failed.Length == 0) return;
        _failedBusyAtPreviousEnd = failed.Select(action => action.Id).ToHashSet();
        _completedSyncSeverity = failed.Any(action => action.IsRetryPaused || action.NeedsSendReview) ? 2 : 1;
        _syncSeverity = _completedSyncSeverity;
        MailActionStateChanged();
    }

    private int _syncSeverity;
    private int _completedSyncSeverity;
    private bool _workspaceWarning;
    private HashSet<string> _failedBusyAtPreviousEnd = [];
    public int SyncSeverity => Math.Max(_syncSeverity, _workspaceWarning ? 1 : 0);
    public int BusySeverity => _completedSyncSeverity;
    public bool SyncIsWarning => SyncSeverity == 1;
    public bool SyncNeedsAttention => SyncSeverity == 2;
    public bool SyncIsInformation => SyncSeverity == 0 && HasOutbox;
    public bool BusyIsWarning => BusySeverity == 1;
    public bool BusyNeedsAttention => BusySeverity == 2;

    internal void BeginSyncOutcome()
    {
        _workspaceWarning = false;
        // Red is latched until a completed sync empties Busy. Orange survives the retry run.
        _syncSeverity = _completedSyncSeverity == 2 ? 2 : HasOutbox ? _completedSyncSeverity : 0;
        MailActionStateChanged();
    }

    internal void RecordSyncOutcome(bool failed, bool workspace = false)
    {
        if (workspace)
        {
            // Background workspace failures warn, but do not turn normal syncing red.
            _workspaceWarning = failed;
            if (!IsSyncing) _completedSyncSeverity = Math.Max(_syncSeverity, failed ? 1 : 0);
        }
        else
        {
            var failedBusy = BusyActions.Where(action => action.Error is not null || action.IsRetryPaused || action.NeedsSendReview)
                .Select(action => action.Id).ToHashSet();
            var stillStuck = failedBusy.Overlaps(_failedBusyAtPreviousEnd);
            _completedSyncSeverity = HasOutbox && (_completedSyncSeverity == 2 || stillStuck) ? 2
                : failed || failedBusy.Count > 0 || _workspaceWarning ? 1 : 0;
            _failedBusyAtPreviousEnd = failedBusy;
            _syncSeverity = _completedSyncSeverity;
        }
        MailActionStateChanged();
    }
    public bool HasSyncIssues => SyncIssueCount > 0;
    private void MailActionStateChanged()
    {
        if (IsSyncing && _syncSeverity == 0 && BusyActions.Any(action => action.Error is not null || action.NeedsSendReview || action.IsRetryPaused))
            _syncSeverity = 1;
        RaisePropertyChanged(nameof(SyncSeverity));
        RaisePropertyChanged(nameof(SyncIsWarning));
        RaisePropertyChanged(nameof(SyncNeedsAttention));
        RaisePropertyChanged(nameof(SyncIsInformation));
        RaisePropertyChanged(nameof(BusySeverity));
        RaisePropertyChanged(nameof(BusyIsWarning));
        RaisePropertyChanged(nameof(BusyNeedsAttention));
        _mailActionVersion++;
        RaisePropertyChanged(nameof(MailActionVersion));
    }
    private void BeginMessageFeedback(IEnumerable<MailMessage> messages)
    {
        foreach (var message in messages)
        {
            var key = MessageKey(message);
            _visualMailActions[key] = _visualMailActions.GetValueOrDefault(key) + 1;
        }
        MailActionStateChanged();
    }
    private void EndMessageFeedback(IEnumerable<MailMessage> messages)
    {
        foreach (var message in messages)
        {
            var key = MessageKey(message);
            if (_visualMailActions.GetValueOrDefault(key) <= 1) _visualMailActions.Remove(key);
            else _visualMailActions[key]--;
        }
        MailActionStateChanged();
    }
    private void ShowQueuedAction(MailAction action)
    {
        var existing = BusyActions.FirstOrDefault(item => item.Id == action.Id);
        if (existing is null) BusyActions.Add(action);
        else BusyActions[BusyActions.IndexOf(existing)] = action;
        RaiseDraftState();
        MailActionStateChanged();
    }

    private async Task QueueMessageStateChangesAsync(IReadOnlyList<MailMessage> messages,
        bool? read = null, bool? flagged = null, bool? pinned = null, Action? accepted = null)
    {
        if (_store is null || messages.Count == 0) return;
        BeginMessageFeedback(messages);
        var feedback = Task.Delay(350);
        try
        {
            foreach (var message in messages)
            {
                if (!TryGetMessageContext(message, out var account, out _)) continue;
                var action = await _store.QueueMessageStateAsync(account, message, read, flagged, pinned);
                ShowQueuedAction(action);
                ApplyMessageStateUpdate(message, isRead: read, isFlagged: flagged, isPinned: pinned);
                accepted?.Invoke();
            }
            Status = "Message update queued";
            _ = SyncAsync();
            await feedback;
        }
        catch (Exception exception) { Error = exception.Message; Status = "Could not queue message update"; }
        finally { EndMessageFeedback(messages); }
    }
}
