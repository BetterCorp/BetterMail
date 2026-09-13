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
    private int _mailSyncFailureStreak;
    private int _workspaceFailureStreak;
    public int SyncSeverity => Math.Max(BusySeverity, Math.Min(2, Math.Max(_mailSyncFailureStreak, _workspaceFailureStreak)));
    private static string BadgeBackground(int severity) => severity switch { 2 => "#24D13438", 1 => "#24D98200", _ => "#208A9AA9" };
    private static string BadgeForeground(int severity) => severity switch { 2 => "#E45155", 1 => "#D98200", _ => "#8297AD" };
    public string SyncBadgeBackground => BadgeBackground(SyncSeverity);
    public string SyncBadgeForeground => BadgeForeground(SyncSeverity);
    internal void RecordSyncOutcome(bool failed, bool workspace = false)
    {
        if (workspace) _workspaceFailureStreak = failed ? Math.Min(2, _workspaceFailureStreak + 1) : 0;
        else _mailSyncFailureStreak = failed ? Math.Min(2, _mailSyncFailureStreak + 1) : 0;
        MailActionStateChanged();
    }
    public bool HasSyncIssues => SyncIssueCount > 0;
    public int BusySeverity => BusyActions.Any(action => action.FailureCount >= 2 || action.NeedsSendReview) ? 2
        : BusyActions.Any(action => action.FailureCount > 0 || action.Error is not null) ? 1 : 0;
    public string BusyBadgeBackground => BusySeverity switch { 2 => "#24D13438", 1 => "#24D98200", _ => "#208A9AA9" };
    public string BusyBadgeForeground => BusySeverity switch { 2 => "#E45155", 1 => "#D98200", _ => "#8297AD" };
    private void MailActionStateChanged()
    {
        RaisePropertyChanged(nameof(SyncSeverity));
        RaisePropertyChanged(nameof(SyncBadgeBackground));
        RaisePropertyChanged(nameof(SyncBadgeForeground));
        RaisePropertyChanged(nameof(BusySeverity));
        RaisePropertyChanged(nameof(BusyBadgeBackground));
        RaisePropertyChanged(nameof(BusyBadgeForeground));
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
