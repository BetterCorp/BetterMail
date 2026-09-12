using System.Collections.ObjectModel;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public enum ConversationAction
{
    Reply,
    ReplyAll,
    Forward,
    Archive,
    Delete,
    Junk,
    NotJunk,
    ToggleRead,
    ToggleFlag,
    TogglePin,
    ViewHeaders,
    Move
}

public sealed record ConversationActionRequest(
    ConversationAction Action,
    MailMessage Message,
    MailFolderItem? Destination = null);

public sealed class ConversationThreadViewModel : ViewModelBase
{
    private readonly MailContentRenderer _renderer;
    private readonly Func<ConversationActionRequest, Task>? _action;
    private readonly Action<MailMessage>? _selectionChanged;
    private readonly Func<MailMessage, Task<MailMessage?>>? _loadMessage;
    private readonly Func<MailMessage, string> _location;
    private readonly Func<LocalDraft, Task>? _openDraft;
    private readonly Func<MailMessage, IReadOnlyList<MailFolderItem>>? _moveFolders;
    private readonly Func<MailMessage, Task<IReadOnlyList<MailAttachment>>>? _loadAttachments;
    private readonly Func<MailMessage, MailAttachment, Task>? _openAttachment;
    private readonly Func<MailMessage, IReadOnlyList<MailAttachment>, Task>? _saveAttachments;
    private IReadOnlyList<LocalDraft> _drafts = [];
    private ConversationThreadItem? _selectedThread;
    private ConversationMessageItem? _selectedMessage;
    private readonly Dictionary<string, ConversationMessageItem> _messageCache = new(StringComparer.Ordinal);
    private bool _isActionRunning;
    private int _runningActionCount;
    private bool _isLoadingAttachments;
    private int _attachmentVersion;

    public ConversationThreadViewModel(
        MailContentRenderer? renderer = null,
        Func<ConversationActionRequest, Task>? action = null,
        Action<MailMessage>? selectionChanged = null,
        Func<MailMessage, Task<MailMessage?>>? loadMessage = null,
        Func<MailMessage, string>? location = null,
        Func<LocalDraft, Task>? openDraft = null,
        Func<MailMessage, IReadOnlyList<MailFolderItem>>? moveFolders = null,
        bool showActions = false,
        Func<MailMessage, Task<IReadOnlyList<MailAttachment>>>? loadAttachments = null,
        Func<MailMessage, MailAttachment, Task>? openAttachment = null,
        Func<MailMessage, IReadOnlyList<MailAttachment>, Task>? saveAttachments = null)
    {
        _renderer = renderer ?? new MailContentRenderer();
        _action = action;
        _selectionChanged = selectionChanged;
        _loadMessage = loadMessage;
        _location = location ?? (message => message.FolderId);
        _openDraft = openDraft;
        _moveFolders = moveFolders;
        _loadAttachments = loadAttachments;
        _openAttachment = openAttachment;
        _saveAttachments = saveAttachments;
        SaveAttachmentsCommand = new AsyncCommand(() => SelectedMessage is null || _saveAttachments is null
            ? Task.CompletedTask : _saveAttachments(SelectedMessage.Message, Attachments.ToArray()));
        ShowActions = showActions;
        ToggleMessageCommand = new AsyncCommand<ConversationMessageItem>(ToggleMessageAsync);
        AllowRemoteContentCommand = new AsyncCommand<ConversationMessageItem>(AllowRemoteContentAsync);
        SelectMessageCommand = new AsyncCommand<ConversationMessageItem>(SelectMessageAsync);
        ReplyCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Reply), CanRunAction, allowConcurrent: true);
        ReplyAllCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.ReplyAll), CanRunAction, allowConcurrent: true);
        ForwardCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Forward), CanRunAction, allowConcurrent: true);
        ArchiveCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Archive), CanRunAction, allowConcurrent: true);
        DeleteCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Delete), CanRunAction, allowConcurrent: true);
        JunkCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.Junk), CanRunAction, allowConcurrent: true);
        NotJunkCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.NotJunk), CanRunAction, allowConcurrent: true);
        ToggleReadCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.ToggleRead), CanRunAction, allowConcurrent: true);
        ToggleFlagCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.ToggleFlag), CanRunAction, allowConcurrent: true);
        TogglePinCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.TogglePin), CanRunAction, allowConcurrent: true);
        ViewHeadersCommand = new AsyncCommand(() => RunActionAsync(ConversationAction.ViewHeaders), CanRunAction, allowConcurrent: true);
        MoveToFolderCommand = new AsyncCommand<MailFolderItem>(MoveToFolderAsync, CanMoveToFolder, allowConcurrent: true);
        OpenDraftCommand = new AsyncCommand<LocalDraft>(draft => _openDraft?.Invoke(draft) ?? Task.CompletedTask);
        OpenAttachmentCommand = new AsyncCommand<MailAttachment>(OpenAttachmentAsync);
    }

    public ObservableCollection<ConversationThreadItem> Threads { get; } = [];
    public ObservableCollection<LocalDraft> Drafts { get; } = [];
    public ObservableCollection<MailAttachment> Attachments { get; } = [];
    public ICommand ToggleMessageCommand { get; }
    public ICommand AllowRemoteContentCommand { get; }
    public ICommand SelectMessageCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand ReplyAllCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand JunkCommand { get; }
    public ICommand NotJunkCommand { get; }
    public ICommand ToggleReadCommand { get; }
    public ICommand ToggleFlagCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand ViewHeadersCommand { get; }
    public ICommand MoveToFolderCommand { get; }
    public ICommand OpenDraftCommand { get; }
    public ICommand OpenAttachmentCommand { get; }
    public ICommand SaveAttachmentsCommand { get; }
    public bool CanSaveAttachments => _saveAttachments is not null && Attachments.Count > 1;
    public bool ShowActions { get; }
    public bool IsLoadingAttachments
    {
        get => _isLoadingAttachments;
        private set
        {
            if (SetProperty(ref _isLoadingAttachments, value))
            {
                RaisePropertyChanged(nameof(ShowAttachmentArea));
            }
        }
    }
    public bool ShowAttachmentArea => IsLoadingAttachments || Attachments.Count > 0;
    public string AttachmentSummary => Attachments.Count == 1
        ? "1 attachment"
        : $"{Attachments.Count} attachments";
    public bool IsActionRunning
    {
        get => _isActionRunning;
        private set
        {
            if (SetProperty(ref _isActionRunning, value))
            {
                RefreshActionCommands();
            }
        }
    }
    public string ToggleReadText => SelectedMessage?.Message.IsRead == true ? "Mark unread" : "Mark read";
    public string ToggleFlagText => SelectedMessage?.Message.IsFlagged == true ? "Clear flag" : "Flag";
    public string TogglePinText => SelectedMessage?.Message.IsPinned == true ? "Unpin" : "Pin";
    public IReadOnlyList<MailFolderItem> MoveFolders => SelectedMessage is null || _moveFolders is null
        ? []
        : _moveFolders(SelectedMessage.Message);

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
                RaisePropertyChanged(nameof(ToggleReadText));
                RaisePropertyChanged(nameof(ToggleFlagText));
                RaisePropertyChanged(nameof(TogglePinText));
                RaisePropertyChanged(nameof(MoveFolders));
                RefreshActionCommands();
            }
        }
    }

    public bool HasThread => SelectedThread is not null;
    public bool HasNoThread => !HasThread;
    public bool HasDrafts => Drafts.Count > 0;
    public bool ShowThreadList => HasThread;
    public string ThreadItemCountText
    {
        get
        {
            var count = (SelectedThread?.Messages.Count ?? 0) + Drafts.Count;
            return $"{count} {(count == 1 ? "item" : "items")}";
        }
    }

    public void ReconcileDrafts(IEnumerable<LocalDraft> drafts)
    {
        _drafts = drafts.Where(static draft => !draft.HasSyncIssue).ToArray();
        RefreshDrafts();
    }

    private void RefreshDrafts()
    {
        Replace(Drafts, _drafts
            .Where(draft => draft.ConversationIdentity == SelectedThread?.Identity)
            .OrderBy(static draft => draft.UpdatedAt)
            .ToArray());
        RaisePropertyChanged(nameof(HasDrafts));
        RaisePropertyChanged(nameof(ShowThreadList));
        RaisePropertyChanged(nameof(ThreadItemCountText));
    }

    public void RefreshTheme()
    {
        foreach (var message in _messageCache.Values)
        {
            message.RefreshTheme();
        }
    }

    public event Action? Reconciling;
    public event Action? Reconciled;
    public void Reconcile(IEnumerable<MailMessage> messages, MailMessage? selectedMessage = null)
    {
        Reconciling?.Invoke();
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
        CollectionUpdates.Reconcile(Threads, reconciled, static thread => thread.Identity);

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
        RaisePropertyChanged(nameof(ShowThreadList));
        RaisePropertyChanged(nameof(ThreadItemCountText));
        Reconciled?.Invoke();
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
        if (SelectedMessage?.Identity == identity)
        {
            Replace(Attachments, attachments);
            RaisePropertyChanged(nameof(AttachmentSummary));
            RaisePropertyChanged(nameof(ShowAttachmentArea));
            RaisePropertyChanged(nameof(CanSaveAttachments));
        }
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
        if (changed)
        {
            if (item is not null)
            {
                _selectionChanged?.Invoke(item.Message);
            }
            _ = LoadAttachmentsAsync(item);
        }
    }

    private async Task LoadAttachmentsAsync(ConversationMessageItem? item)
    {
        var version = ++_attachmentVersion;
        IsLoadingAttachments = false;
        Replace(Attachments, []);
        RaisePropertyChanged(nameof(AttachmentSummary));
        RaisePropertyChanged(nameof(ShowAttachmentArea));
        RaisePropertyChanged(nameof(CanSaveAttachments));
        if (item is null || _loadAttachments is null || (!item.Message.HasAttachments &&
            item.Message.Body?.Contains("cid:", StringComparison.OrdinalIgnoreCase) != true))
        {
            return;
        }

        IsLoadingAttachments = true;
        try
        {
            var attachments = await _loadAttachments(item.Message);
            if (version != _attachmentVersion)
            {
                return;
            }
            Replace(Attachments, attachments);
            item.SetAttachments(attachments);
            RaisePropertyChanged(nameof(AttachmentSummary));
            RaisePropertyChanged(nameof(ShowAttachmentArea));
            RaisePropertyChanged(nameof(CanSaveAttachments));
        }
        finally
        {
            if (version == _attachmentVersion)
            {
                IsLoadingAttachments = false;
            }
        }
    }

    private Task OpenAttachmentAsync(MailAttachment attachment) =>
        SelectedMessage is null || _openAttachment is null
            ? Task.CompletedTask
            : _openAttachment(SelectedMessage.Message, attachment);

    private Task ToggleMessageAsync(ConversationMessageItem item)
    {
        // Thread selection is independent of the mail-list row.
        return SelectMessageAsync(item);
    }

    private static Task AllowRemoteContentAsync(ConversationMessageItem item)
    {
        item.AllowRemoteContent();
        return Task.CompletedTask;
    }

    private bool CanRunAction() => SelectedMessage is not null && _action is not null;

    private async Task RunActionAsync(ConversationAction action)
    {
        var selected = SelectedMessage;
        if (selected is null || _action is null)
        {
            return;
        }
        _runningActionCount++;
        IsActionRunning = true;
        try
        {
            var original = selected.Message;
            var request = new ConversationActionRequest(action, original);
            await _action!(request);
            if (action == ConversationAction.ToggleRead)
            {
                selected.Update(selected.Message with { IsRead = !original.IsRead });
            }
            else if (action == ConversationAction.ToggleFlag)
            {
                selected.Update(selected.Message with { IsFlagged = !original.IsFlagged });
            }
            else if (action == ConversationAction.TogglePin)
            {
                selected.Update(selected.Message with { IsPinned = !original.IsPinned });
            }
        }
        finally
        {
            IsActionRunning = --_runningActionCount > 0;
        }
    }

    private bool CanMoveToFolder(MailFolderItem? folder) =>
        folder is not null && CanRunAction() && SelectedMessage?.Message.MailboxId == folder.MailboxId;

    private Task MoveToFolderAsync(MailFolderItem folder) =>
        RunActionAsync(new ConversationActionRequest(ConversationAction.Move, SelectedMessage!.Message, folder));

    private async Task RunActionAsync(ConversationActionRequest request)
    {
        if (_action is null)
        {
            return;
        }
        _runningActionCount++;
        IsActionRunning = true;
        try
        {
            await _action(request);
        }
        finally
        {
            IsActionRunning = --_runningActionCount > 0;
        }
    }

    private void RefreshActionCommands()
    {
        ((AsyncCommand)ReplyCommand).Refresh();
        ((AsyncCommand)ReplyAllCommand).Refresh();
        ((AsyncCommand)ForwardCommand).Refresh();
        ((AsyncCommand)ArchiveCommand).Refresh();
        ((AsyncCommand)DeleteCommand).Refresh();
        ((AsyncCommand)JunkCommand).Refresh();
        ((AsyncCommand)NotJunkCommand).Refresh();
        ((AsyncCommand)ToggleReadCommand).Refresh();
        ((AsyncCommand)ToggleFlagCommand).Refresh();
        ((AsyncCommand)TogglePinCommand).Refresh();
        ((AsyncCommand)ViewHeadersCommand).Refresh();
        ((AsyncCommand<MailFolderItem>)MoveToFolderCommand).Refresh();
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
        CollectionUpdates.Reconcile(Messages, reconciled, static message => message.Identity);
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
    public string Recipients
    {
        get
        {
            var to = $"To: {string.Join(", ", _message.To.Select(address => address.ToString()))}";
            return _message.Cc is { Count: > 0 }
                ? $"{to}  |  Cc: {string.Join(", ", _message.Cc.Select(address => address.ToString()))}"
                : to;
        }
    }
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
