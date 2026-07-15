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
    private const int RenderedMessageCacheLimit = 128;
    private readonly MailContentRenderer _renderer;
    private readonly Action<ConversationActionRequest>? _action;
    private readonly Action<MailMessage>? _selectionChanged;
    private ConversationThreadItem? _selectedThread;
    private ConversationMessageItem? _selectedMessage;
    private readonly Dictionary<string, ConversationMessageItem> _messageCache = new(StringComparer.Ordinal);

    public ConversationThreadViewModel(
        MailContentRenderer? renderer = null,
        Action<ConversationActionRequest>? action = null,
        Action<MailMessage>? selectionChanged = null)
    {
        _renderer = renderer ?? new MailContentRenderer();
        _action = action;
        _selectionChanged = selectionChanged;
        ToggleMessageCommand = new AsyncCommand<ConversationMessageItem>(ToggleMessageAsync);
        AllowRemoteContentCommand = new AsyncCommand<ConversationMessageItem>(AllowRemoteContentAsync);
        SelectMessageCommand = new AsyncCommand<ConversationMessageItem>(SelectMessageAsync);
        ReplyCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Reply), CanRunAction);
        ReplyAllCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.ReplyAll), CanRunAction);
        ForwardCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Forward), CanRunAction);
    }

    public ObservableCollection<ConversationThreadItem> Threads { get; } = [];
    public ICommand ToggleMessageCommand { get; }
    public ICommand AllowRemoteContentCommand { get; }
    public ICommand SelectMessageCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand ReplyAllCommand { get; }
    public ICommand ForwardCommand { get; }

    public ConversationThreadItem? SelectedThread
    {
        get => _selectedThread;
        private set
        {
            if (SetProperty(ref _selectedThread, value))
            {
                RaisePropertyChanged(nameof(HasThread));
                RaisePropertyChanged(nameof(HasNoThread));
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
    }

    private ConversationMessageItem GetMessageItem(string identity, MailMessage message)
    {
        if (_messageCache.TryGetValue(identity, out var cached))
        {
            cached.Update(message);
            return cached;
        }

        // ponytail: bounded click-history cache; use a real LRU only if 128 rendered messages proves insufficient.
        if (_messageCache.Count >= RenderedMessageCacheLimit)
        {
            _messageCache.Remove(_messageCache.Keys.First());
        }
        var item = new ConversationMessageItem(identity, message, _renderer);
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

    private Task SelectMessageAsync(ConversationMessageItem item)
    {
        Select(item);
        return Task.CompletedTask;
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
        Select(item);
        return Task.CompletedTask;
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
    private MailMessage _message;
    private bool _isExpanded;
    private bool _isSelected;
    private bool _allowRemoteContent;
    private IReadOnlyList<MailAttachment> _attachments = [];
    private Uri _bodyUri;
    private bool _hasBlockedRemoteContent;
    private bool _renderRequested;
    private int _renderVersion;

    public ConversationMessageItem(string identity, MailMessage message, MailContentRenderer renderer)
    {
        Identity = identity;
        _message = message;
        _renderer = renderer;
        _bodyUri = renderer.Render("Loading message…", false);
    }

    public string Identity { get; }
    public MailMessage Message => _message;
    public string Sender => _message.SenderDisplayName;
    public string SenderAddress => _message.From.Address;
    public string Recipients => $"To: {string.Join(", ", _message.To.Select(address => address.ToString()))}";
    public string ReceivedText => _message.ReceivedAt.ToLocalTime().ToString("ddd, MMM d, yyyy HH:mm");
    public string Preview => _message.Preview;
    public Uri BodyUri
    {
        get
        {
            EnsureRendered();
            return _bodyUri;
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
        var bodyChanged = !string.Equals(_message.Body, message.Body, StringComparison.Ordinal) ||
            (_message.Body is null && !string.Equals(_message.Preview, message.Preview, StringComparison.Ordinal)) ||
            _message.IsHtml != message.IsHtml;
        _message = message;
        if (bodyChanged)
        {
            _allowRemoteContent = false;
            _attachments = [];
            _renderRequested = false;
            _bodyUri = _renderer.Render("Loading message…", false);
        }
        RaisePropertyChanged(nameof(Message));
        RaisePropertyChanged(nameof(Sender));
        RaisePropertyChanged(nameof(SenderAddress));
        RaisePropertyChanged(nameof(Recipients));
        RaisePropertyChanged(nameof(ReceivedText));
        RaisePropertyChanged(nameof(Preview));
        if (bodyChanged)
        {
            RaisePropertyChanged(nameof(BodyUri));
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
        _attachments = attachments;
        if (_renderRequested)
        {
            RenderBody();
        }
    }

    public void RefreshTheme()
    {
        _renderRequested = false;
        _bodyUri = _renderer.Render("Loading message…", false);
        RaisePropertyChanged(nameof(BodyUri));
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
        (Uri BodyUri, bool HasBlockedRemoteContent) rendered;
        try
        {
            rendered = await Task.Run(() => Render(message, attachments, allowRemoteContent));
        }
        catch
        {
            rendered = (_renderer.Render(message.Preview, false), false);
        }
        ApplyRenderedBody(version, rendered);
    }

    private (Uri BodyUri, bool HasBlockedRemoteContent) Render(
        MailMessage message,
        IReadOnlyList<MailAttachment> attachments,
        bool allowRemoteContent)
    {
        var hasHtmlBody = message.IsHtml && message.Body is not null;
        return (
            _renderer.Render(message.Body ?? message.Preview, hasHtmlBody, attachments, allowRemoteContent),
            !allowRemoteContent && _renderer.HasRemoteImages(message.Body, hasHtmlBody));
    }

    private void ApplyRenderedBody(int version, (Uri BodyUri, bool HasBlockedRemoteContent) rendered)
    {
        if (version != _renderVersion)
        {
            return;
        }
        _bodyUri = rendered.BodyUri;
        _hasBlockedRemoteContent = rendered.HasBlockedRemoteContent;
        RaisePropertyChanged(nameof(BodyUri));
        RaisePropertyChanged(nameof(HasBlockedRemoteContent));
    }
}
