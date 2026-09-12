using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    private readonly Dictionary<string, int> _visualMailActions = new(StringComparer.Ordinal);
    private int _mailActionVersion;
    public int MailActionVersion => _mailActionVersion;
    public bool IsMessageActionPending(MailMessage message) => _visualMailActions.ContainsKey(MessageKey(message)) ||
        BusyActions.Any(action => action.Kind is MailActionKind.Move or MailActionKind.UpdateState && ActionMatches(action, message));
    private static bool ActionMatches(MailAction action, MailMessage message) => action.MailboxId == message.MailboxId &&
        (action.ProviderId == message.ProviderId || action.ItemId == message.ProviderId || (action.PreviousProviderIds ?? []).Contains(message.ProviderId));
    private void MailActionStateChanged()
    {
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
        bool? read = null, bool? flagged = null, bool? pinned = null)
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
            }
            Status = "Message update queued";
            _ = SyncAsync();
            await feedback;
        }
        catch (Exception exception) { Error = exception.Message; Status = "Could not queue message update"; }
        finally { EndMessageFeedback(messages); }
    }
}
