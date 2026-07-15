using System.Collections.ObjectModel;
using System.Net;
using System.Net.Mail;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class ComposeWindowViewModel : ViewModelBase
{
    private readonly Func<ComposeSender, string, DraftMessage, Task> _send;
    private readonly Func<ComposeSender, string> _signatureForSender;
    private readonly Func<LocalDraft, Task>? _saveDraft;
    private readonly Func<string, Task>? _deleteDraft;
    private readonly TimeSpan _autosaveDelay;
    private readonly MailContentRenderer _renderer = new();
    private readonly SemaphoreSlim _draftGate = new(1, 1);
    private readonly string _draftId;
    private CancellationTokenSource? _autosaveCancellation;
    private ComposeSender? _selectedSender;
    private string _subject;
    private string _body;
    private string? _error;
    private string _draftStatus = "";
    private bool _sent;
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
        Func<ComposeSender, string>? signatureForSender = null,
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
        _body = _renderer.PrepareComposeHtml(request.Body, request.IsHtml);
        _draftId = request.DraftId ?? Guid.NewGuid().ToString("N");
        _send = send;
        _signatureForSender = signatureForSender ?? (_ => "");
        _saveDraft = saveDraft;
        _deleteDraft = deleteDraft;
        _autosaveDelay = autosaveDelay ?? TimeSpan.FromMilliseconds(600);
        _manageSignature = request.DraftId is null;
        ApplySignatureForSender(_selectedSender);
        foreach (var attachment in request.Attachments ?? [])
        {
            Attachments.Add(attachment);
        }
        SendCommand = new AsyncCommand(SendAsync, () => SelectedSender is not null);
        RemoveAttachmentCommand = new AsyncCommand<DraftAttachment>(RemoveAttachmentAsync);
        if (HasContent())
        {
            ScheduleAutosave();
        }
    }

    public event EventHandler? Sent;
    public ObservableCollection<ComposeSender> Senders { get; }
    public ObservableCollection<DraftAttachment> Attachments { get; } = [];
    public IReadOnlyList<ComposeRecipientField> RecipientFields { get; }
    public ComposeRecipientField ToField { get; }
    public ComposeRecipientField CcField { get; }
    public ComposeRecipientField BccField { get; }
    public ICommand SendCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }

    public ComposeSender? SelectedSender
    {
        get => _selectedSender;
        set
        {
            if (SetProperty(ref _selectedSender, value))
            {
                ((AsyncCommand)SendCommand).Refresh();
                ApplySignatureForSender(value);
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
                !value.EndsWith(_managedSignatureBlock, StringComparison.Ordinal))
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
        private set => SetProperty(ref _error, value);
    }

    public string DraftStatus
    {
        get => _draftStatus;
        private set => SetProperty(ref _draftStatus, value);
    }

    public void AddAttachment(DraftAttachment attachment)
    {
        if (!ValidateAttachmentSize(attachment.Name, attachment.Size))
        {
            return;
        }

        Error = null;
        Attachments.Add(attachment);
        ScheduleAutosave();
    }

    public void ReportError(string error) => Error = error;

    public bool ValidateAttachmentSize(string name, long size)
    {
        if (size is < 0 or > DraftAttachment.MaximumSizeBytes)
        {
            Error = $"'{name}' is larger than the 150 MB Microsoft Graph attachment limit.";
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
            Error = $"Draft could not be saved: {exception.Message}";
        }
    }

    private async Task SendAsync()
    {
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
            var safeBody = _renderer.SanitizeComposeHtml(Body);
            await _send(SelectedSender, _draftId, new DraftMessage(
                Subject.Trim(),
                recipients,
                safeBody,
                IsHtml: true,
                cc,
                bcc,
                Attachments.ToArray()));
            _sent = true;
            Sent?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
        }
    }

    private void SetAndSchedule(ref string field, string value, [CallerMemberName] string? propertyName = null)
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
            if (!_body.EndsWith(_managedSignatureBlock, StringComparison.Ordinal))
            {
                _manageSignature = false;
                _managedSignatureBlock = null;
                return;
            }
            _body = _body[..^_managedSignatureBlock.Length];
        }

        var signature = WebUtility.HtmlEncode(_signatureForSender(sender).Trim())
            .Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal);
        _managedSignatureBlock = string.IsNullOrWhiteSpace(signature)
            ? null
            : string.IsNullOrWhiteSpace(_body)
                ? signature
                : $"<br><br>-- <br>{signature}";
        if (_managedSignatureBlock is not null)
        {
            _body += _managedSignatureBlock;
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
            Error = $"Draft could not be saved: {exception.Message}";
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
                IsHtml: true));
            DraftStatus = "Saved";
        }
        finally
        {
            _draftGate.Release();
        }
    }

    private async Task DeleteSavedDraftAsync()
    {
        await _draftGate.WaitAsync();
        try
        {
            if (_deleteDraft is not null)
            {
                await _deleteDraft(_draftId);
            }
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
        Attachments.Count > 0;

    public static IReadOnlyList<BetterMail.Core.MailAddress> ParseRecipients(string value)
    {
        var recipients = new List<BetterMail.Core.MailAddress>();
        foreach (var part in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!System.Net.Mail.MailAddress.TryCreate(part, out var parsed))
            {
                throw new FormatException($"'{part}' is not a valid email address.");
            }

            recipients.Add(new BetterMail.Core.MailAddress(parsed.DisplayName, parsed.Address));
        }

        return recipients;
    }
}

public sealed record RecipientSuggestion(string DisplayName, string Address, string Source)
{
    public string PrimaryText => string.IsNullOrWhiteSpace(DisplayName) ? Address : DisplayName;
    public string SecondaryText => string.IsNullOrWhiteSpace(Source) ? Address : $"{Address} · {Source}";
}

public sealed record ComposeRecipientToken(string DisplayName, string Address)
{
    public string Text => string.IsNullOrWhiteSpace(DisplayName) ? Address : DisplayName;
    public string Serialized => string.IsNullOrWhiteSpace(DisplayName) ? Address : $"{DisplayName} <{Address}>";
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
                _ = SearchAfterDelayAsync(value);
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
        var parts = Query.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = parts.Select(part => System.Net.Mail.MailAddress.TryCreate(part, out var address)
                ? address
                : throw new FormatException($"'{part}' is not a valid email address."))
            .ToArray();
        if (parsed.Length == 0)
        {
            throw new FormatException($"'{Query}' is not a valid email address.");
        }
        foreach (var address in parsed)
        {
            Add(address.DisplayName, address.Address);
        }
        Query = "";
        CloseSearch();
    }

    private void SetSerialized(string value, bool notify)
    {
        Tokens.Clear();
        var invalid = new List<string>();
        foreach (var part in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.Mail.MailAddress.TryCreate(part, out var parsed))
            {
                Add(parsed.DisplayName, parsed.Address, notify: false);
            }
            else
            {
                invalid.Add(part);
            }
        }
        _query = string.Join("; ", invalid);
        RaisePropertyChanged(nameof(Query));
        RaisePropertyChanged(nameof(Serialized));
        if (notify)
        {
            _changed();
        }
    }

    private async Task SearchAfterDelayAsync(string query)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        Suggestions.Clear();
        IsSearchOpen = false;
        if (_search is null || query.Trim().Length < 2)
        {
            return;
        }
        try
        {
            await Task.Delay(200, token);
            var results = await _search(query.Trim(), token);
            token.ThrowIfCancellationRequested();
            foreach (var result in results.Where(result => Tokens.All(token =>
                         !string.Equals(token.Address, result.Address, StringComparison.OrdinalIgnoreCase))))
            {
                Suggestions.Add(result);
            }
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
