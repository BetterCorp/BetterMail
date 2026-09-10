using System.Collections.ObjectModel;
using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class ComposeWindowViewModel : ViewModelBase
{
    private readonly Func<ComposeSender, string, DraftMessage, Task> _send;
    private readonly Func<ComposeSender, ComposeIntent, SignatureContent?> _signatureForSender;
    private readonly Func<LocalDraft, Task>? _saveDraft;
    private readonly Func<string, Task>? _deleteDraft;
    private readonly TimeSpan _autosaveDelay;
    private readonly MailContentRenderer _renderer = new();
    private readonly SemaphoreSlim _draftGate = new(1, 1);
    private readonly string _draftId;
    private readonly ComposeIntent _intent;
    private readonly string? _conversationIdentity;
    private CancellationTokenSource? _autosaveCancellation;
    private ComposeSender? _selectedSender;
    private string _subject;
    private string _body;
    private string? _error;
    private string _draftStatus = "";
    private bool _sent;
    private bool _isSending;
    private MailImportance _importance;
    private bool _isFlagged;
    private bool _requestReadReceipt;
    private bool _requestDeliveryReceipt;
    private bool _manageSignature;
    private string? _managedSignatureBlock;

    public ComposeWindowViewModel(
        IReadOnlyList<MailAccount> accounts,
        IReadOnlyList<Mailbox> mailboxes,
        ComposeRequest request,
        Func<ComposeSender, string, DraftMessage, Task> send,
        Func<LocalDraft, Task>? saveDraft = null,
        Func<string, Task>? deleteDraft = null,
        TimeSpan? autosaveDelay = null,
        Func<ComposeSender, ComposeIntent, SignatureContent?>? signatureForSender = null,
        Func<string, CancellationToken, Task<IReadOnlyList<RecipientSuggestion>>>? searchRecipients = null)
    {
        Senders = new ObservableCollection<ComposeSender>(
            from mailbox in mailboxes
            join account in accounts on mailbox.AccountId equals account.AccountId
            where !mailbox.IsShared || mailbox.CanSendAs || mailbox.CanSendOnBehalf
            select new ComposeSender(account, mailbox));
        _selectedSender = request.MailboxId is null
            ? Senders.FirstOrDefault()
            : Senders.FirstOrDefault(sender =>
                sender.Account.AccountId == request.AccountId && sender.Mailbox.Id == request.MailboxId);
        ToField = new ComposeRecipientField("To", request.To, searchRecipients, RecipientsChanged);
        CcField = new ComposeRecipientField("Cc", request.Cc, searchRecipients, RecipientsChanged);
        BccField = new ComposeRecipientField("Bcc", request.Bcc, searchRecipients, RecipientsChanged);
        RecipientFields = [ToField, CcField, BccField];
        _subject = request.Subject;
        _importance = request.Importance;
        _isFlagged = request.IsFlagged;
        _requestReadReceipt = request.RequestReadReceipt;
        _requestDeliveryReceipt = request.RequestDeliveryReceipt;
        _body = _renderer.PrepareComposeHtml(request.Body, request.IsHtml);
        _draftId = request.DraftId ?? Guid.NewGuid().ToString("N");
        _send = send;
        _signatureForSender = signatureForSender ?? ((_, _) => null);
        _intent = request.Intent;
        _conversationIdentity = request.ConversationIdentity;
        _saveDraft = saveDraft;
        _deleteDraft = deleteDraft;
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromMilliseconds(600);
        _manageSignature = request.DraftId is null;
        ApplySignatureForSender(_selectedSender);
        foreach (var attachment in request.Attachments ?? [])
        {
            Attachments.Add(attachment);
        }
        SendCommand = new AsyncCommand(SendAsync, () => SelectedSender is not null && !IsSending && !IsUploadingAttachment);
        DeleteCommand = new AsyncCommand(DeleteSavedDraftAsync, () => _deleteDraft is not null && !IsSending && !IsUploadingAttachment);
        RemoveAttachmentCommand = new AsyncCommand<DraftAttachment>(RemoveAttachmentAsync);
        if (HasContent())
        {
            ScheduleAutosave();
        }
    }

    public event EventHandler? Sent;
    public event EventHandler? Deleted;
    internal string DraftId => _draftId;
    public ObservableCollection<ComposeSender> Senders { get; }
    public ObservableCollection<DraftAttachment> Attachments { get; } = [];
    public IReadOnlyList<ComposeRecipientField> RecipientFields { get; }
    public ComposeRecipientField ToField { get; }
    public ComposeRecipientField CcField { get; }
    public ComposeRecipientField BccField { get; }
    public ICommand SendCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public IReadOnlyList<MailImportance> ImportanceLevels { get; } = Enum.GetValues<MailImportance>();
    public MailImportance Importance { get => _importance; set => SetAndSchedule(ref _importance, value); }
    public bool SupportsReceipts => SelectedSender?.Account.ProviderId == "microsoft365";
    public bool RequestReadReceipt { get => SupportsReceipts && _requestReadReceipt; set => SetAndSchedule(ref _requestReadReceipt, value); }
    public bool RequestDeliveryReceipt { get => SupportsReceipts && _requestDeliveryReceipt; set => SetAndSchedule(ref _requestDeliveryReceipt, value); }
    public bool SupportsFollowUpFlag => SelectedSender?.Account.ProviderId == "microsoft365";
    public bool IsFlagged { get => SupportsFollowUpFlag && _isFlagged; set => SetAndSchedule(ref _isFlagged, value); }

    public ComposeSender? SelectedSender
    {
        get => _selectedSender;
        set
        {
            if (SetProperty(ref _selectedSender, value))
            {
                ((AsyncCommand)SendCommand).Refresh();
                ((AsyncCommand)DeleteCommand).Refresh();
                ApplySignatureForSender(value);
                RaisePropertyChanged(nameof(SupportsFollowUpFlag));
                RaisePropertyChanged(nameof(IsFlagged));
                RaisePropertyChanged(nameof(SupportsReceipts));
                RaisePropertyChanged(nameof(RequestReadReceipt));
                RaisePropertyChanged(nameof(RequestDeliveryReceipt));
                ScheduleAutosave();
            }
        }
    }

    public string To
    {
        get => ToField.Serialized;
        set => ToField.SetSerialized(value);
    }

    public string Cc
    {
        get => CcField.Serialized;
        set => CcField.SetSerialized(value);
    }

    public string Bcc
    {
        get => BccField.Serialized;
        set => BccField.SetSerialized(value);
    }

    public string Subject
    {
        get => _subject;
        set => SetAndSchedule(ref _subject, value);
    }

    public string Body
    {
        get => _body;
        set
        {
            if (_managedSignatureBlock is not null &&
                !value.Contains(_managedSignatureBlock, StringComparison.Ordinal))
            {
                _manageSignature = false;
                _managedSignatureBlock = null;
            }
            SetAndSchedule(ref _body, value);
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                ((AsyncCommand)SendCommand).Refresh();
                ((AsyncCommand)DeleteCommand).Refresh();
                RaisePropertyChanged(nameof(CanChangeAttachments));
            }
        }
    }

    public bool CanChangeAttachments => !IsSending && !IsUploadingAttachment;

    public string DraftStatus
    {
        get => _draftStatus;
        private set => SetProperty(ref _draftStatus, value);
    }

    private bool _isUploadingAttachment;
    public bool IsUploadingAttachment
    {
        get => _isUploadingAttachment;
        set { if (SetProperty(ref _isUploadingAttachment, value)) { ((AsyncCommand)SendCommand).Refresh(); ((AsyncCommand)DeleteCommand).Refresh(); RaisePropertyChanged(nameof(CanChangeAttachments)); } }
    }

    internal bool TryBeginFileAttachmentUpload(IEnumerable<string> names)
    {
        if (!CanChangeAttachments)
        {
            ReportError($"Files were not attached: {string.Join(", ", names)}. Wait for the current operation to finish and try again.");
            return false;
        }
        IsUploadingAttachment = true;
        return true;
    }

    private readonly Queue<DraftAttachment> _droppedAttachments = new();
    private bool _processingDroppedAttachments;

    // Called on the UI thread; keep the busy state set while the entire drop queue drains.
    internal async Task AttachDroppedAsync(DraftAttachment attachment, Func<DraftAttachment, Task> uploadLarge)
    {
        if (IsSending || IsUploadingAttachment && !_processingDroppedAttachments)
        {
            ReportError($"'{attachment.Name}' was not attached. Wait for the current operation to finish and drop it again.");
            return;
        }
        _droppedAttachments.Enqueue(attachment);
        if (_processingDroppedAttachments) return;
        _processingDroppedAttachments = true;
        IsUploadingAttachment = true;
        try
        {
            while (_droppedAttachments.TryDequeue(out var file))
            {
                try
                {
                    if (LargeAttachmentPolicy.UseDrive(file.Size, Attachments)) await uploadLarge(file);
                    else AddAttachment(file);
                }
                catch (Exception exception) { ReportError($"'{file.Name}' could not be attached: {exception.Message}"); }
            }
        }
        finally
        {
            _processingDroppedAttachments = false;
            IsUploadingAttachment = false;
        }
    }

    public void AddAttachment(DraftAttachment attachment)
    {
        if (!ValidateAttachmentSize(attachment.Name, attachment.Size))
        {
            return;
        }

        Attachments.Add(attachment);
        ScheduleAutosave();
    }

    public void ReportError(string error) =>
        Error = IsUploadingAttachment && HasError ? Error + Environment.NewLine + error : error;

    public void DismissError() => Error = null;

    public bool ValidateAttachmentSize(string name, long size)
    {
        if (size is < 0 or > DraftAttachment.MaximumSizeBytes)
        {
            ReportError($"'{name}' is larger than the 150 MB Microsoft Graph attachment limit.");
            return false;
        }
        return true;
    }

    private Task RemoveAttachmentAsync(DraftAttachment attachment)
    {
        Attachments.Remove(attachment);
        ScheduleAutosave();
        return Task.CompletedTask;
    }

    public async Task FlushDraftAsync()
    {
        _autosaveCancellation?.Cancel();
        try
        {
            await SaveDraftNowAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError($"Draft could not be saved: {exception.Message}");
        }
    }

    private async Task SendAsync()
    {
        IsSending = true;
        Error = null;
        try
        {
            ToField.CommitQuery();
            CcField.CommitQuery();
            BccField.CommitQuery();
            var recipients = ParseRecipients(To);
            var cc = ParseRecipients(Cc);
            var bcc = ParseRecipients(Bcc);
            if (recipients.Count == 0)
            {
                throw new InvalidOperationException("Add at least one recipient.");
            }

            if (SelectedSender is null)
            {
                throw new InvalidOperationException("Choose a sending account.");
            }

            _autosaveCancellation?.Cancel();
            await SaveDraftNowAsync();
            var outgoing = _renderer.PrepareOutgoingHtml(Body, Attachments.ToArray());
            await _send(SelectedSender, _draftId, new DraftMessage(
                Subject.Trim(),
                recipients,
                outgoing.Html,
                IsHtml: true,
                cc,
                bcc,
                outgoing.Attachments, Importance, IsFlagged, RequestReadReceipt, RequestDeliveryReceipt));
            _sent = true;
            Sent?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Error = "The message could not be queued. Your draft is still open; try again.";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    private void SetAndSchedule<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            ScheduleAutosave();
        }
    }

    private void RecipientsChanged()
    {
        RaisePropertyChanged(nameof(To));
        RaisePropertyChanged(nameof(Cc));
        RaisePropertyChanged(nameof(Bcc));
        ScheduleAutosave();
    }

    private void ApplySignatureForSender(ComposeSender? sender)
    {
        if (!_manageSignature || sender is null)
        {
            return;
        }

        if (_managedSignatureBlock is not null)
        {
            var existing = _body.IndexOf(_managedSignatureBlock, StringComparison.Ordinal);
            if (existing < 0)
            {
                _manageSignature = false;
                _managedSignatureBlock = null;
                return;
            }
            _body = _body.Remove(existing, _managedSignatureBlock.Length);
        }

        var signature = _signatureForSender(sender, _intent);
        if (signature is null || string.IsNullOrWhiteSpace(signature.Html))
        {
            _managedSignatureBlock = null;
            RaisePropertyChanged(nameof(Body));
            return;
        }
        var block = $"<!--bettermail-signature-start--><div data-bettermail-signature=\"{signature.Id}\">{signature.Html}</div><!--bettermail-signature-end-->";
        var beforeQuotedContent = _intent is ComposeIntent.Reply or ComposeIntent.ReplyAll or ComposeIntent.Forward;
        _managedSignatureBlock = string.IsNullOrWhiteSpace(_body)
            ? block
            : beforeQuotedContent ? $"{block}<br><br>" : $"<br><br>{block}";
        if (_managedSignatureBlock is not null)
        {
            _body = beforeQuotedContent ? _managedSignatureBlock + _body : _body + _managedSignatureBlock;
        }
        RaisePropertyChanged(nameof(Body));
    }

    private void ScheduleAutosave()
    {
        if (_saveDraft is null || _sent || SelectedSender is null)
        {
            return;
        }

        _autosaveCancellation?.Cancel();
        _autosaveCancellation = new CancellationTokenSource();
        DraftStatus = "Saving...";
        _ = AutosaveAfterDelayAsync(_autosaveCancellation.Token);
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_autosaveDelay, cancellationToken);
            await SaveDraftNowAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportError($"Draft could not be saved: {exception.Message}");
            DraftStatus = "Not saved";
        }
    }

    private async Task SaveDraftNowAsync()
    {
        var sender = SelectedSender;
        if (_saveDraft is null || _sent || sender is null)
        {
            return;
        }

        await _draftGate.WaitAsync();
        try
        {
            if (_sent)
            {
                return;
            }
            if (!HasContent())
            {
                if (_deleteDraft is not null)
                {
                    await _deleteDraft(_draftId);
                }
                DraftStatus = "";
                return;
            }

            await _saveDraft(new LocalDraft(
                _draftId,
                sender.Account.AccountId,
                sender.Mailbox.Id,
                To,
                Cc,
                Bcc,
                Subject,
                _renderer.SanitizeComposeHtml(Body),
                Attachments.ToArray(),
                DateTimeOffset.UtcNow,
                IsHtml: true,
                ConversationIdentity: _conversationIdentity, Importance: Importance, IsFlagged: IsFlagged,
                RequestReadReceipt: RequestReadReceipt, RequestDeliveryReceipt: RequestDeliveryReceipt));
            DraftStatus = "Saved";
        }
        finally
        {
            _draftGate.Release();
        }
    }

    private async Task DeleteSavedDraftAsync()
    {
        _autosaveCancellation?.Cancel();
        _sent = true;
        await _draftGate.WaitAsync();
        try
        {
            if (_deleteDraft is not null)
            {
                await _deleteDraft(_draftId);
            }
            DraftStatus = "";
            Deleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _sent = false;
            Error = $"Draft could not be deleted: {exception.Message}";
        }
        finally
        {
            _draftGate.Release();
        }
    }

    private bool HasContent() =>
        !string.IsNullOrWhiteSpace(To) ||
        !string.IsNullOrWhiteSpace(Cc) ||
        !string.IsNullOrWhiteSpace(Bcc) ||
        !string.IsNullOrWhiteSpace(Subject) ||
        !string.IsNullOrWhiteSpace(Body) ||
        Attachments.Count > 0 || Importance != MailImportance.Normal || IsFlagged || RequestReadReceipt || RequestDeliveryReceipt;

    public static IReadOnlyList<BetterMail.Core.MailAddress> ParseRecipients(string value) => MailAddressList.Parse(value);
}

public sealed record RecipientSuggestion(string DisplayName, string Address, string Source)
{
    public string PrimaryText => string.IsNullOrWhiteSpace(DisplayName) ? Address : DisplayName;
    public string SecondaryText => string.IsNullOrWhiteSpace(Source) ? Address : $"{Address} · {Source}";
}

public sealed record ComposeRecipientToken(string DisplayName, string Address)
{
    public string Text => string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Equals(Address, StringComparison.OrdinalIgnoreCase)
        ? Address : $"{DisplayName} <{Address}>";
    public string Serialized => new BetterMail.Core.MailAddress(DisplayName, Address).ToString();
}

public sealed class ComposeRecipientField : ViewModelBase
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RecipientSuggestion>>>? _search;
    private readonly Action _changed;
    private CancellationTokenSource? _searchCancellation;
    private string _query = "";
    private bool _isSearchOpen;

    public ComposeRecipientField(
        string label,
        string initialValue,
        Func<string, CancellationToken, Task<IReadOnlyList<RecipientSuggestion>>>? search,
        Action changed)
    {
        Label = label;
        _search = search;
        _changed = changed;
        AddSuggestionCommand = new AsyncCommand<RecipientSuggestion>(AddSuggestionAsync);
        RemoveTokenCommand = new AsyncCommand<ComposeRecipientToken>(RemoveTokenAsync);
        SetSerialized(initialValue, notify: false);
    }

    public string Label { get; }
    public ObservableCollection<ComposeRecipientToken> Tokens { get; } = [];
    public ObservableCollection<RecipientSuggestion> Suggestions { get; } = [];
    public ICommand AddSuggestionCommand { get; }
    public ICommand RemoveTokenCommand { get; }
    public string Serialized
    {
        get
        {
            var tokens = string.Join("; ", Tokens.Select(static token => token.Serialized));
            return string.IsNullOrWhiteSpace(Query) ? tokens : string.IsNullOrWhiteSpace(tokens) ? Query : $"{tokens}; {Query}";
        }
    }
    public bool IsSearchOpen { get => _isSearchOpen; private set => SetProperty(ref _isSearchOpen, value); }
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                RaisePropertyChanged(nameof(Serialized));
                _changed();
                _ = SearchAsync(value);
            }
        }
    }

    public void SetSerialized(string value) => SetSerialized(value, notify: true);

    public void CommitQuery()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            return;
        }
        var parsed = MailAddressList.Parse(Query);
        if (parsed.Count == 0)
        {
            throw new FormatException($"'{Query}' is not a valid email address.");
        }
        foreach (var address in parsed)
        {
            var name = string.IsNullOrWhiteSpace(address.Name)
                ? Suggestions.FirstOrDefault(suggestion => suggestion.Address.Equals(address.Address, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? ""
                : address.Name;
            Add(name, address.Address);
        }
        Query = "";
        CloseSearch();
    }

    private void SetSerialized(string value, bool notify)
    {
        Tokens.Clear();
        _query = "";
        try
        {
            foreach (var parsed in MailAddressList.Parse(value))
                Add(parsed.Name, parsed.Address, notify: false);
        }
        catch (FormatException) { _query = value; }
        RaisePropertyChanged(nameof(Query));
        RaisePropertyChanged(nameof(Serialized));
        if (notify)
        {
            _changed();
        }
    }

    public void RefreshSearch() => _ = SearchAsync(Query);

    public bool CommitFirstSuggestion()
    {
        var suggestion = Suggestions.FirstOrDefault();
        if (suggestion is null)
        {
            return false;
        }
        Add(suggestion.DisplayName, suggestion.Address);
        Query = "";
        CloseSearch();
        return true;
    }

    private async Task SearchAsync(string query)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        if (_search is null || string.IsNullOrWhiteSpace(query))
        {
            CloseSearch();
            return;
        }
        try
        {
            var results = await _search(query.Trim(), token);
            token.ThrowIfCancellationRequested();
            CollectionUpdates.Reconcile(
                Suggestions,
                results.Where(result => Tokens.All(existing =>
                        !string.Equals(existing.Address, result.Address, StringComparison.OrdinalIgnoreCase)))
                    .ToArray(),
                static result => result.Address.ToLowerInvariant());
            IsSearchOpen = Suggestions.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
    }
    private Task AddSuggestionAsync(RecipientSuggestion suggestion)
    {
        Add(suggestion.DisplayName, suggestion.Address);
        Query = "";
        CloseSearch();
        return Task.CompletedTask;
    }

    private Task RemoveTokenAsync(ComposeRecipientToken token)
    {
        if (Tokens.Remove(token))
        {
            RaisePropertyChanged(nameof(Serialized));
            _changed();
        }
        return Task.CompletedTask;
    }

    private void Add(string name, string address, bool notify = true)
    {
        if (Tokens.Any(token => string.Equals(token.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        Tokens.Add(new(name, address));
        RaisePropertyChanged(nameof(Serialized));
        if (notify)
        {
            _changed();
        }
    }

    private void CloseSearch()
    {
        Suggestions.Clear();
        IsSearchOpen = false;
    }
}
