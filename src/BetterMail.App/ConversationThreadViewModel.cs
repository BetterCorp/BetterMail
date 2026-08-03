using System.Collections.ObjectModel;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public enum ConversationAction
{
    Reply,
    ReplyAll,
    Forward
}

public sealed record ConversationActionRequest(ConversationAction Action, MailMessage Message);

public sealed class ConversationThreadViewModel : ViewModelBase
{
    private readonly MailContentRenderer _renderer;
    private readonly Action<ConversationActionRequest>? _action;
    private readonly Action<MailMessage>? _selectionChanged;
    private readonly Func<MailMessage, Task<MailMessage?>>? _loadMessage;
    private readonly Func<MailMessage, string> _location;
    private readonly Func<LocalDraft, Task>? _openDraft;
    private IReadOnlyList<LocalDraft> _drafts = [];
    private ConversationThreadItem? _selectedThread;
    private ConversationMessageItem? _selectedMessage;
    private readonly Dictionary<string, ConversationMessageItem> _messageCache = new(StringComparer.Ordinal);

    public ConversationThreadViewModel(
        MailContentRenderer? renderer = null,
        Action<ConversationActionRequest>? action = null,
        Action<MailMessage>? selectionChanged = null,
        Func<MailMessage, Task<MailMessage?>>? loadMessage = null,
        Func<MailMessage, string>? location = null,
        Func<LocalDraft, Task>? openDraft = null)
    {
        _renderer = renderer ?? new MailContentRenderer();
        _action = action;
        _selectionChanged = selectionChanged;
        _loadMessage = loadMessage;
        _location = location ?? (message => message.FolderId);
        _openDraft = openDraft;
        ToggleMessageCommand = new AsyncCommand<ConversationMessageItem>(ToggleMessageAsync);
        AllowRemoteContentCommand = new AsyncCommand<ConversationMessageItem>(AllowRemoteContentAsync);
        SelectMessageCommand = new AsyncCommand<ConversationMessageItem>(SelectMessageAsync);
        ReplyCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Reply), CanRunAction);
        ReplyAllCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.ReplyAll), CanRunAction);
        ForwardCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Forward), CanRunAction);
        OpenDraftCommand = new AsyncCommand<LocalDraft>(draft => _openDraft?.Invoke(draft) ?? Task.CompletedTask);
    }

    public ObservableCollection<ConversationThreadItem> Threads { get; } = [];
    public ObservableCollection<LocalDraft> Drafts { get; } = [];
    public ICommand ToggleMessageCommand { get; }
    public ICommand AllowRemoteContentCommand { get; }
    public ICommand SelectMessageCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand ReplyAllCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand OpenDraftCommand { get; }

    public ConversationThreadItem? SelectedThread
    {
        get => _selectedThread;
        private set
        {
            if (SetProperty(ref _selectedThread, value))
            {
                RaisePropertyChanged(nameof(HasThread));
                RaisePropertyChanged(nameof(HasNoThread));
                RefreshDrafts();
            }
        }
    }

    public ConversationMessageItem? SelectedMessage
    {
        get => _selectedMessage;
        private set
        {
            if (SetProperty(ref _selectedMessage, value))
            {
                RefreshActionCommands();
            }
        }
    }

    public bool HasThread => SelectedThread is not null;
    public bool HasNoThread => !HasThread;
    public bool HasDrafts => Drafts.Count > 0;
    public string ThreadItemCountText => $"{(SelectedThread?.Messages.Count ?? 0) + Drafts.Count} items";

    public void ReconcileDrafts(IEnumerable<LocalDraft> drafts)
    {
        _drafts = drafts.ToArray();
        RefreshDrafts();
    }

    private void RefreshDrafts()
    {
        Replace(Drafts, _drafts
            .Where(draft => draft.ConversationIdentity == SelectedThread?.Identity)
            .OrderBy(static draft => draft.UpdatedAt)
            .ToArray());
        RaisePropertyChanged(nameof(HasDrafts));
        RaisePropertyChanged(nameof(ThreadItemCountText));
    }

    public void RefreshTheme()
    {
        foreach (var message in _messageCache.Values)
        {
            message.RefreshTheme();
        }
    }

    public void Reconcile(IEnumerable<MailMessage> messages, MailMessage? selectedMessage = null)
    {
        var projections = ConversationThread.Project(messages);
        var activeMessages = projections
            .SelectMany(static thread => thread.Messages)
            .Select(static item => ConversationThread.MessageIdentity(item.Message))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var identity in _messageCache.Keys.Where(identity => !activeMessages.Contains(identity)).ToArray())
        {
            _messageCache.Remove(identity);
        }
        var existing = Threads.ToDictionary(thread => thread.Identity, StringComparer.Ordinal);
        var reconciled = new List<ConversationThreadItem>(projections.Count);
        foreach (var projection in projections)
        {
            if (!existing.TryGetValue(projection.Identity, out var thread))
            {
                thread = new ConversationThreadItem(projection.Identity, GetMessageItem);
            }
            thread.Reconcile(projection);
            reconciled.Add(thread);
        }
        Replace(Threads, reconciled);

        var selectedIdentity = selectedMessage is null
            ? SelectedMessage?.Identity
            : ConversationThread.MessageIdentity(selectedMessage);
        var selectedThreadIdentity = selectedMessage is null
            ? SelectedThread?.Identity
            : ConversationThread.ThreadIdentity(selectedMessage);
        SelectedThread = Threads.FirstOrDefault(thread => thread.Identity == selectedThreadIdentity)
            ?? Threads.FirstOrDefault();
        var selected = SelectedThread?.Messages.FirstOrDefault(message => message.Identity == selectedIdentity)
            ?? SelectedThread?.Messages.LastOrDefault();
        Select(selected);
        RaisePropertyChanged(nameof(ThreadItemCountText));
    }

    private ConversationMessageItem GetMessageItem(string identity, MailMessage message)
    {
        if (_messageCache.TryGetValue(identity, out var cached))
        {
            cached.Update(message);
            return cached;
        }

        var item = new ConversationMessageItem(identity, message, _renderer, _location);
        _messageCache.Add(identity, item);
        return item;
    }

    public void SelectMessage(MailMessage? message)
    {
        if (message is null)
        {
            Select(null);
            return;
        }
        SelectedThread = Threads.FirstOrDefault(thread =>
            thread.Identity == ConversationThread.ThreadIdentity(message));
        Select(SelectedThread?.Messages.FirstOrDefault(item =>
            item.Identity == ConversationThread.MessageIdentity(message)));
    }

    public void SetAttachments(MailMessage message, IReadOnlyList<MailAttachment> attachments)
    {
        var identity = ConversationThread.MessageIdentity(message);
        Threads.SelectMany(static thread => thread.Messages)
            .FirstOrDefault(item => item.Identity == identity)
            ?.SetAttachments(attachments);
    }

    private async Task SelectMessageAsync(ConversationMessageItem item)
    {
        Select(item);
        if (_loadMessage is null || item.Message.Body is not null)
        {
            return;
        }
        var hydrated = await _loadMessage(item.Message);
        if (hydrated is null || SelectedMessage?.Identity != item.Identity || SelectedThread is null)
        {
            return;
        }
        Reconcile(
            SelectedThread.Messages.Select(current => current.Identity == item.Identity ? hydrated : current.Message),
            hydrated);
    }

    private void Select(ConversationMessageItem? item)
    {
        var changed = !ReferenceEquals(SelectedMessage, item);
        SelectedMessage = item;
        if (SelectedThread is not null)
        {
            SelectedThread.SetSelection(item);
        }
        if (changed && item is not null)
        {
            _selectionChanged?.Invoke(item.Message);
        }
    }

    private Task ToggleMessageAsync(ConversationMessageItem item)
    {
        // Thread headers already reference locally cached messages. Selecting one must
        // never reselect the mail-list row or start provider work.
        return SelectMessageAsync(item);
    }

    private static Task AllowRemoteContentAsync(ConversationMessageItem item)
    {
        item.AllowRemoteContent();
        return Task.CompletedTask;
    }

    private bool CanRunAction() => SelectedMessage is not null && _action is not null;

    private Task RunActionAsync(ConversationAction action)
    {
        if (SelectedMessage is not null)
        {
            _action?.Invoke(new(action, SelectedMessage.Message));
        }
        return Task.CompletedTask;
    }

    private void RefreshActionCommands()
    {
        ((AsyncCommand)ReplyCommand).Refresh();
        ((AsyncCommand)ReplyAllCommand).Refresh();
        ((AsyncCommand)ForwardCommand).Refresh();
    }

    internal static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        if (target.Count == values.Count && target.SequenceEqual(values))
        {
            return;
        }
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}

public sealed class ConversationThreadItem(
    string identity,
    Func<string, MailMessage, ConversationMessageItem> getMessageItem) : ViewModelBase
{
    private string _subject = "(no subject)";
    private ConversationMessageItem? _selected;

    public string Identity { get; } = identity;
    public ObservableCollection<ConversationMessageItem> Messages { get; } = [];
    public string Subject { get => _subject; private set => SetProperty(ref _subject, value); }
    public ConversationMessageItem? Newest => Messages.LastOrDefault();

    public void Reconcile(ConversationThread projection)
    {
        Subject = projection.Subject;
        var existing = Messages.ToDictionary(message => message.Identity, StringComparer.Ordinal);
        var reconciled = new List<ConversationMessageItem>(projection.Messages.Count);
        foreach (var projected in projection.Messages)
        {
            if (!existing.TryGetValue(projected.Identity, out var item))
            {
                item = getMessageItem(projected.Identity, projected.Message);
            }
            else
            {
                item.Update(projected.Message);
            }
            reconciled.Add(item);
        }
        ConversationThreadViewModel.Replace(Messages, reconciled);
        _selected = Messages.FirstOrDefault(message => message.Identity == _selected?.Identity);
        SetSelection(_selected ?? Newest);
        RaisePropertyChanged(nameof(Newest));
    }

    public void SetSelection(ConversationMessageItem? selected)
    {
        _selected = selected;
        foreach (var message in Messages)
        {
            message.IsSelected = ReferenceEquals(message, selected);
        }
        if (selected is not null)
        {
            selected.IsExpanded = true;
        }
        if (Newest is not null)
        {
            Newest.IsExpanded = true;
        }
    }
}

public sealed class ConversationMessageItem : ViewModelBase
{
    private readonly MailContentRenderer _renderer;
    private readonly Func<MailMessage, string> _location;
    private MailMessage _message;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _allowRemoteContent;
    private IReadOnlyList<MailAttachment> _attachments = [];
    private string _bodyHtml;
    private bool _hasBlockedRemoteContent;
    private bool _renderRequested;
    private int _renderVersion;

    public ConversationMessageItem(
        string identity,
        MailMessage message,
        MailContentRenderer renderer,
        Func<MailMessage, string> location)
    {
        Identity = identity;
        _message = message;
        _renderer = renderer;
        _location = location;
        _bodyHtml = renderer.RenderDocument("Loading message…", false);
    }

    public string Identity { get; }
    public MailMessage Message => _message;
    public string Sender => _message.SenderDisplayName;
    public string SenderAddress => _message.From.Address;
    public string Recipients => $"To: {string.Join(", ", _message.To.Select(address => address.ToString()))}";
    public string ReceivedText => _message.ReceivedAt.ToLocalTime().ToString("ddd, MMM d, yyyy HH:mm");
    public string Location => _location(_message);
    public string Preview => _message.Preview;
    public string BodyHtml
    {
        get
        {
            EnsureRendered();
            return _bodyHtml;
        }
    }
    public bool HasBlockedRemoteContent
    {
        get
        {
            EnsureRendered();
            return _hasBlockedRemoteContent;
        }
    }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public void Update(MailMessage message)
    {
        if (_message.Body is not null && message.Body is null)
        {
            message = message with { Body = _message.Body };
        }
        if (_message.HasSameContent(message))
        {
            return;
        }
        var bodyChanged = !string.Equals(_message.Body, message.Body, StringComparison.Ordinal) ||
            (_message.Body is null && !string.Equals(_message.Preview, message.Preview, StringComparison.Ordinal)) ||
            _message.IsHtml != message.IsHtml;
        _message = message;
        if (bodyChanged)
        {
            _allowRemoteContent = false;
            _renderRequested = false;
            _renderVersion++;
        }
        RaisePropertyChanged(nameof(Message));
        RaisePropertyChanged(nameof(Sender));
        RaisePropertyChanged(nameof(SenderAddress));
        RaisePropertyChanged(nameof(Recipients));
        RaisePropertyChanged(nameof(ReceivedText));
        RaisePropertyChanged(nameof(Location));
        RaisePropertyChanged(nameof(Preview));
        if (bodyChanged)
        {
            RaisePropertyChanged(nameof(BodyHtml));
            RaisePropertyChanged(nameof(HasBlockedRemoteContent));
        }
    }

    public void AllowRemoteContent()
    {
        _allowRemoteContent = true;
        _hasBlockedRemoteContent = false;
        _renderRequested = false;
        RaisePropertyChanged(nameof(HasBlockedRemoteContent));
        RenderBody();
    }

    public void SetAttachments(IReadOnlyList<MailAttachment> attachments)
    {
        if (_attachments.SequenceEqual(attachments))
        {
            return;
        }
        _attachments = attachments;
        if (_renderRequested)
        {
            RenderBody();
        }
    }

    public void RefreshTheme()
    {
        _renderRequested = false;
        _bodyHtml = _renderer.RenderDocument("Loading message…", false);
        RaisePropertyChanged(nameof(BodyHtml));
        RaisePropertyChanged(nameof(HasBlockedRemoteContent));
    }

    private void EnsureRendered()
    {
        if (!_renderRequested)
        {
            RenderBody();
        }
    }

    private void RenderBody()
    {
        _renderRequested = true;
        _ = RenderBodyAsync(++_renderVersion, _message, _attachments, _allowRemoteContent);
    }

    private async Task RenderBodyAsync(
        int version,
        MailMessage message,
        IReadOnlyList<MailAttachment> attachments,
        bool allowRemoteContent)
    {
        (string BodyHtml, bool HasBlockedRemoteContent) rendered;
        try
        {
            rendered = await Task.Run(() => Render(message, attachments, allowRemoteContent));
        }
        catch
        {
            rendered = (_renderer.RenderDocument(message.Preview, false), false);
        }
        ApplyRenderedBody(version, rendered);
    }

    private (string BodyHtml, bool HasBlockedRemoteContent) Render(
        MailMessage message,
        IReadOnlyList<MailAttachment> attachments,
        bool allowRemoteContent)
    {
        var hasHtmlBody = message.IsHtml && message.Body is not null;
        return (
            _renderer.RenderDocument(message.Body ?? message.Preview, hasHtmlBody, attachments, allowRemoteContent),
            !allowRemoteContent && _renderer.HasRemoteImages(message.Body, hasHtmlBody));
    }

    private void ApplyRenderedBody(int version, (string BodyHtml, bool HasBlockedRemoteContent) rendered)
    {
        if (version != _renderVersion)
        {
            return;
        }
        _bodyHtml = rendered.BodyHtml;
        _hasBlockedRemoteContent = rendered.HasBlockedRemoteContent;
        RaisePropertyChanged(nameof(BodyHtml));
        RaisePropertyChanged(nameof(HasBlockedRemoteContent));
    }
}
