using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Input;
using BetterMail.Core;
using BetterMail.Microsoft365;

namespace BetterMail.App;

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] SyncFrames = ["◴", "◷", "◶", "◵"];
    private static readonly IReadOnlyDictionary<string, string> Accents = new Dictionary<string, string>
    {
        ["Violet"] = "#615FFF",
        ["Blue"] = "#157EFB",
        ["Teal"] = "#008C7A",
        ["Rose"] = "#C84B71",
        ["Orange"] = "#D66A1F"
    };
    private static readonly MailQuickActionOption[] AvailableMailQuickActions =
    [
        new("none", "None", "\u2014"),
        new("read", "Read / unread", "✉"),
        new("flag", "Flag / clear flag", "⚑"),
        new("archive", "Archive", "▣"),
        new("delete", "Delete", "×"),
        new("move", "Move to folder", "↪", IsMove: true),
        new("junk", "Junk / not junk", "!"),
        new("more", "More actions", "\u22EF", IsMore: true)
    ];
    private static readonly string[] DefaultMailQuickActionIds = ["read", "flag", "archive", "delete"];

    private readonly EncryptedMailStore? _store;
    private readonly string _dataDirectory;
    private readonly Action<string> _applyTheme;
    private readonly Action<string> _applyAccent;
    private readonly MailContentRenderer _renderer = new();
    private readonly TimeSpan _markReadDelay;
    private readonly NewMailNotificationCoordinator _newMailNotifications;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _draftSyncLocks = new(StringComparer.Ordinal);
    private Microsoft365AuthService? _authentication;
    private IMailProvider? _provider;
    private IWorkspaceProvider? _workspaceProvider;
    private CalendarWorkspaceViewModel? _calendarWorkspace;
    private NotesWorkspaceViewModel? _notesWorkspace;
    private DriveWorkspaceViewModel? _driveWorkspace;
    private TasksWorkspaceViewModel? _tasksWorkspace;
    private CancellationTokenSource? _driveAttachmentCancellation;
    private MailMessage? _selectedMessage;
    private MailFolderItem? _selectedFolder;
    private MailAccount? _pendingRemovalAccount;
    private string _searchText = "";
    private string _status = "Loading mailbox…";
    private string? _error;
    private bool _isBusy;
    private bool _isCompact;
    private bool _desktopNotificationsEnabled = true;
    private string _mailSyncRange = "All mail";
    private bool _isLoadingAttachments;
    private bool _isSyncing;
    private int _syncRunning;
    private int _workspaceSyncRunning;
    private DateTimeOffset _lastWorkspaceSyncAt = DateTimeOffset.MinValue;
    private bool _isMailActionRunning;
    private string _mailActionStatus = "";
    private bool _allowRemoteContent;
    private bool _autoSyncStarted;
    private int _syncFrame;
    private int _selectionVersion;
    private CancellationTokenSource? _selectionWorkCancellation;
    private CancellationTokenSource? _globalSearchCancellation;
    private string? _dismissedSearchText;
    private readonly List<MailMessage> _latestMailSearchResults = [];
    private readonly HashSet<string> _subjectRepairAttempts = new(StringComparer.Ordinal);
    private bool _initializationComplete;
    private bool _isSettingsOpen;
    private bool _isConfirmingAccountRemoval;
    private bool _isWorkspaceLoading;
    private bool _isDraftsView;
    private bool _isGlobalSearchOpen;
    private bool _isGlobalSearchRunning;
    private bool _isSearchResultsView;
    private SearchAccountFilter? _selectedSearchAccountFilter;
    private SearchFolderFilter? _selectedSearchFolderFilter;
    private string _selectedSearchScope = "Everything";
    private bool _includeArchivedMailInSearch;
    private string _activeModule = "Mail";
    private string _moduleSearchText = "";
    private string _selectedThemeMode = "System";
    private string _selectedAccentName = "Blue";
    private string? _defaultSenderMailboxId;
    private readonly Dictionary<string, MailboxSignaturePreferences> _mailboxSignatures = new(StringComparer.Ordinal);
    private string? _legacyFallbackSignatureId;
    private SignatureItem? _selectedSignature;
    private SignatureTemplate? _selectedSignatureTemplate =
        SignatureCatalog.Templates.FirstOrDefault(static template => template.Id == "minimal");
    private string _signatureEditorName = "";
    private string _signatureEditorHtml = "";
    private string? _signatureEditorError;
    private bool _isSignatureTemplatePickerOpen;
    private SignatureItem? _pendingSignatureDelete;
    private bool _isCreatingSignature;
    private SettingsTabItem? _selectedSettingsTab;
    private int _senderPreferencesVersion;
    private int _mailQuickActionsVersion;
    private bool _configuringMailQuickActions;
    private PersonEntry? _editingContact;
    private PersonEntry? _pendingDeleteContact;
    private MailAccount? _selectedContactAccount;
    private bool _isContactEditorOpen;
    private string _contactName = "";
    private string _contactEmails = "";
    private string _peopleErrorText = "";
    private bool _isContactActionRunning;
    private bool _isLoadingMailStatistics;
    private int _errorVersion;
    private bool _isReplacingSelectedMessage;

    internal string DataDirectory => _dataDirectory;
    internal string MailListContextKey => IsSearchResultsView
        ? $"search:{SearchText}"
        : IsDraftsView
            ? "drafts"
            : _selectedFolder is null
                ? "unified"
                : $"{_selectedFolder.MailboxId}:{_selectedFolder.ProviderId}";

    public MainWindowViewModel(
        EncryptedMailStore? store,
        string dataDirectory,
        Action<string> applyTheme,
        Action<string> applyAccent,
        string? startupError,
        IMailProvider? provider = null,
        TimeSpan? markReadDelay = null,
        IWorkspaceProvider? workspaceProvider = null,
        IDesktopNotificationService? desktopNotificationService = null)
    {
        _store = store;
        _dataDirectory = dataDirectory;
        _applyTheme = applyTheme;
        _applyAccent = applyAccent;
        _error = startupError;
        _provider = provider;
        _workspaceProvider = workspaceProvider;
        _markReadDelay = markReadDelay ?? TimeSpan.FromSeconds(2);
        _newMailNotifications = new NewMailNotificationCoordinator(
            desktopNotificationService ?? NoOpDesktopNotificationService.Instance);

        ConnectCommand = new AsyncCommand(ConnectAsync, () => _store is not null);
        AddSharedMailboxForAccountCommand = new AsyncCommand<MailAccount>(RequestSharedMailboxAsync);
        ReauthenticateAccountCommand = new AsyncCommand<MailAccount>(ReauthenticateAccountAsync);
        SyncCommand = new AsyncCommand(SyncAsync, () => Accounts.Count > 0 && _provider is not null && !IsSyncing);
        ToggleReadCommand = new AsyncCommand(ToggleReadAsync, CanRunSelectedMailAction);
        ArchiveCommand = new AsyncCommand(() => MoveSelectedMessageAsync("archive", "Archiving...", "Archived"), CanRunSelectedMailAction);
        DeleteCommand = new AsyncCommand(() => MoveSelectedMessageAsync("deleteditems", "Moving to Deleted Items...", "Moved to Deleted Items"), CanRunSelectedMailAction);
        JunkCommand = new AsyncCommand(() => MoveSelectedMessageAsync("junkemail", "Moving to Junk Email...", "Moved to Junk Email"), CanRunSelectedMailAction);
        NotJunkCommand = new AsyncCommand(() => MoveSelectedMessageAsync("inbox", "Moving to Inbox...", "Marked as not junk"), CanRunSelectedMailAction);
        ToggleFlagCommand = new AsyncCommand(ToggleFlagAsync, CanRunSelectedMailAction);
        MoveToFolderCommand = new AsyncCommand<MailFolderItem>(MoveSelectionToFolderAsync, CanMoveSelectionToFolder);
        ShowUnifiedInboxCommand = new AsyncCommand(ShowUnifiedInboxAsync);
        ShowDraftsCommand = new AsyncCommand(ShowDraftsAsync);
        ShowCalendarCommand = new AsyncCommand(() => ShowWorkspaceModuleAsync("Calendar"), CanOpenWorkspaceModule);
        ShowContactsCommand = new AsyncCommand(() => ShowWorkspaceModuleAsync("People"), CanOpenWorkspaceModule);
        ShowTasksCommand = new AsyncCommand(() => ShowWorkspaceModuleAsync("To Do"), CanOpenWorkspaceModule);
        ShowFilesCommand = new AsyncCommand(() => ShowWorkspaceModuleAsync("OneDrive"), CanOpenWorkspaceModule);
        ShowNotesCommand = new AsyncCommand(() => ShowWorkspaceModuleAsync("Notes"), CanOpenWorkspaceModule);
        RefreshWorkspaceCommand = new AsyncCommand(RefreshWorkspaceAsync, CanOpenWorkspaceModule);
        SearchWorkspaceCommand = new AsyncCommand(RefreshWorkspaceAsync, CanOpenWorkspaceModule);
        SelectFolderCommand = new AsyncCommand<MailFolderItem>(SelectFolderAsync);
        SearchCommand = new AsyncCommand(() => StartGlobalSearchAsync(debounce: false, forceOpen: true), () => _store is not null);
        OpenGlobalSearchResultCommand = new AsyncCommand<GlobalSearchResult>(OpenGlobalSearchResultAsync);
        OpenGlobalSearchGroupCommand = new AsyncCommand<GlobalSearchResult>(OpenGlobalSearchGroupAsync);
        CloseGlobalSearchCommand = new AsyncCommand(CloseGlobalSearchAsync);
        ClearGlobalSearchCommand = new AsyncCommand(ClearGlobalSearchAsync);
        ClearSearchResultsCommand = new AsyncCommand(ClearSearchResultsAsync);
        FocusSearchCommand = new AsyncCommand(FocusSearchAsync);
        ComposeCommand = new AsyncCommand(() => RequestComposeAsync(new ComposeRequest()));
        OpenDraftCommand = new AsyncCommand<LocalDraft>(OpenLocalDraftAsync);
        SetDefaultSenderCommand = new AsyncCommand<SenderSettingsItem>(SetDefaultSenderAsync);
        NewSignatureCommand = new AsyncCommand(OpenSignatureTemplatesAsync);
        CreateSignatureFromTemplateCommand = new AsyncCommand(
            CreateSignatureFromTemplateAsync,
            () => SelectedSignatureTemplate is not null);
        SaveSignatureCommand = new AsyncCommand(SaveSignatureAsync, () => HasUnsavedSignatureChanges);
        ResetSignatureCommand = new AsyncCommand(ResetSignatureAsync, () => HasUnsavedSignatureChanges);
        DuplicateSignatureCommand = new AsyncCommand(DuplicateSignatureAsync, () => CanManageSavedSignature);
        DeleteSignatureCommand = new AsyncCommand(RequestDeleteSignatureAsync, () => CanDeleteSelectedSignature);
        ConfirmDeleteSignatureCommand = new AsyncCommand(
            ConfirmDeleteSignatureAsync,
            () => _pendingSignatureDelete is not null);
        CancelDeleteSignatureCommand = new AsyncCommand(CancelDeleteSignatureAsync);
        CloseSignatureTemplatesCommand = new AsyncCommand(CloseSignatureTemplatesAsync);
        ReplyCommand = new AsyncCommand(ReplyAsync, CanReplyToSelectedMessage);
        ReplyAllCommand = new AsyncCommand(ReplyAllAsync, CanReplyToSelectedMessage);
        ForwardCommand = new AsyncCommand(ForwardAsync, CanReplyToSelectedMessage);
        ViewHeadersCommand = new AsyncCommand(ViewHeadersAsync, CanViewHeaders);
        SelectNextMessageCommand = new AsyncCommand(
            () => SelectAdjacentMessageAsync(1),
            () => SelectedMessage is not null && Messages.Count > 1);
        SelectPreviousMessageCommand = new AsyncCommand(
            () => SelectAdjacentMessageAsync(-1),
            () => SelectedMessage is not null && Messages.Count > 1);
        AllowRemoteContentCommand = new AsyncCommand(AllowRemoteContentAsync);
        OpenSettingsCommand = new AsyncCommand(OpenSettingsAsync);
        CloseSettingsCommand = new AsyncCommand(CloseSettingsAsync);
        RequestRemoveAccountCommand = new AsyncCommand<MailAccount>(RequestRemoveAccountAsync);
        CancelRemoveAccountCommand = new AsyncCommand(CancelRemoveAccountAsync);
        ConfirmRemoveAccountCommand = new AsyncCommand(RemovePendingAccountAsync, () => _pendingRemovalAccount is not null);
        EditContactCommand = new AsyncCommand<PersonEntry>(EditContactAsync);
        NewContactCommand = new AsyncCommand(OpenNewContactAsync, () => Accounts.Count > 0 && !_isContactActionRunning);
        RequestDeleteContactCommand = new AsyncCommand<PersonEntry>(RequestDeleteContactAsync);
        SaveContactCommand = new AsyncCommand(SaveContactAsync, () => IsContactEditorOpen && SelectedContactAccount is not null && !_isContactActionRunning);
        CancelEditContactCommand = new AsyncCommand(CancelEditContactAsync);
        ConfirmDeleteContactCommand = new AsyncCommand(ConfirmDeleteContactAsync, () => _pendingDeleteContact?.SavedContact is not null && !_isContactActionRunning);
        CancelDeleteContactCommand = new AsyncCommand(CancelDeleteContactAsync);
        ConversationThread = new ConversationThreadViewModel(
            _renderer,
            HandleConversationAction,
            location: MailLocation);
        SearchAccountFilters.Add(new SearchAccountFilter("All accounts", null));
        SearchFolderFilters.Add(new SearchFolderFilter("All mail folders", null, null));
        _selectedSearchAccountFilter = SearchAccountFilters[0];
        _selectedSearchFolderFilter = SearchFolderFilters[0];
        SettingsTabs.Add(new("Appearance"));
        SettingsTabs.Add(new("Mail & notifications"));
        SettingsTabs.Add(new("Signatures"));
        SettingsTabs.Add(new("Accounts"));
        SettingsTabs.Add(new("About"));
        _selectedSettingsTab = SettingsTabs[0];
        foreach (var id in DefaultMailQuickActionIds)
        {
            MailQuickActionSlots.Add(new(
                MailQuickActionSlots.Count + 1,
                AvailableMailQuickActions.First(action => action.Id == id),
                MailQuickActionChanged));
        }
        RebuildMailQuickActions();
        var defaultSignature = new SignatureItem(
            SignatureCatalog.Default.Id,
            SignatureCatalog.Default.Name,
            SignatureCatalog.Default.Html,
            isReadOnly: true);
        SignatureChoices.Add(new SignatureItem("", "None", "", isReadOnly: true));
        Signatures.Add(defaultSignature);
        SignatureChoices.Add(defaultSignature);
        _selectedSignature = defaultSignature;
        LoadSelectedSignatureEditor();
    }

    public event Action<ComposeRequest>? ComposeRequested;
    public event Action? SearchFocusRequested;
    public event Action<MailAccount>? SharedMailboxRequested;
    public event Action<MailHeadersDocument>? HeadersRequested;
    public ObservableCollection<MailAccount> Accounts { get; } = [];
    public ObservableCollection<Mailbox> Mailboxes { get; } = [];
    public IEnumerable<Mailbox> SharedMailboxes => Mailboxes.Where(static mailbox => mailbox.IsShared);
    public IEnumerable<AccountSettingsItem> SettingsAccounts => Accounts.Select(account => new AccountSettingsItem(
        account,
        Mailboxes.Where(mailbox => mailbox.AccountId == account.AccountId && mailbox.IsShared).ToArray()));
    public ObservableCollection<MailMessage> Messages { get; } = [];
    public ObservableCollection<MailMessage> SelectedMessages { get; } = [];
    public ObservableCollection<MailQuickActionOption> MailQuickActions { get; } = [];
    public bool HasMailQuickActions => MailQuickActions.Count > 0;
    public ObservableCollection<MailQuickActionSlot> MailQuickActionSlots { get; } = [];
    public ObservableCollection<MailFolderItem> Folders { get; } = [];
    public ObservableCollection<MailboxFolderGroup> FolderGroups { get; } = [];
    public ObservableCollection<MailAttachment> Attachments { get; } = [];
    public ObservableCollection<LocalDraft> Drafts { get; } = [];
    public ObservableCollection<MailboxStatisticsItem> MailStatistics { get; } = [];
    public ObservableCollection<GlobalSearchResult> GlobalSearchResults { get; } = [];
    public ObservableCollection<SearchAccountFilter> SearchAccountFilters { get; } = [];
    public ObservableCollection<SearchFolderFilter> SearchFolderFilters { get; } = [];
    internal IFilesProvider? FilesProvider => _workspaceProvider;
    public ObservableCollection<SenderSettingsItem> SenderSettings { get; } = [];
    public ObservableCollection<SignatureAccountSettingsItem> SignatureAccountSettings { get; } = [];
    public ObservableCollection<SignatureItem> Signatures { get; } = [];
    public ObservableCollection<SignatureItem> SignatureChoices { get; } = [];
    public ObservableCollection<SettingsTabItem> SettingsTabs { get; } = [];
    public IReadOnlyList<SignatureTemplate> SignatureTemplates { get; } = SignatureCatalog.Templates;
    public ObservableCollection<ContactInfo> Contacts { get; } = [];
    public ObservableCollection<PersonEntry> People { get; } = [];
    public IReadOnlyList<string> ThemeModes { get; } = ["System", "Light", "Dark"];
    public IReadOnlyList<string> AccentNames { get; } = Accents.Keys.ToArray();
    public IReadOnlyList<string> MailSyncRanges { get; } = ["1 month", "3 months", "6 months", "1 year", "All mail"];
    public IReadOnlyList<string> SearchScopes { get; } = ["Everything", "Mail", "OneDrive", "People", "Calendar", "To Do", "Notes"];
    public IReadOnlyList<MailQuickActionOption> MailQuickActionOptions => AvailableMailQuickActions;
    public bool IsLoadingMailStatistics
    {
        get => _isLoadingMailStatistics;
        private set => SetProperty(ref _isLoadingMailStatistics, value);
    }
    public bool HasMailStatistics => MailStatistics.Count > 0;
    public string MailStatisticsSummary =>
        $"{MailStatistics.Sum(static item => item.SyncedMessages):N0} synced locally ({MailSyncRange}) | " +
        $"{MailStatistics.Sum(static item => item.CloudMessages):N0} total on Microsoft 365 | " +
        $"{Folders.Count:N0} folders | {Drafts.Count:N0} local drafts";

    public ICommand ConnectCommand { get; }
    public ICommand AddSharedMailboxForAccountCommand { get; }
    public ICommand ReauthenticateAccountCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand ToggleReadCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand JunkCommand { get; }
    public ICommand NotJunkCommand { get; }
    public ICommand ToggleFlagCommand { get; }
    public ICommand MoveToFolderCommand { get; }
    public ICommand ShowUnifiedInboxCommand { get; }
    public ICommand ShowDraftsCommand { get; }
    public ICommand ShowCalendarCommand { get; }
    public ICommand ShowContactsCommand { get; }
    public ICommand ShowTasksCommand { get; }
    public ICommand ShowFilesCommand { get; }
    public ICommand ShowNotesCommand { get; }
    public ICommand RefreshWorkspaceCommand { get; }
    public ICommand SearchWorkspaceCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand OpenGlobalSearchResultCommand { get; }
    public ICommand OpenGlobalSearchGroupCommand { get; }
    public ICommand CloseGlobalSearchCommand { get; }
    public ICommand ClearGlobalSearchCommand { get; }
    public ICommand ClearSearchResultsCommand { get; }
    public ICommand FocusSearchCommand { get; }
    public ICommand ComposeCommand { get; }
    public ICommand OpenDraftCommand { get; }
    public ICommand SetDefaultSenderCommand { get; }
    public ICommand NewSignatureCommand { get; }
    public ICommand CreateSignatureFromTemplateCommand { get; }
    public ICommand SaveSignatureCommand { get; }
    public ICommand ResetSignatureCommand { get; }
    public ICommand DuplicateSignatureCommand { get; }
    public ICommand DeleteSignatureCommand { get; }
    public ICommand ConfirmDeleteSignatureCommand { get; }
    public ICommand CancelDeleteSignatureCommand { get; }
    public ICommand CloseSignatureTemplatesCommand { get; }
    public ICommand ReplyCommand { get; }
    public ICommand ReplyAllCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand ViewHeadersCommand { get; }
    public ICommand SelectNextMessageCommand { get; }
    public ICommand SelectPreviousMessageCommand { get; }
    public ICommand AllowRemoteContentCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseSettingsCommand { get; }
    public ICommand RequestRemoveAccountCommand { get; }
    public ICommand CancelRemoveAccountCommand { get; }
    public ICommand ConfirmRemoveAccountCommand { get; }
    public ICommand EditContactCommand { get; }
    public ICommand NewContactCommand { get; }
    public ICommand RequestDeleteContactCommand { get; }
    public ICommand SaveContactCommand { get; }
    public ICommand CancelEditContactCommand { get; }
    public ICommand ConfirmDeleteContactCommand { get; }
    public ICommand CancelDeleteContactCommand { get; }
    public CalendarWorkspaceViewModel? CalendarWorkspace
    {
        get => _calendarWorkspace;
        private set
        {
            if (SetProperty(ref _calendarWorkspace, value))
            {
                RaisePropertyChanged(nameof(ActiveWorkspace));
            }
        }
    }
    public NotesWorkspaceViewModel? NotesWorkspace
    {
        get => _notesWorkspace;
        private set
        {
            if (SetProperty(ref _notesWorkspace, value))
            {
                RaisePropertyChanged(nameof(ActiveWorkspace));
            }
        }
    }
    public DriveWorkspaceViewModel? DriveWorkspace
    {
        get => _driveWorkspace;
        private set
        {
            if (SetProperty(ref _driveWorkspace, value))
            {
                RaisePropertyChanged(nameof(ActiveWorkspace));
            }
        }
    }
    public TasksWorkspaceViewModel? TasksWorkspace
    {
        get => _tasksWorkspace;
        private set
        {
            if (SetProperty(ref _tasksWorkspace, value))
            {
                RaisePropertyChanged(nameof(ActiveWorkspace));
            }
        }
    }
    public ConversationThreadViewModel ConversationThread { get; }

    public MailMessage? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (value is null && _isReplacingSelectedMessage && _selectedMessage is not null)
            {
                return;
            }

            var isMetadataUpdate = SameMessage(_selectedMessage, value);
            if (SetProperty(ref _selectedMessage, value))
            {
                if (isMetadataUpdate)
                {
                    ReconcileConversation(value);
                    if (value is not null)
                    {
                        ConversationThread.SetAttachments(value, Attachments.ToArray());
                    }
                    RaisePropertyChanged(nameof(SenderInitials));
                    RaisePropertyChanged(nameof(SelectedSenderName));
                    RaisePropertyChanged(nameof(SelectedSenderAddress));
                    RaisePropertyChanged(nameof(SelectedRecipientsText));
                    RaisePropertyChanged(nameof(SelectedMessageTime));
                    RaisePropertyChanged(nameof(ToggleReadText));
                    RaisePropertyChanged(nameof(ToggleFlagText));
                    RaisePropertyChanged(nameof(HasSelectedAttachments));
                    RefreshMailActionCommands();
                    return;
                }

                var selectionVersion = ++_selectionVersion;
                _selectionWorkCancellation?.Cancel();
                _selectionWorkCancellation?.Dispose();
                _selectionWorkCancellation = new CancellationTokenSource();
                var selectionToken = _selectionWorkCancellation.Token;
                _allowRemoteContent = false;
                RaisePropertyChanged(nameof(HasSelectedMessage));
                RaisePropertyChanged(nameof(HasNoSelectedMessage));
                RaisePropertyChanged(nameof(SelectedMessageBodyUri));
                RaisePropertyChanged(nameof(HasBlockedRemoteContent));
                RaisePropertyChanged(nameof(ShowMessageBody));
                RaisePropertyChanged(nameof(SenderInitials));
                RaisePropertyChanged(nameof(SelectedSenderName));
                RaisePropertyChanged(nameof(SelectedSenderAddress));
                RaisePropertyChanged(nameof(SelectedRecipientsText));
                RaisePropertyChanged(nameof(SelectedMessageTime));
                RaisePropertyChanged(nameof(ToggleReadText));
                RaisePropertyChanged(nameof(ToggleFlagText));
                RaisePropertyChanged(nameof(HasSelectedAttachments));
                Attachments.Clear();
                ReconcileConversation(value);
                RaisePropertyChanged(nameof(AttachmentSummary));
                IsLoadingAttachments = false;
                ((AsyncCommand)ReplyCommand).Refresh();
                ((AsyncCommand)ReplyAllCommand).Refresh();
                ((AsyncCommand)ForwardCommand).Refresh();
                ((AsyncCommand)SelectNextMessageCommand).Refresh();
                ((AsyncCommand)SelectPreviousMessageCommand).Refresh();
                ((AsyncCommand)ToggleReadCommand).Refresh();
                RefreshMailActionCommands();
                _ = LoadConversationAsync(value, selectionVersion, selectionToken);
                _ = LoadAttachmentsAsync(value, selectionToken);
                _ = MarkReadAfterDelayAsync(value, selectionVersion, selectionToken);
            }
        }
    }

    public Uri SelectedMessageBodyUri => _renderer.Render(
        SelectedMessage?.Body ?? SelectedMessage?.Preview,
        SelectedMessage?.IsHtml == true && SelectedMessage.Body is not null,
        Attachments,
        _allowRemoteContent);

    public void SetSelectedMessages(IEnumerable<MailMessage> messages)
    {
        var selected = messages.DistinctBy(MessageKey).ToArray();
        Replace(SelectedMessages, selected);
        if (selected.Length > 0 && !selected.Any(message => SameMessage(message, SelectedMessage)))
        {
            SelectedMessage = selected[^1];
        }
        ((AsyncCommand)ReplyCommand).Refresh();
        ((AsyncCommand)ReplyAllCommand).Refresh();
        ((AsyncCommand)ForwardCommand).Refresh();
        RefreshMailActionCommands();
    }

    internal async Task<CachedMailPreview?> GetCachedPreviewAsync(
        PreviewWindowSession session,
        CancellationToken cancellationToken = default)
    {
        if (_store is null)
        {
            return null;
        }

        var selected = await _store.GetMessageAsync(
            session.MailboxId,
            session.ProviderMessageId,
            cancellationToken);
        if (selected is null)
        {
            return null;
        }

        var thread = BetterMail.Core.ConversationThread.ThreadIdentity(selected);
        IReadOnlyList<MailMessage> messages = await _store.GetThreadMessagesAsync(thread, cancellationToken);
        return new CachedMailPreview(selected, messages.Count == 0 ? [selected] : messages);
    }

    public bool HasSelectedMessage => SelectedMessage is not null;
    public bool HasBlockedRemoteContent => HasSelectedMessage && !_allowRemoteContent && _renderer.HasRemoteImages(
        SelectedMessage?.Body,
        SelectedMessage?.IsHtml == true);
    public bool HasNoSelectedMessage => !HasSelectedMessage;
    public bool ShowMessageBody => HasSelectedMessage && IsMailModule && !IsSettingsOpen && !IsBusy;
    public bool IsMailModule => ActiveModule == "Mail";
    public bool IsWorkspaceModule => !IsMailModule;
    public bool ShowMailSurface => IsMailModule && !IsSettingsOpen;
    public bool ShowWorkspaceSurface => IsWorkspaceModule && !IsSettingsOpen;
    public bool IsCalendarModule => ActiveModule == "Calendar";
    public bool IsGenericWorkspaceModule =>
        IsWorkspaceModule && !IsCalendarModule && !IsNotesModule && !IsFilesModule && !IsTasksModule;
    public bool IsContactsModule => ActiveModule == "People";
    public bool IsTasksModule => ActiveModule == "To Do";
    public bool IsFilesModule => ActiveModule == "OneDrive";
    public bool IsNotesModule => ActiveModule == "Notes";
    public object? ActiveWorkspace => ActiveModule switch
    {
        "Calendar" => CalendarWorkspace,
        "To Do" => TasksWorkspace,
        "OneDrive" => DriveWorkspace,
        "Notes" => NotesWorkspace,
        _ => null
    };
    public bool ShowWorkspaceSearch => IsContactsModule;
    public bool ShowWorkspaceRefresh => IsGenericWorkspaceModule && !ShowWorkspaceSearch;
    public bool IsWorkspaceEmpty => !IsWorkspaceLoading && ActiveModule switch
    {
        "Calendar" => false,
        "People" => People.Count == 0,
        "To Do" => false,
        "OneDrive" => false,
        "Notes" => false,
        _ => false
    };
    public bool HasAccounts => Accounts.Count > 0;
    public bool ShowOnboarding => _initializationComplete && !HasAccounts;
    public bool ShowFullScreenLoader => !_initializationComplete || IsBusy;
    public bool IsDraftsView
    {
        get => _isDraftsView;
        private set
        {
            if (SetProperty(ref _isDraftsView, value))
            {
                RaisePropertyChanged(nameof(IsMessageListView));
                RaisePropertyChanged(nameof(CurrentFolderName));
                RaisePropertyChanged(nameof(IsUnifiedInbox));
                RaisePropertyChanged(nameof(CurrentItemCountText));
                RaisePropertyChanged(nameof(ShowEmptyState));
                RaisePropertyChanged(nameof(ShowDraftEmptyState));
            }
        }
    }
    public bool IsMessageListView => !IsDraftsView;
    public bool IsUnifiedInbox => _selectedFolder is null && !IsDraftsView && !IsSearchResultsView;
    public bool ShowEmptyState => IsMessageListView && Messages.Count == 0 && !IsBusy;
    public bool ShowDraftEmptyState => IsDraftsView && Drafts.Count == 0;
    public string MessageCountText => $"{Messages.Count:N0} messages";
    public string CurrentItemCountText => IsDraftsView
        ? $"{Drafts.Count:N0} draft{(Drafts.Count == 1 ? "" : "s")}"
        : MessageCountText;
    public string DraftCountText => Drafts.Count == 0 ? "Drafts" : $"Drafts ({Drafts.Count:N0})";
    public bool HasDrafts => Drafts.Count > 0;
    public bool HasPeopleErrors => !string.IsNullOrWhiteSpace(PeopleErrorText);
    public bool IsEditingContact => _editingContact is not null;
    public bool IsCreatingContact => IsContactEditorOpen && _editingContact is null;
    public bool IsContactEditorOpen
    {
        get => _isContactEditorOpen;
        private set
        {
            if (SetProperty(ref _isContactEditorOpen, value))
            {
                RaisePropertyChanged(nameof(IsContactPaneOpen));
                RaisePropertyChanged(nameof(IsCreatingContact));
                RaisePropertyChanged(nameof(ContactEditorTitle));
            }
        }
    }
    public bool IsConfirmingContactDelete => _pendingDeleteContact is not null;
    public bool IsContactPaneOpen => IsContactEditorOpen || IsConfirmingContactDelete;
    public string ContactEditorTitle => IsCreatingContact ? "New contact" : "Edit contact";
    public MailAccount? SelectedContactAccount
    {
        get => _selectedContactAccount;
        set
        {
            if (SetProperty(ref _selectedContactAccount, value))
            {
                ((AsyncCommand)SaveContactCommand).Refresh();
            }
        }
    }
    public string ContactDeleteText => _pendingDeleteContact is null
        ? ""
        : $"Delete {_pendingDeleteContact.DisplayName} from {_pendingDeleteContact.ProvenanceText}?";

    public string ContactName
    {
        get => _contactName;
        set => SetProperty(ref _contactName, value);
    }

    public string ContactEmails
    {
        get => _contactEmails;
        set => SetProperty(ref _contactEmails, value);
    }

    public string PeopleErrorText
    {
        get => _peopleErrorText;
        private set
        {
            if (SetProperty(ref _peopleErrorText, value))
            {
                RaisePropertyChanged(nameof(HasPeopleErrors));
            }
        }
    }
    public string CurrentFolderName => IsSearchResultsView ? "Search results" : IsDraftsView ? "Drafts" : _selectedFolder?.DisplayName ?? "Inbox";
    public double MessageRowHeight => IsCompact ? 58 : 78;
    public Avalonia.Thickness MessageRowMargin => IsCompact
        ? new(10, 2, 9, 3)
        : new(12, 8, 11, 9);
    public string SenderInitials => Initials(SelectedMessage?.SenderDisplayName);
    public string SelectedSenderName => SelectedMessage?.SenderDisplayName ?? "";
    public string SelectedSenderAddress => SelectedMessage?.From.Address ?? "";
    public string SelectedRecipientsText => SelectedMessage is null
        ? ""
        : $"To: {string.Join(", ", SelectedMessage.To.Select(static recipient => string.IsNullOrWhiteSpace(recipient.Name) ? recipient.Address : recipient.Name))}";
    public string SelectedMessageTime => SelectedMessage?.ReceivedAt.ToLocalTime().ToString("dddd, MMMM d, yyyy, h:mm tt") ?? "";
    public string ToggleReadText => SelectedMessage?.IsRead == true ? "Mark unread" : "Mark read";
    public string ToggleFlagText => SelectedMessage?.IsFlagged == true ? "Clear flag" : "Flag";
    public string SyncIcon => IsSyncing ? SyncFrames[_syncFrame] : "↻";
    public string SyncButtonText => $"{SyncIcon} Sync";
    public bool HasSelectedAttachments => SelectedMessage?.HasAttachments == true;
    public string AttachmentSummary => IsLoadingAttachments
        ? "Loading attachments…"
        : Attachments.Count == 0 ? "Attachments" : $"{Attachments.Count:N0} attachment{(Attachments.Count == 1 ? "" : "s")}";

    public bool IsLoadingAttachments
    {
        get => _isLoadingAttachments;
        private set
        {
            if (SetProperty(ref _isLoadingAttachments, value))
            {
                RaisePropertyChanged(nameof(AttachmentSummary));
            }
        }
    }

    public string ActiveModule
    {
        get => _activeModule;
        private set
        {
            if (!SetProperty(ref _activeModule, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(IsMailModule));
            RaisePropertyChanged(nameof(IsWorkspaceModule));
            RaisePropertyChanged(nameof(IsCalendarModule));
            RaisePropertyChanged(nameof(IsGenericWorkspaceModule));
            RaisePropertyChanged(nameof(IsContactsModule));
            RaisePropertyChanged(nameof(IsTasksModule));
            RaisePropertyChanged(nameof(IsFilesModule));
            RaisePropertyChanged(nameof(IsNotesModule));
            RaisePropertyChanged(nameof(ShowWorkspaceSearch));
            RaisePropertyChanged(nameof(ShowWorkspaceRefresh));
            RaisePropertyChanged(nameof(ActiveWorkspace));
            RaisePropertyChanged(nameof(IsWorkspaceEmpty));
            RaisePropertyChanged(nameof(ShowMessageBody));
            RaisePropertyChanged(nameof(ShowMailSurface));
            RaisePropertyChanged(nameof(ShowWorkspaceSurface));
            ((AsyncCommand)ReplyCommand).Refresh();
            ((AsyncCommand)ReplyAllCommand).Refresh();
            ((AsyncCommand)ForwardCommand).Refresh();
            RefreshMailActionCommands();
        }
    }

    public string ModuleSearchText
    {
        get => _moduleSearchText;
        set => SetProperty(ref _moduleSearchText, value);
    }

    public bool IsWorkspaceLoading
    {
        get => _isWorkspaceLoading;
        private set
        {
            if (SetProperty(ref _isWorkspaceLoading, value))
            {
                RaisePropertyChanged(nameof(IsWorkspaceEmpty));
            }
        }
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        private set
        {
            if (SetProperty(ref _isSyncing, value))
            {
                RaisePropertyChanged(nameof(SyncIcon));
                RaisePropertyChanged(nameof(SyncButtonText));
                ((AsyncCommand)SyncCommand).Refresh();
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                RaisePropertyChanged(nameof(SearchResultSummary));
                RaisePropertyChanged(nameof(HasSearchText));
                _ = StartGlobalSearchAsync(debounce: true);
            }
        }
    }

    public string SelectedSearchScope
    {
        get => _selectedSearchScope;
        set
        {
            if (SetProperty(ref _selectedSearchScope, value))
            {
                RaisePropertyChanged(nameof(ShowSearchFolderFilter));
                RaisePropertyChanged(nameof(ShowMailSearchOptions));
                RaisePropertyChanged(nameof(IsMailSearchScope));
                if (IsGlobalSearchOpen)
                {
                    _ = StartGlobalSearchAsync(debounce: false, forceOpen: true);
                }
            }
        }
    }

    public SearchAccountFilter? SelectedSearchAccountFilter
    {
        get => _selectedSearchAccountFilter;
        set
        {
            if (SetProperty(ref _selectedSearchAccountFilter, value) && IsGlobalSearchOpen)
            {
                _ = StartGlobalSearchAsync(debounce: false, forceOpen: true);
            }
        }
    }

    public SearchFolderFilter? SelectedSearchFolderFilter
    {
        get => _selectedSearchFolderFilter;
        set
        {
            if (SetProperty(ref _selectedSearchFolderFilter, value) && IsGlobalSearchOpen)
            {
                _ = StartGlobalSearchAsync(debounce: false, forceOpen: true);
            }
        }
    }

    public bool IsMailSearchScope => SelectedSearchScope == "Mail";
    public bool ShowSearchFolderFilter => IsMailSearchScope;
    public bool ShowMailSearchOptions => SelectedSearchScope is "Everything" or "Mail";
    public bool IncludeArchivedMailInSearch
    {
        get => _includeArchivedMailInSearch;
        set
        {
            if (!SetProperty(ref _includeArchivedMailInSearch, value))
            {
                return;
            }
            RebuildSearchFilters();
            if (IsGlobalSearchOpen)
            {
                _ = StartGlobalSearchAsync(debounce: false, forceOpen: true);
            }
        }
    }
    public bool IsSearchResultsView
    {
        get => _isSearchResultsView;
        private set
        {
            if (SetProperty(ref _isSearchResultsView, value))
            {
                RaisePropertyChanged(nameof(CurrentFolderName));
                RaisePropertyChanged(nameof(IsUnifiedInbox));
                RaisePropertyChanged(nameof(CurrentItemCountText));
                RaisePropertyChanged(nameof(SearchResultSummary));
            }
        }
    }
    public string SearchResultSummary => $"Showing messages for ‘{SearchText.Trim()}’";

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);

    public bool IsGlobalSearchOpen
    {
        get => _isGlobalSearchOpen;
        set
        {
            if (SetProperty(ref _isGlobalSearchOpen, value))
            {
                _dismissedSearchText = value ? null : SearchText.Trim();
                ((AsyncCommand)ReplyCommand).Refresh();
                ((AsyncCommand)ReplyAllCommand).Refresh();
                ((AsyncCommand)ForwardCommand).Refresh();
                RefreshMailActionCommands();
            }
        }
    }

    public bool IsGlobalSearchRunning
    {
        get => _isGlobalSearchRunning;
        private set => SetProperty(ref _isGlobalSearchRunning, value);
    }

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set
        {
            if (SetProperty(ref _isSettingsOpen, value))
            {
                RaisePropertyChanged(nameof(ShowMessageBody));
                RaisePropertyChanged(nameof(ShowMailSurface));
                RaisePropertyChanged(nameof(ShowWorkspaceSurface));
                ((AsyncCommand)ReplyCommand).Refresh();
                ((AsyncCommand)ReplyAllCommand).Refresh();
                ((AsyncCommand)ForwardCommand).Refresh();
                RefreshMailActionCommands();
            }
        }
    }

    public bool IsConfirmingAccountRemoval
    {
        get => _isConfirmingAccountRemoval;
        private set => SetProperty(ref _isConfirmingAccountRemoval, value);
    }

    public string RemovalAccountText => _pendingRemovalAccount is null
        ? ""
        : $"Remove {_pendingRemovalAccount.EmailAddress} and its cached mail from this device?";

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsMailActionRunning => _isMailActionRunning;

    public string MailActionStatus
    {
        get => _mailActionStatus;
        private set => SetProperty(ref _mailActionStatus, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                var version = ++_errorVersion;
                RaisePropertyChanged(nameof(HasError));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _ = DismissErrorAfterDelayAsync(value, version);
                }
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public void DismissError() => Error = null;

    private async Task DismissErrorAfterDelayAsync(string error, int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (_errorVersion == version && Error == error)
        {
            Error = null;
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(ShowEmptyState));
                RaisePropertyChanged(nameof(ShowMessageBody));
                RaisePropertyChanged(nameof(ShowFullScreenLoader));
            }
        }
    }

    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (SetProperty(ref _isCompact, value))
            {
                RaisePropertyChanged(nameof(MessageRowHeight));
                RaisePropertyChanged(nameof(MessageRowMargin));
            }
        }
    }

    public bool DesktopNotificationsEnabled
    {
        get => _desktopNotificationsEnabled;
        set => SetProperty(ref _desktopNotificationsEnabled, value);
    }

    public string MailSyncRange
    {
        get => _mailSyncRange;
        set
        {
            var normalized = MailSyncRanges.Contains(value) ? value : "All mail";
            if (SetProperty(ref _mailSyncRange, normalized) && _initializationComplete)
            {
                _ = SyncAsync();
            }
        }
    }

    private int MailSyncHistoryDays => MailSyncRange switch
    {
        "1 month" => 30,
        "3 months" => 90,
        "6 months" => 180,
        "1 year" => 365,
        _ => 0
    };

    public string SelectedThemeMode
    {
        get => _selectedThemeMode;
        set
        {
            if (SetProperty(ref _selectedThemeMode, value))
            {
                _renderer.ThemeMode = value;
                ConversationThread.RefreshTheme();
                RaisePropertyChanged(nameof(SelectedSignatureTemplatePreviewUri));
                _applyTheme(value);
            }
        }
    }

    public string SelectedAccentName
    {
        get => _selectedAccentName;
        set
        {
            if (SetProperty(ref _selectedAccentName, value) && Accents.TryGetValue(value, out var color))
            {
                _applyAccent(color);
            }
        }
    }

    public SettingsTabItem? SelectedSettingsTab
    {
        get => _selectedSettingsTab;
        set
        {
            if (SetProperty(ref _selectedSettingsTab, value))
            {
                RaisePropertyChanged(nameof(ShowAppearanceSettings));
                RaisePropertyChanged(nameof(ShowMailSettings));
                RaisePropertyChanged(nameof(ShowSignatureSettings));
                RaisePropertyChanged(nameof(ShowAccountSettings));
                RaisePropertyChanged(nameof(ShowAboutSettings));
            }
        }
    }

    public bool ShowAppearanceSettings => SelectedSettingsTab?.Name == "Appearance";
    public bool ShowMailSettings => SelectedSettingsTab?.Name == "Mail & notifications";
    public bool ShowSignatureSettings => SelectedSettingsTab?.Name == "Signatures";
    public bool ShowAccountSettings => SelectedSettingsTab?.Name == "Accounts";
    public bool ShowAboutSettings => SelectedSettingsTab?.Name == "About";

    public SignatureItem? SelectedSignature
    {
        get => _selectedSignature;
        set
        {
            if (_isCreatingSignature && _selectedSignature is { } draft && value != draft)
            {
                _isCreatingSignature = false;
                Signatures.Remove(draft);
            }
            if (SetProperty(ref _selectedSignature, value))
            {
                LoadSelectedSignatureEditor();
                RaisePropertyChanged(nameof(CanEditSelectedSignature));
                RefreshSignatureEditorState();
            }
        }
    }

    public bool CanEditSelectedSignature => SelectedSignature?.CanEdit == true;
    public bool HasUnsavedSignatureChanges =>
        CanEditSelectedSignature &&
        (_isCreatingSignature ||
         !string.Equals(SignatureEditorName, SelectedSignature?.Name, StringComparison.Ordinal) ||
         !string.Equals(SignatureEditorHtml, SelectedSignature?.Html, StringComparison.Ordinal));
    public bool CanManageSavedSignature =>
        SelectedSignature is not null && !HasUnsavedSignatureChanges && !IsConfirmingSignatureDelete;
    public bool CanDeleteSelectedSignature =>
        CanEditSelectedSignature && CanManageSavedSignature;
    public bool IsConfirmingSignatureDelete => _pendingSignatureDelete is not null;
    public string SignatureDeleteText
    {
        get
        {
            if (_pendingSignatureDelete is not { } signature)
            {
                return "";
            }
            var usageCount = SignatureUsageCount(signature);
            return usageCount == 0
                ? $"Delete “{signature.Name}”? This cannot be undone."
                : $"Delete “{signature.Name}”? It is used by {usageCount} mailbox{(usageCount == 1 ? "" : "es")}; those choices will be set to None.";
        }
    }

    public SignatureTemplate? SelectedSignatureTemplate
    {
        get => _selectedSignatureTemplate;
        set
        {
            if (SetProperty(ref _selectedSignatureTemplate, value))
            {
                RaisePropertyChanged(nameof(SelectedSignatureTemplatePreviewUri));
                ((AsyncCommand)CreateSignatureFromTemplateCommand).Refresh();
            }
        }
    }

    public Uri SelectedSignatureTemplatePreviewUri
    {
        get
        {
            var content = string.IsNullOrWhiteSpace(SelectedSignatureTemplate?.Html)
                ? "<p style=\"font:14px 'Segoe UI',Arial,sans-serif;color:#666\">Blank signature</p>"
                : SelectedSignatureTemplate.Html;
            return _renderer.Render(
                $"<html><body style=\"margin:0;background:#fff;color:#1b1b1b\">{content}</body></html>",
                isHtml: true);
        }
    }

    public string SignatureEditorName
    {
        get => _signatureEditorName;
        set
        {
            if (SetProperty(ref _signatureEditorName, value))
            {
                SignatureEditorChanged();
            }
        }
    }

    public string SignatureEditorHtml
    {
        get => _signatureEditorHtml;
        set
        {
            if (SetProperty(ref _signatureEditorHtml, value))
            {
                SignatureEditorChanged();
            }
        }
    }

    public string? SignatureEditorError
    {
        get => _signatureEditorError;
        private set
        {
            if (SetProperty(ref _signatureEditorError, value))
            {
                RaisePropertyChanged(nameof(HasSignatureEditorError));
            }
        }
    }

    public bool HasSignatureEditorError => !string.IsNullOrWhiteSpace(SignatureEditorError);

    public bool IsSignatureTemplatePickerOpen
    {
        get => _isSignatureTemplatePickerOpen;
        private set => SetProperty(ref _isSignatureTemplatePickerOpen, value);
    }

    public string? DefaultSenderMailboxId => _defaultSenderMailboxId;
    public int SenderPreferencesVersion => _senderPreferencesVersion;
    public int MailQuickActionsVersion => _mailQuickActionsVersion;

    public void ConfigureMailQuickActions(IReadOnlyList<string>? actionIds)
    {
        _configuringMailQuickActions = true;
        for (var index = 0; index < MailQuickActionSlots.Count; index++)
        {
            var id = actionIds is not null && index < actionIds.Count
                ? actionIds[index]
                : DefaultMailQuickActionIds[index];
            MailQuickActionSlots[index].SelectedOption =
                AvailableMailQuickActions.FirstOrDefault(action => action.Id == id) ??
                AvailableMailQuickActions.First(action => action.Id == DefaultMailQuickActionIds[index]);
        }
        _configuringMailQuickActions = false;
        RebuildMailQuickActions();
    }

    public List<string> GetMailQuickActionPreferences() =>
        MailQuickActionSlots.Select(static slot => slot.SelectedOption.Id).ToList();

    private void MailQuickActionChanged()
    {
        if (_configuringMailQuickActions)
        {
            return;
        }
        RebuildMailQuickActions();
        _mailQuickActionsVersion++;
        RaisePropertyChanged(nameof(MailQuickActionsVersion));
    }

    private void RebuildMailQuickActions()
    {
        Replace(MailQuickActions, MailQuickActionSlots
            .Select(static slot => slot.SelectedOption)
            .Where(static action => action.Id != "none"));
        RaisePropertyChanged(nameof(HasMailQuickActions));
    }

    public async Task InitializeAsync()
    {
        if (_store is null)
        {
            Status = "Storage unavailable";
            _initializationComplete = true;
            RaisePropertyChanged(nameof(ShowOnboarding));
            RaisePropertyChanged(nameof(ShowFullScreenLoader));
            return;
        }

        await RunBusyAsync("Loading mailbox…", async () =>
        {
            await _store.InitializeAsync();
            Replace(Accounts, await _store.GetAccountsAsync());
            Replace(Mailboxes, await _store.GetMailboxesAsync());
            Replace(Drafts, await _store.GetLocalDraftsAsync());
            RaiseDraftState();
            foreach (var account in Accounts.Where(account => Mailboxes.All(mailbox => mailbox.AccountId != account.AccountId || mailbox.IsShared)))
            {
                var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
                await _store.SaveMailboxAsync(primary);
                Mailboxes.Add(primary);
            }
            RebuildSenderSettings();
            await LoadFoldersAsync();
            await PrimeNewMailNotificationsAsync();
            await LoadMessagesAsync();

            try
            {
                if (_provider is null)
                {
                    var options = Microsoft365Options.Create(_dataDirectory);
                    _authentication = await Microsoft365AuthService.CreateAsync(options);
                    _provider = new Microsoft365MailProvider(_authentication);
                    _workspaceProvider = new Microsoft365WorkspaceProvider(_authentication);
                }
                ((AsyncCommand)SyncCommand).Refresh();
                ((AsyncCommand)ToggleReadCommand).Refresh();
                RefreshWorkspaceCommands();
                await LoadAttachmentsAsync(SelectedMessage);
                _ = RepairMissingSubjectsAsync(Messages);
            }
            catch (InvalidOperationException exception)
            {
                Error = exception.Message;
            }
        });
        RaisePropertyChanged(nameof(SettingsAccounts));
        _initializationComplete = true;
        RaisePropertyChanged(nameof(HasAccounts));
        RaisePropertyChanged(nameof(ShowOnboarding));
        RaisePropertyChanged(nameof(ShowFullScreenLoader));
        StartAutoSync();
        _ = ReconcileAllDraftsInBackgroundAsync();
    }

    private async Task ConnectAsync()
    {
        if (_store is null)
        {
            return;
        }

        MailAccount? connectedAccount = null;
        await RunBusyAsync("Opening Microsoft sign-in…", async () =>
        {
            _authentication ??= await Microsoft365AuthService.CreateAsync(Microsoft365Options.Create(_dataDirectory));
            _provider ??= new Microsoft365MailProvider(_authentication);
            _workspaceProvider ??= new Microsoft365WorkspaceProvider(_authentication);
            StartAutoSync();
            var account = await _authentication.SignInAsync();
            await _store.SaveAccountAsync(account);
            var primaryMailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await _store.SaveMailboxAsync(primaryMailbox);

            var existing = Accounts.FirstOrDefault(candidate => candidate.AccountId == account.AccountId);
            if (existing is not null)
            {
                Accounts.Remove(existing);
            }

            Accounts.Add(account);
            if (Mailboxes.All(mailbox => mailbox.Id != primaryMailbox.Id))
            {
                Mailboxes.Add(primaryMailbox);
            }
            RebuildSenderSettings();
            await RefreshOwnedWorkspaceAccountsIfCreatedAsync();
            ((AsyncCommand)SyncCommand).Refresh();
            ((AsyncCommand)ToggleReadCommand).Refresh();
            RefreshWorkspaceCommands();
            RaisePropertyChanged(nameof(HasAccounts));
            RaisePropertyChanged(nameof(ShowOnboarding));
            RaisePropertyChanged(nameof(SettingsAccounts));
            connectedAccount = account;
        });
        if (connectedAccount is not null)
        {
            _ = SyncAfterConnectAsync();
        }
    }

    private async Task SyncAfterConnectAsync()
    {
        try
        {
            await SyncAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            Status = "Initial sync failed";
        }
    }

    private async Task OpenSettingsAsync()
    {
        IsSettingsOpen = true;
        await LoadMailStatisticsAsync();
    }

    private async Task LoadMailStatisticsAsync()
    {
        if (_store is null || IsLoadingMailStatistics)
        {
            return;
        }

        IsLoadingMailStatistics = true;
        try
        {
            var statistics = new List<MailboxStatisticsItem>();
            foreach (var mailbox in Mailboxes)
            {
                var local = await _store.GetMessageCountsAsync(mailbox.Id);
                var folders = Folders.Where(folder => folder.MailboxId == mailbox.Id).ToArray();
                statistics.Add(new(
                    mailbox.DisplayName,
                    mailbox.Address,
                    mailbox.IsShared,
                    local.Total,
                    folders.Sum(static folder => folder.TotalCount),
                    local.Unread,
                    folders.Sum(static folder => folder.UnreadCount),
                    local.Flagged,
                    folders.Length));
            }
            Replace(MailStatistics, statistics);
            RaisePropertyChanged(nameof(HasMailStatistics));
            RaisePropertyChanged(nameof(MailStatisticsSummary));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"Mail statistics could not be loaded: {exception.Message}";
        }
        finally
        {
            IsLoadingMailStatistics = false;
        }
    }

    private Task CloseSettingsAsync()
    {
        _pendingRemovalAccount = null;
        IsConfirmingAccountRemoval = false;
        IsSettingsOpen = false;
        return Task.CompletedTask;
    }

    private Task RequestRemoveAccountAsync(MailAccount account)
    {
        _pendingRemovalAccount = account;
        RaisePropertyChanged(nameof(RemovalAccountText));
        IsConfirmingAccountRemoval = true;
        ((AsyncCommand)ConfirmRemoveAccountCommand).Refresh();
        return Task.CompletedTask;
    }

    private Task CancelRemoveAccountAsync()
    {
        _pendingRemovalAccount = null;
        RaisePropertyChanged(nameof(RemovalAccountText));
        IsConfirmingAccountRemoval = false;
        ((AsyncCommand)ConfirmRemoveAccountCommand).Refresh();
        return Task.CompletedTask;
    }

    private async Task RemovePendingAccountAsync()
    {
        var account = _pendingRemovalAccount;
        if (account is null || _store is null)
        {
            return;
        }

        await RunBusyAsync("Removing account…", async () =>
        {
            if (_authentication is not null)
            {
                await _authentication.SignOutAsync(account.AccountId);
            }

            await _store.DeleteAccountAsync(account.ProviderId, account.AccountId);
            Accounts.Remove(account);
            _driveAttachmentCancellation?.Cancel();
            await RefreshOwnedWorkspaceAccountsIfCreatedAsync();
            foreach (var draft in Drafts.Where(candidate => candidate.AccountId == account.AccountId).ToArray())
            {
                Drafts.Remove(draft);
            }
            RaiseDraftState();
            foreach (var mailbox in Mailboxes.Where(candidate => candidate.AccountId == account.AccountId).ToArray())
            {
                _mailboxSignatures.Remove(mailbox.Id);
                Mailboxes.Remove(mailbox);
            }
            RebuildSenderSettings();
            RaiseSenderPreferencesChanged();

            _pendingRemovalAccount = null;
            SelectedMessage = null;
            IsConfirmingAccountRemoval = false;
            RaisePropertyChanged(nameof(RemovalAccountText));
            RaisePropertyChanged(nameof(SharedMailboxes));
            RaisePropertyChanged(nameof(SettingsAccounts));
            RaisePropertyChanged(nameof(HasAccounts));
            RaisePropertyChanged(nameof(ShowOnboarding));
            ((AsyncCommand)SyncCommand).Refresh();
            ((AsyncCommand)ConfirmRemoveAccountCommand).Refresh();
            if (_selectedFolder is not null && _selectedFolder.MailboxId.StartsWith(account.AccountId + ":", StringComparison.Ordinal))
            {
                _selectedFolder = null;
                RaisePropertyChanged(nameof(CurrentFolderName));
            }
            await LoadFoldersAsync();
            await LoadMessagesAsync();

            if (!HasAccounts)
            {
                IsSettingsOpen = false;
            }
        });
    }

    private async Task ReauthenticateAccountAsync(MailAccount account)
    {
        if (_store is null)
        {
            return;
        }

        await RunBusyAsync($"Re-authenticating {account.EmailAddress}...", async () =>
        {
            _authentication ??= await Microsoft365AuthService.CreateAsync(Microsoft365Options.Create(_dataDirectory));
            var refreshed = await _authentication.ReauthenticateAsync(account.AccountId);
            await _store.SaveAccountAsync(refreshed);
            var index = Accounts.IndexOf(account);
            if (index >= 0)
            {
                Accounts[index] = refreshed;
            }
            await RefreshOwnedWorkspaceAccountsIfCreatedAsync();
            RebuildSenderSettings();
            RaisePropertyChanged(nameof(SettingsAccounts));
        });
    }

    private async Task SyncAsync()
    {
        if (IsBusy || _provider is null || Accounts.Count == 0 ||
            Interlocked.CompareExchange(ref _syncRunning, 1, 0) != 0)
        {
            return;
        }

        IsSyncing = true;
        Status = "Syncing Microsoft 365…";
        Error = null;
        var animation = AnimateSyncIconAsync();
        Exception? mailFailure = null;
        try
        {
            try
            {
                foreach (var account in Accounts.ToArray())
                {
                    await SyncAccountAsync(account);
                }

                await LoadFoldersAsync();
                if (!IsGlobalSearchOpen && !IsSearchResultsView)
                {
                    await LoadMessagesAsync();
                }
                if (SelectedMessage is { } selected)
                {
                    await LoadConversationAsync(selected, _selectionVersion, CancellationToken.None);
                }
                if (IsSettingsOpen)
                {
                    await LoadMailStatisticsAsync();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                mailFailure = exception;
            }

            await ReconcileAllDraftsAsync();
            _ = RefreshWorkspaceCacheAsync();
            if (mailFailure is not null)
            {
                Error = mailFailure.Message;
                Status = "Sync failed";
            }
            else
            {
                Status = "Up to date";
            }
        }
        finally
        {
            Interlocked.Exchange(ref _syncRunning, 0);
            IsSyncing = false;
            await animation;
        }
    }

    private void StartAutoSync()
    {
        if (_autoSyncStarted)
        {
            return;
        }

        _autoSyncStarted = true;
        _ = AutoSyncAsync();
    }

    private async Task AutoSyncAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync())
        {
            if (!IsBusy && !IsSyncing && Accounts.Count > 0 && _provider is not null)
            {
                await SyncAsync();
            }
        }
    }

    private async Task AnimateSyncIconAsync()
    {
        while (IsSyncing)
        {
            await Task.Delay(90);
            _syncFrame = (_syncFrame + 1) % SyncFrames.Length;
            RaisePropertyChanged(nameof(SyncIcon));
            RaisePropertyChanged(nameof(SyncButtonText));
        }
    }

    private async Task RefreshWorkspaceCacheAsync()
    {
        if (_store is null || _workspaceProvider is null ||
            DateTimeOffset.UtcNow - _lastWorkspaceSyncAt < TimeSpan.FromMinutes(15) ||
            Interlocked.CompareExchange(ref _workspaceSyncRunning, 1, 0) != 0)
        {
            return;
        }

        _lastWorkspaceSyncAt = DateTimeOffset.UtcNow;
        try
        {
            foreach (var account in Accounts.ToArray())
            {
                await RefreshContactsAsync(account);
                await RefreshCalendarsAsync(account);
                await RefreshTasksAsync(account);
                await RefreshNotesAsync(account);
                await _store.GarbageCollectWorkspaceAsync(account.AccountId);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _workspaceSyncRunning, 0);
        }

        async Task RefreshContactsAsync(MailAccount account)
        {
            try
            {
                var contacts = await _workspaceProvider.SearchContactsAsync(account, "");
                await _store.ReplaceWorkspaceItemsAsync(
                    "contact", account.AccountId, "all", contacts,
                    static item => item.ProviderId,
                    static item => $"{item.DisplayName} {string.Join(' ', item.EmailAddresses)}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }

        async Task RefreshCalendarsAsync(MailAccount account)
        {
            try
            {
                var calendars = await _workspaceProvider.GetCalendarsAsync(account);
                await _store.ReplaceWorkspaceItemsAsync(
                    "calendar", account.AccountId, "all", calendars,
                    static item => item.ProviderId,
                    static item => $"{item.Name} {item.Color}");
                var from = DateTimeOffset.UtcNow.AddYears(-1);
                var to = DateTimeOffset.UtcNow.AddYears(2);
                foreach (var calendar in calendars)
                {
                    var events = await _workspaceProvider.GetEventsAsync(
                        account, calendar.ProviderId, from, to);
                    await _store.ReplaceCalendarEventsAsync(
                        account.AccountId, calendar.ProviderId, from, to, events);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }

        async Task RefreshTasksAsync(MailAccount account)
        {
            try
            {
                var lists = await _workspaceProvider.GetTaskListsAsync(account);
                await _store.ReplaceWorkspaceItemsAsync(
                    "task-list", account.AccountId, "all", lists,
                    static item => item.ProviderId,
                    static item => item.DisplayName);
                foreach (var list in lists)
                {
                    var tasks = await _workspaceProvider.GetTasksAsync(account, list);
                    await _store.ReplaceWorkspaceItemsAsync(
                        "task", account.AccountId, list.ProviderId, tasks,
                        static item => item.ProviderId,
                        static item => $"{item.Title} {item.Notes} {string.Join(' ', item.Categories ?? [])}");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }

        async Task RefreshNotesAsync(MailAccount account)
        {
            try
            {
                var notebooks = await _workspaceProvider.GetNotebooksAsync(account);
                await _store.ReplaceWorkspaceItemsAsync(
                    "note-notebook", account.AccountId, "all", notebooks,
                    static item => item.ProviderId,
                    static item => item.Name);
                var notes = new List<NoteInfo>();
                foreach (var notebook in notebooks)
                {
                    var sections = await _workspaceProvider.GetSectionsAsync(account, notebook);
                    await _store.ReplaceWorkspaceItemsAsync(
                        "note-section", account.AccountId, notebook.ProviderId, sections,
                        static item => item.ProviderId,
                        static item => item.Name);
                    foreach (var section in sections)
                    {
                        var pages = await _workspaceProvider.GetPagesAsync(account, section);
                        await _store.ReplaceWorkspaceItemsAsync(
                            "note-page", account.AccountId, section.ProviderId, pages,
                            static item => item.ProviderId,
                            static item => item.Title);
                        notes.AddRange(pages.Select(page => new NoteInfo(
                            page.ProviderId, page.Title, page.ModifiedAt, page.WebUrl,
                            page.AccountId, page.AccountProviderId, page.SectionProviderId)));
                    }
                }
                await _store.ReplaceWorkspaceItemsAsync(
                    "note", account.AccountId, "all", notes,
                    static item => item.ProviderId,
                    static item => item.Title);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }
    }

    private async Task SyncAccountAsync(MailAccount account)
    {
        if (_provider is null || _store is null)
        {
            return;
        }

        var engine = new SyncEngine(_provider, _store);
        foreach (var mailbox in Mailboxes.Where(mailbox => mailbox.AccountId == account.AccountId))
        {
            var folders = await _provider.GetFoldersAsync(account, mailbox);
            await _store.SaveFoldersAsync(mailbox.Id, folders);
            foreach (var folder in folders.Where(static folder => folder.TotalCount > 0))
            {
                InboxNotificationContext? notificationContext = null;
                var notifyThisCycle = false;
                if (folder.WellKnownName?.Equals("inbox", StringComparison.OrdinalIgnoreCase) == true)
                {
                    notificationContext = new InboxNotificationContext(account, mailbox, folder);
                    notifyThisCycle = _newMailNotifications.IsPrimed(notificationContext);
                    if (!notifyThisCycle)
                    {
                        _newMailNotifications.Prime(
                            notificationContext,
                            await _store.GetMessagesAsync(mailbox.Id, folder.ProviderId));
                    }
                }
                await engine.SyncFolderAsync(account, mailbox, folder, MailSyncHistoryDays);
                if (notificationContext is not null)
                {
                    var synced = await _store.GetMessagesAsync(mailbox.Id, folder.ProviderId);
                    if (notifyThisCycle)
                    {
                        _newMailNotifications.Observe(
                            notificationContext,
                            synced,
                            DesktopNotificationsEnabled);
                    }
                    else
                    {
                        _newMailNotifications.Prime(notificationContext, synced);
                    }
                }
            }
        }
    }

    private async Task<IReadOnlyList<string>> ReconcileAllDraftsAsync()
    {
        if (_provider?.SupportsCloudDrafts != true || _store is null)
        {
            return [];
        }

        var issues = new List<string>();
        foreach (var account in Accounts.ToArray())
        {
            foreach (var mailbox in Mailboxes.Where(candidate => candidate.AccountId == account.AccountId).ToArray())
            {
                var issue = await ReconcileDraftMailboxAsync(account, mailbox);
                if (issue is not null)
                {
                    issues.Add(issue);
                }
            }
        }
        await RefreshDraftsAsync();
        return issues;
    }

    private async Task ReconcileAllDraftsInBackgroundAsync()
    {
        try
        {
            await ReconcileAllDraftsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Drafts remain local when cloud reconciliation fails; do not interrupt the user.
        }
    }

    private async Task<string?> ReconcileDraftMailboxAsync(MailAccount account, Mailbox mailbox)
    {
        if (_provider?.SupportsCloudDrafts != true || _store is null)
        {
            return null;
        }

        var mailboxLock = _draftSyncLocks.GetOrAdd(mailbox.Id, static _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync();
        try
        {
            var result = await new DraftSynchronizationService(_provider, _store)
                .SynchronizeAsync(account, mailbox);
            var issue = result.Items.FirstOrDefault(static item =>
                item.Status is DraftSyncStatus.Conflict or
                    DraftSyncStatus.MissingRemote or
                    DraftSyncStatus.Failed);
            return issue is null
                ? null
                : $"{mailbox.Address}: {issue.Error ?? issue.Status.ToString()}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"{mailbox.Address}: {exception.Message}";
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    private async Task RefreshDraftsAsync()
    {
        if (_store is null)
        {
            return;
        }
        Replace(Drafts, await _store.GetLocalDraftsAsync());
        RaiseDraftState();
    }

    private async Task PrimeNewMailNotificationsAsync()
    {
        if (_store is null)
        {
            return;
        }
        foreach (var folder in Folders.Where(folder =>
                     folder.WellKnownName?.Equals("inbox", StringComparison.OrdinalIgnoreCase) == true))
        {
            var mailbox = Mailboxes.FirstOrDefault(candidate => candidate.Id == folder.MailboxId);
            var account = mailbox is null
                ? null
                : Accounts.FirstOrDefault(candidate => candidate.AccountId == mailbox.AccountId);
            if (mailbox is null || account is null)
            {
                continue;
            }
            var context = new InboxNotificationContext(account, mailbox, folder.Folder);
            _newMailNotifications.Prime(
                context,
                await _store.GetMessagesAsync(mailbox.Id, folder.ProviderId));
        }
    }

    public async Task AddSharedMailboxAsync(MailAccount account, string address, string permissionMode)
    {
        if (_provider is not ISharedMailboxProvider sharedMailboxProvider || _store is null)
        {
            throw new InvalidOperationException("Connect to Microsoft 365 before adding a shared mailbox.");
        }

        var mailbox = await sharedMailboxProvider.ValidateSharedMailboxAsync(account, address);
        mailbox = permissionMode switch
        {
            "Send As" => mailbox with { CanSendAs = true },
            "Send on behalf" => mailbox with { CanSendOnBehalf = true },
            _ => mailbox
        };
        await _store.SaveMailboxAsync(mailbox);
        var existing = Mailboxes.FirstOrDefault(candidate => candidate.Id == mailbox.Id);
        if (existing is not null)
        {
            Mailboxes.Remove(existing);
        }

        Mailboxes.Add(mailbox);
        RebuildSenderSettings();
        RaisePropertyChanged(nameof(SharedMailboxes));
        RaisePropertyChanged(nameof(SettingsAccounts));
        var folders = await _provider.GetFoldersAsync(account, mailbox);
        await _store.SaveFoldersAsync(mailbox.Id, folders);
        var engine = new SyncEngine(_provider, _store);
        foreach (var folder in folders.Where(static folder => folder.TotalCount > 0))
        {
            await engine.SyncFolderAsync(account, mailbox, folder, MailSyncHistoryDays);
            if (folder.WellKnownName?.Equals("inbox", StringComparison.OrdinalIgnoreCase) == true)
            {
                var context = new InboxNotificationContext(account, mailbox, folder);
                _newMailNotifications.Prime(
                    context,
                    await _store.GetMessagesAsync(mailbox.Id, folder.ProviderId));
            }
        }
        await LoadFoldersAsync();
        await LoadMessagesAsync();
    }

    private Task RequestSharedMailboxAsync(MailAccount account)
    {
        SharedMailboxRequested?.Invoke(account);
        return Task.CompletedTask;
    }

    private async Task StartGlobalSearchAsync(bool debounce, bool forceOpen = false)
    {
        _globalSearchCancellation?.Cancel();
        _globalSearchCancellation?.Dispose();
        _globalSearchCancellation = new CancellationTokenSource();
        var source = _globalSearchCancellation;
        var query = SearchText.Trim();
        GlobalSearchResults.Clear();
        if (_store is null || query.Length < 2)
        {
            ClearLatestMailSearchResults();
            IsGlobalSearchRunning = false;
            IsGlobalSearchOpen = false;
            return;
        }
        if (!forceOpen && string.Equals(_dismissedSearchText, query, StringComparison.Ordinal))
        {
            IsGlobalSearchRunning = false;
            return;
        }

        IsGlobalSearchRunning = true;
        IsGlobalSearchOpen = true;
        if (SelectedSearchScope is not ("Everything" or "Mail"))
        {
            ClearLatestMailSearchResults();
        }
        try
        {
            if (debounce)
            {
                await Task.Delay(300, source.Token);
            }

            var searches = new List<Task>();
            if (SelectedSearchScope is "Everything" or "Mail")
            {
                var localMailSearch = AddGlobalResultsAsync(
                    "Mail",
                    SearchCachedMailGloballyAsync(query, source.Token),
                    source);
                searches.Add(localMailSearch);
                searches.Add(EnrichMailSearchFromProviderAsync(query, localMailSearch, source));
            }
            AddSearch("People", () => SearchPeopleGloballyAsync(query, source.Token));
            AddSearch("Calendar", () => SearchCalendarGloballyAsync(query, source.Token));
            AddSearch("To Do", () => SearchTasksGloballyAsync(query, source.Token));
            AddSearch("OneDrive", () => SearchDriveGloballyAsync(query, source.Token));
            AddSearch("Notes", () => SearchNotesGloballyAsync(query, source.Token));
            await Task.WhenAll(searches);

            void AddSearch(string category, Func<Task<IReadOnlyList<GlobalSearchResult>>> search)
            {
                if (SelectedSearchScope is "Everything" || SelectedSearchScope == category)
                {
                    searches.Add(AddGlobalResultsAsync(category, search(), source));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_globalSearchCancellation, source))
            {
                IsGlobalSearchRunning = false;
            }
        }
    }

    private void ClearLatestMailSearchResults()
    {
        if (_latestMailSearchResults.Count == 0)
        {
            return;
        }
        _latestMailSearchResults.Clear();
    }

    private async Task AddGlobalResultsAsync(
        string category,
        Task<IReadOnlyList<GlobalSearchResult>> search,
        CancellationTokenSource source)
    {
        IReadOnlyList<GlobalSearchResult> results;
        try
        {
            results = await search;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            results = [new(category, $"{category} search unavailable", exception.Message, category)];
        }

        if (!ReferenceEquals(_globalSearchCancellation, source) || source.IsCancellationRequested)
        {
            return;
        }
        var startsCategory = true;
        string? previousAccountGroup = null;
        foreach (var result in results
            .Select(result => result with { AccountGroup = SearchGroupFor(result.Value) })
            .OrderBy(static result => result.AccountGroup, StringComparer.OrdinalIgnoreCase))
        {
            var startsAccountGroup = !string.Equals(previousAccountGroup, result.AccountGroup, StringComparison.OrdinalIgnoreCase);
            GlobalSearchResults.Add(result with
            {
                StartsCategory = startsCategory,
                StartsAccountGroup = startsAccountGroup
            });
            startsCategory = false;
            previousAccountGroup = result.AccountGroup;
        }
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchCachedMailGloballyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var messages = (await _store!.SearchAsync(query, 500, cancellationToken))
            .Where(IsSearchableMail)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        _latestMailSearchResults.Clear();
        _latestMailSearchResults.AddRange(messages);
        return messages.Take(100)
            .Select(message => new GlobalSearchResult(
                "Mail", message.Subject, MailSearchSubtitle(message), "Mail", message))
            .ToArray();
    }

    private async Task EnrichMailSearchFromProviderAsync(
        string query,
        Task localResultsAdded,
        CancellationTokenSource source)
    {
        await localResultsAdded;
        if (_provider is null || _store is null || source.IsCancellationRequested ||
            !ReferenceEquals(_globalSearchCancellation, source))
        {
            return;
        }

        var remoteMessages = new List<MailMessage>();
        foreach (var mailbox in Mailboxes.Where(MailboxMatchesSearchFilters))
        {
            source.Token.ThrowIfCancellationRequested();
            if (await IsMailboxSearchCoverageCompleteAsync(mailbox, source.Token))
            {
                continue;
            }

            var account = Accounts.FirstOrDefault(candidate => candidate.AccountId == mailbox.AccountId);
            if (account is null)
            {
                continue;
            }

            try
            {
                var found = await _provider.SearchMessagesAsync(account, mailbox, query, 250, source.Token);
                if (found.Count == 0)
                {
                    continue;
                }
                await _store.ApplySyncPageAsync(
                    $"search-import:{mailbox.Id}",
                    new MailSyncPage(found, null, false),
                    source.Token);
                remoteMessages.AddRange(found.Where(IsSearchableMail));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Local results stay usable when the optional provider search is unavailable.
            }
        }

        if (remoteMessages.Count == 0 || source.IsCancellationRequested ||
            !ReferenceEquals(_globalSearchCancellation, source))
        {
            return;
        }

        var merged = _latestMailSearchResults
            .Concat(remoteMessages)
            .GroupBy(MessageKey, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderByDescending(static message => message.ReceivedAt)
            .ToArray();
        _latestMailSearchResults.Clear();
        _latestMailSearchResults.AddRange(merged);
        ReplaceGlobalMailSearchResults(merged.Take(100));
    }

    private bool MailboxMatchesSearchFilters(Mailbox mailbox) =>
        (SelectedSearchAccountFilter?.AccountId is null ||
            SelectedSearchAccountFilter.AccountId == mailbox.AccountId) &&
        (!IsMailSearchScope || SelectedSearchFolderFilter?.MailboxId is null ||
            SelectedSearchFolderFilter.MailboxId == mailbox.Id);

    private async Task<bool> IsMailboxSearchCoverageCompleteAsync(
        Mailbox mailbox,
        CancellationToken cancellationToken)
    {
        var mailboxFolders = Folders.Where(folder => folder.MailboxId == mailbox.Id).ToArray();
        if (mailboxFolders.Length == 0)
        {
            return false;
        }

        var relevantFolders = mailboxFolders.Where(folder =>
            folder.TotalCount > 0 &&
            IsFolderIncludedInSearch(folder, mailbox.Id) &&
            (!IsMailSearchScope || SelectedSearchFolderFilter?.FolderId is null ||
                SelectedSearchFolderFilter.FolderId == folder.ProviderId));
        foreach (var folder in relevantFolders)
        {
            var state = await _store!.GetSyncStateAsync(
                SyncEngine.CursorId(mailbox.Id, folder.ProviderId, 0),
                cancellationToken);
            if (!state.IsComplete)
            {
                return false;
            }
        }
        return true;
    }

    private void ReplaceGlobalMailSearchResults(IEnumerable<MailMessage> messages)
    {
        var insertionIndex = GlobalSearchResults
            .Select((result, index) => (result, index))
            .FirstOrDefault(static item => item.result.Category == "Mail")
            .index;
        for (var index = GlobalSearchResults.Count - 1; index >= 0; index--)
        {
            if (GlobalSearchResults[index].Category == "Mail")
            {
                GlobalSearchResults.RemoveAt(index);
            }
        }

        var startsCategory = true;
        string? previousAccountGroup = null;
        foreach (var result in messages
            .Select(message => new GlobalSearchResult(
                "Mail", message.Subject, MailSearchSubtitle(message), "Mail", message))
            .Select(result => result with { AccountGroup = SearchGroupFor(result.Value) })
            .OrderBy(static result => result.AccountGroup, StringComparer.OrdinalIgnoreCase))
        {
            var startsAccountGroup = !string.Equals(
                previousAccountGroup,
                result.AccountGroup,
                StringComparison.OrdinalIgnoreCase);
            GlobalSearchResults.Insert(insertionIndex++, result with
            {
                StartsCategory = startsCategory,
                StartsAccountGroup = startsAccountGroup
            });
            startsCategory = false;
            previousAccountGroup = result.AccountGroup;
        }
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchPeopleGloballyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var discoveredTask = _store!.GetDiscoveredPeopleAsync(query, 8, cancellationToken);
        var savedTask = _workspaceProvider is null
            ? Task.FromResult<IReadOnlyList<ContactInfo>>([])
            : SearchAccountsAsync(
                (account, token) => GetSavedContactsAsync(account, query, token),
                cancellationToken);
        await Task.WhenAll(discoveredTask, savedTask);
        return savedTask.Result.Select(contact => new GlobalSearchResult(
                "People", contact.DisplayName, contact.EmailText, "People", contact))
            .Concat(discoveredTask.Result.Select(person => new GlobalSearchResult(
                "People", person.DisplayName, person.EmailAddress, "People", person)))
            .Take(30)
            .ToArray();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchCalendarGloballyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var cached = await _store!.SearchWorkspaceItemsAsync<CalendarEvent>(
            "calendar-event", query, 30, SelectedSearchAccountFilter?.AccountId, cancellationToken);
        if (cached.Count > 0 || _workspaceProvider is null)
        {
            return CalendarResults(cached, query);
        }
        var now = DateTimeOffset.Now;
        var events = await SearchAccountsAsync(
            (account, token) => _workspaceProvider.GetEventsAsync(
                account, now.AddYears(-1), now.AddYears(2), token),
            cancellationToken);
        foreach (var group in events.GroupBy(item => (item.AccountId, item.CalendarId)))
        {
            if (group.Key.AccountId is not null)
            {
                await _store.ReplaceCalendarEventsAsync(
                    group.Key.AccountId, group.Key.CalendarId,
                    now.AddYears(-1), now.AddYears(2), group.ToArray(), cancellationToken);
            }
        }
        return CalendarResults(events, query);

        static IReadOnlyList<GlobalSearchResult> CalendarResults(
            IEnumerable<CalendarEvent> source,
            string search) => source
            .Where(item => Contains(item.Subject, search) || Contains(item.Location, search))
            .OrderBy(item => item.StartsAt)
            .Take(30)
            .Select(item => new GlobalSearchResult(
                "Calendar", item.Subject, item.TimeText, "Calendar", item))
            .ToArray();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchTasksGloballyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var cached = await _store!.SearchWorkspaceItemsAsync<TaskInfo>(
            "task", query, 30, SelectedSearchAccountFilter?.AccountId, cancellationToken);
        if (cached.Count > 0 || _workspaceProvider is null)
        {
            return TaskResults(cached, query);
        }
        var tasks = await SearchAccountsAsync(
            (account, token) => _workspaceProvider.GetTasksAsync(account, token),
            cancellationToken);
        foreach (var group in tasks.GroupBy(item => (item.AccountId, item.ListId)))
        {
            if (group.Key.AccountId is not null)
            {
                await _store.ReplaceWorkspaceItemsAsync(
                    "task", group.Key.AccountId, group.Key.ListId, group.ToArray(),
                    static item => item.ProviderId,
                    static item => $"{item.Title} {item.Notes} {string.Join(' ', item.Categories ?? [])}",
                    cancellationToken);
            }
        }
        return TaskResults(tasks, query);

        static IReadOnlyList<GlobalSearchResult> TaskResults(
            IEnumerable<TaskInfo> source,
            string search) => source
            .Where(item => Contains(item.Title, search) || Contains(item.Notes, search))
            .Take(30)
            .Select(item => new GlobalSearchResult("To Do", item.Title, item.DueText, "To Do", item))
            .ToArray();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchDriveGloballyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var cached = await _store!.SearchWorkspaceItemsAsync<CloudFile>(
            "drive-file", query, 40, SelectedSearchAccountFilter?.AccountId, cancellationToken);
        if (cached.Count > 0 || _workspaceProvider is null)
        {
            return DriveResults(cached);
        }
        var files = await SearchAccountsAsync(
            (account, token) => _workspaceProvider.SearchFilesAsync(account, query, token),
            cancellationToken);
        foreach (var group in files.GroupBy(static item => item.AccountId))
        {
            if (group.Key is not null)
            {
                await _store.UpsertWorkspaceItemsAsync(
                    "drive-file", group.Key, "index", group.ToArray(),
                    static item => item.ProviderId,
                    static item => $"{item.Name} {item.Path}", cancellationToken);
            }
        }
        return DriveResults(files);

        static IReadOnlyList<GlobalSearchResult> DriveResults(IEnumerable<CloudFile> source) => source.Take(40)
            .Select(item => new GlobalSearchResult("OneDrive", item.Name, item.Path, "OneDrive", item))
            .ToArray();
    }

    private async Task<IReadOnlyList<GlobalSearchResult>> SearchNotesGloballyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var cached = await _store!.SearchWorkspaceItemsAsync<NoteInfo>(
            "note", query, 30, SelectedSearchAccountFilter?.AccountId, cancellationToken);
        if (cached.Count > 0 || _workspaceProvider is null)
        {
            return NoteResults(cached, query);
        }
        var notes = await SearchAccountsAsync(
            (account, token) => _workspaceProvider.GetNotesAsync(account, token),
            cancellationToken);
        foreach (var group in notes.GroupBy(static item => item.AccountId))
        {
            if (group.Key is not null)
            {
                await _store.ReplaceWorkspaceItemsAsync(
                    "note", group.Key, "all", group.ToArray(),
                    static item => item.ProviderId,
                    static item => item.Title, cancellationToken);
            }
        }
        return NoteResults(notes, query);

        static IReadOnlyList<GlobalSearchResult> NoteResults(
            IEnumerable<NoteInfo> source,
            string search) => source.Where(item => Contains(item.Title, search))
            .OrderByDescending(item => item.ModifiedAt)
            .Take(30)
            .Select(item => new GlobalSearchResult("Notes", item.Title, item.ModifiedText, "Notes", item))
            .ToArray();
    }

    private async Task<IReadOnlyList<T>> SearchAccountsAsync<T>(
        Func<MailAccount, CancellationToken, Task<IReadOnlyList<T>>> search,
        CancellationToken cancellationToken)
    {
        var batches = await Task.WhenAll(Accounts
            .Where(account => SelectedSearchAccountFilter?.AccountId is null ||
                SelectedSearchAccountFilter.AccountId == account.AccountId)
            .Select(async account =>
        {
            try
            {
                return await search(account, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Array.Empty<T>();
            }
        }));
        return batches.SelectMany(static batch => batch).ToArray();
    }

    private static bool Contains(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private async Task<IReadOnlyList<ContactInfo>> GetSavedContactsAsync(
        MailAccount account,
        string query,
        CancellationToken cancellationToken)
    {
        var cached = string.IsNullOrWhiteSpace(query)
            ? Array.Empty<ContactInfo>()
            : await _store!.SearchWorkspaceItemsAsync<ContactInfo>(
                "contact", query, 500, account.AccountId, cancellationToken);
        if (cached.Count > 0)
        {
            return Filter(cached);
        }

        try
        {
            var contacts = await _workspaceProvider!.SearchContactsAsync(account, query, cancellationToken);
            if (string.IsNullOrWhiteSpace(query))
            {
                await _store!.ReplaceWorkspaceItemsAsync(
                    "contact", account.AccountId, "all", contacts,
                    static item => item.ProviderId,
                    static item => $"{item.DisplayName} {string.Join(' ', item.EmailAddresses)}",
                    cancellationToken);
            }
            else
            {
                await _store!.UpsertWorkspaceItemsAsync(
                    "contact", account.AccountId, "all", contacts,
                    static item => item.ProviderId,
                    static item => $"{item.DisplayName} {string.Join(' ', item.EmailAddresses)}",
                    cancellationToken);
            }
            return Filter(contacts);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Filter(cached);
        }

        IReadOnlyList<ContactInfo> Filter(IEnumerable<ContactInfo> contacts) => contacts
            .Where(contact => string.IsNullOrWhiteSpace(query) ||
                Contains(contact.DisplayName, query) ||
                contact.EmailAddresses.Any(address => Contains(address, query)))
            .ToArray();
    }

    private bool IsSearchableMail(MailMessage message)
    {
        if (message.IsDeleted)
        {
            return false;
        }
        var mailbox = Mailboxes.FirstOrDefault(candidate => candidate.Id == message.MailboxId);
        if (SelectedSearchAccountFilter?.AccountId is { } accountId && mailbox?.AccountId != accountId)
        {
            return false;
        }
        if (IsMailSearchScope && SelectedSearchFolderFilter is { FolderId: not null } folderFilter &&
            (message.MailboxId != folderFilter.MailboxId || message.FolderId != folderFilter.FolderId))
        {
            return false;
        }
        var folder = Folders.FirstOrDefault(candidate =>
            candidate.MailboxId == message.MailboxId && candidate.ProviderId == message.FolderId);
        return IsFolderIncludedInSearch(folder, message.MailboxId);
    }

    private bool IsFolderIncludedInSearch(MailFolderItem? folder, string mailboxId) =>
        !IsDeletedOrJunkFolder(folder) &&
        (IncludeArchivedMailInSearch ||
            (!IsArchiveFolder(folder) && !IsArchiveMailbox(mailboxId)));

    private static bool IsDeletedOrJunkFolder(MailFolderItem? folder) =>
        folder?.WellKnownName is "deleteditems" ||
        string.Equals(folder?.DisplayName, "Deleted Items", StringComparison.OrdinalIgnoreCase) ||
        IsJunkFolder(folder);

    private static bool IsJunkFolder(MailFolderItem? folder) =>
        string.Equals(folder?.WellKnownName, "junkemail", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(folder?.DisplayName, "Junk Email", StringComparison.OrdinalIgnoreCase);

    internal bool IsMessageInJunk(MailMessage message) =>
        IsJunkFolder(Folders.FirstOrDefault(folder =>
            folder.MailboxId == message.MailboxId && folder.ProviderId == message.FolderId));

    private bool IsArchiveFolder(MailFolderItem? folder)
    {
        while (folder is not null)
        {
            if (string.Equals(folder.WellKnownName, "archive", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(folder.DisplayName, "Archive", StringComparison.OrdinalIgnoreCase) ||
                folder.DisplayName.StartsWith("Online Archive", StringComparison.OrdinalIgnoreCase) ||
                folder.DisplayName.StartsWith("In-Place Archive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            folder = Folders.FirstOrDefault(candidate =>
                candidate.MailboxId == folder.MailboxId && candidate.ProviderId == folder.ParentProviderId);
        }
        return false;
    }

    private bool IsArchiveMailbox(string mailboxId)
    {
        var name = Mailboxes.FirstOrDefault(mailbox => mailbox.Id == mailboxId)?.DisplayName;
        return name?.StartsWith("Online Archive", StringComparison.OrdinalIgnoreCase) == true ||
            name?.StartsWith("In-Place Archive", StringComparison.OrdinalIgnoreCase) == true;
    }

    private string MailSearchSubtitle(MailMessage message) =>
        $"{MailFolderPath(message.MailboxId, message.FolderId)} · {message.SenderDisplayName} · {message.DisplayPreview}";

    private string MailFolderPath(string mailboxId, string folderProviderId)
    {
        var folders = Folders.Where(candidate => candidate.MailboxId == mailboxId)
            .ToDictionary(static folder => folder.ProviderId, StringComparer.Ordinal);
        var parts = new Stack<string>();
        var folderId = folderProviderId;
        while (folders.TryGetValue(folderId, out var folder))
        {
            parts.Push(folder.DisplayName);
            folderId = folder.ParentProviderId ?? "";
        }
        return parts.Count == 0 ? "Unknown folder" : string.Join(" / ", parts);
    }

    private string MailLocation(MailMessage message)
    {
        var mailbox = Mailboxes.FirstOrDefault(candidate => candidate.Id == message.MailboxId);
        var folder = MailFolderPath(message.MailboxId, message.FolderId);
        return mailbox is null ? folder : $"{mailbox.Address} · {folder}";
    }

    private void RebuildSearchFilters()
    {
        var accountId = SelectedSearchAccountFilter?.AccountId;
        var folderId = SelectedSearchFolderFilter?.FolderId;
        var folderMailboxId = SelectedSearchFolderFilter?.MailboxId;
        Replace(SearchAccountFilters,
        [
            new SearchAccountFilter("All accounts", null),
            .. Accounts.Select(account => new SearchAccountFilter(
                $"{account.DisplayName} - {account.EmailAddress}", account.AccountId))
        ]);
        Replace(SearchFolderFilters,
        [
            new SearchFolderFilter("All mail folders", null, null),
            .. Folders.Where(folder => !IsDeletedOrJunkFolder(folder) &&
                    (IncludeArchivedMailInSearch || !IsArchiveFolder(folder)))
                .Select(folder => new SearchFolderFilter(
                    MailFolderFilterName(folder), folder.MailboxId, folder.ProviderId))
        ]);
        _selectedSearchAccountFilter = SearchAccountFilters.FirstOrDefault(filter => filter.AccountId == accountId)
            ?? SearchAccountFilters[0];
        _selectedSearchFolderFilter = SearchFolderFilters.FirstOrDefault(filter =>
            filter.MailboxId == folderMailboxId && filter.FolderId == folderId) ?? SearchFolderFilters[0];
        RaisePropertyChanged(nameof(SelectedSearchAccountFilter));
        RaisePropertyChanged(nameof(SelectedSearchFolderFilter));
    }

    private string MailFolderFilterName(MailFolderItem folder)
        => $"{Mailboxes.FirstOrDefault(mailbox => mailbox.Id == folder.MailboxId)?.Address ?? folder.MailboxId} / " +
            MailFolderPath(folder.MailboxId, folder.ProviderId);

    private string SearchGroupFor(object? value) => value switch
    {
        MailMessage message => MailboxSearchGroup(message.MailboxId),
        DiscoveredPerson person => MailboxSearchGroup(person.MailboxIds.FirstOrDefault()),
        ContactInfo contact => AccountSearchGroup(contact.AccountId),
        CalendarEvent calendarEvent => AccountSearchGroup(calendarEvent.AccountId),
        TaskInfo task => AccountSearchGroup(task.AccountId),
        CloudFile file => AccountSearchGroup(file.AccountId),
        NoteInfo note => AccountSearchGroup(note.AccountId),
        _ => "All accounts"
    };

    private string MailboxSearchGroup(string? mailboxId)
    {
        var mailbox = Mailboxes.FirstOrDefault(candidate => candidate.Id == mailboxId);
        if (mailbox is null)
        {
            var account = Accounts.FirstOrDefault(candidate =>
                mailboxId?.StartsWith(candidate.AccountId + ":", StringComparison.Ordinal) == true);
            if (account is not null)
            {
                return $"{account.DisplayName} - {account.EmailAddress}";
            }
            return "Local mail cache";
        }
        return mailbox.IsShared
            ? $"Shared mailbox - {mailbox.Address}"
            : $"{mailbox.DisplayName} - {mailbox.Address}";
    }

    private string AccountSearchGroup(string? accountId)
    {
        var account = Accounts.FirstOrDefault(candidate => candidate.AccountId == accountId);
        return account is null ? "Connected account" : $"{account.DisplayName} - {account.EmailAddress}";
    }

    private async Task OpenGlobalSearchResultAsync(GlobalSearchResult result)
    {
        IsGlobalSearchOpen = false;
        switch (result.Module)
        {
            case "Mail" when result.Value is MailMessage message:
                ShowMailSearchResults(message);
                break;
            case "People":
                ModuleSearchText = SearchText;
                await ShowWorkspaceModuleAsync("People");
                break;
            case "Calendar":
                await ShowWorkspaceModuleAsync("Calendar");
                if (CalendarWorkspace is not null && result.Value is CalendarEvent calendarEvent)
                {
                    CalendarWorkspace.SelectedDate = calendarEvent.StartsAt.ToLocalTime();
                }
                break;
            case "To Do":
                await ShowWorkspaceModuleAsync("To Do");
                if (TasksWorkspace is not null)
                {
                    TasksWorkspace.SearchText = SearchText;
                    TasksWorkspace.SearchCommand.Execute(null);
                }
                break;
            case "OneDrive":
                await ShowWorkspaceModuleAsync("OneDrive");
                if (DriveWorkspace is not null)
                {
                    DriveWorkspace.SearchQuery = SearchText;
                    DriveWorkspace.SearchCommand.Execute(null);
                }
                break;
            case "Notes":
                await ShowWorkspaceModuleAsync("Notes");
                if (NotesWorkspace is not null)
                {
                    NotesWorkspace.SearchText = SearchText;
                    NotesWorkspace.SearchCommand.Execute(null);
                }
                break;
        }
    }

    private Task CloseGlobalSearchAsync()
    {
        IsGlobalSearchOpen = false;
        return Task.CompletedTask;
    }

    private Task ClearGlobalSearchAsync()
    {
        SearchText = "";
        IsGlobalSearchOpen = false;
        return Task.CompletedTask;
    }

    private async Task OpenGlobalSearchGroupAsync(GlobalSearchResult result)
    {
        if (result.Module != "Mail")
        {
            await OpenGlobalSearchResultAsync(result);
            return;
        }

        var messages = _latestMailSearchResults
            .Where(message => string.Equals(
                SearchGroupFor(message), result.AccountGroup, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        ShowMailSearchResults(messages.FirstOrDefault(), messages);
    }

    private void ShowMailSearchResults(
        MailMessage? selected,
        IReadOnlyList<MailMessage>? results = null)
    {
        IsGlobalSearchOpen = false;
        IsSettingsOpen = false;
        ActiveModule = "Mail";
        IsDraftsView = false;
        _selectedFolder = null;
        IsSearchResultsView = true;
        Replace(Messages, results ?? _latestMailSearchResults);
        SelectedMessages.Clear();
        SelectedMessage = selected is null
            ? Messages.FirstOrDefault()
            : Messages.FirstOrDefault(candidate => SameMessage(candidate, selected)) ?? Messages.FirstOrDefault();
        RaisePropertyChanged(nameof(CurrentFolderName));
        RaiseMessageState();
    }

    private async Task ClearSearchResultsAsync()
    {
        IsSearchResultsView = false;
        SearchText = "";
        await ShowUnifiedInboxAsync();
    }

    private Task FocusSearchAsync()
    {
        SearchFocusRequested?.Invoke();
        return Task.CompletedTask;
    }

    private async Task LoadMessagesAsync()
    {
        if (_store is null)
        {
            return;
        }
        if (IsSearchResultsView && SearchText.Trim().Length >= 2)
        {
            await SearchCachedMailGloballyAsync(SearchText.Trim(), CancellationToken.None);
            ReconcileMessages(_latestMailSearchResults);
            RaiseMessageState();
            return;
        }

        IReadOnlyList<MailMessage> messages;
        if (_selectedFolder is not null)
        {
            messages = await _store.GetMessagesAsync(_selectedFolder.MailboxId, _selectedFolder.ProviderId);
        }
        else
        {
            var inboxes = Folders.Where(static folder => folder.WellKnownName == "inbox").ToArray();
            messages = inboxes.Length == 0
                ? await _store.GetMessagesAsync()
                : (await Task.WhenAll(inboxes.Select(folder =>
                    _store.GetMessagesAsync(folder.MailboxId, folder.ProviderId))))
                    .SelectMany(static messages => messages)
                    .OrderByDescending(static message => message.ReceivedAt)
                    .Take(5000)
                    .ToArray();
        }

        ReconcileMessages(messages);
        RaiseMessageState();
        _ = RepairMissingSubjectsAsync(messages);
    }

    private async Task RepairMissingSubjectsAsync(IEnumerable<MailMessage> messages)
    {
        if (_provider is null || _store is null)
        {
            return;
        }

        foreach (var stale in messages.Where(static message => message.Subject == "(no subject)"))
        {
            var key = $"{stale.MailboxId}:{stale.ProviderId}";
            if (!_subjectRepairAttempts.Add(key) ||
                !TryGetMessageContext(stale, out var account, out var mailbox))
            {
                continue;
            }

            try
            {
                var hydrated = await _provider.GetMessageAsync(account, mailbox, stale.ProviderId);
                if (hydrated.Subject == "(no subject)")
                {
                    continue;
                }

                var repaired = stale with { Subject = hydrated.Subject };
                await _store.ApplySyncPageAsync(
                    $"manual:{mailbox.Id}",
                    new MailSyncPage([repaired], null, false));
                var displayed = Messages.FirstOrDefault(message => SameMessage(message, stale));
                if (displayed is not null)
                {
                    ApplyMessageUpdate(displayed, repaired);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _subjectRepairAttempts.Remove(key);
                // A legitimate subjectless message should not disrupt navigation or sync.
            }
        }
    }

    private async Task LoadFoldersAsync()
    {
        if (_store is null)
        {
            return;
        }

        var selectedFolderId = _selectedFolder?.ProviderId;
        var selectedMailboxId = _selectedFolder?.MailboxId;
        var mailboxes = Mailboxes.ToDictionary(static mailbox => mailbox.Id);
        Replace(Folders, (await _store.GetFoldersAsync()).Select(folder => new MailFolderItem(
            folder,
            mailboxes.TryGetValue(folder.MailboxId, out var mailbox) ? mailbox.DisplayName : folder.MailboxId)));
        _selectedFolder = Folders.FirstOrDefault(folder =>
            folder.ProviderId == selectedFolderId && folder.MailboxId == selectedMailboxId);
        Replace(FolderGroups, Mailboxes.Select(mailbox => new MailboxFolderGroup(
            mailbox,
            BuildFolderTree(Folders.Where(folder => folder.MailboxId == mailbox.Id)))));
        if (!IsGlobalSearchOpen && !IsSearchResultsView)
        {
            RebuildSearchFilters();
        }
    }

    private static IReadOnlyList<MailFolderNode> BuildFolderTree(IEnumerable<MailFolderItem> source)
    {
        var folders = source.ToArray();
        var ids = folders.Select(static folder => folder.ProviderId).ToHashSet(StringComparer.Ordinal);
        var children = folders.ToLookup(static folder => folder.ParentProviderId, StringComparer.Ordinal);

        MailFolderNode Build(MailFolderItem folder) =>
            new(folder, children[folder.ProviderId].Select(Build).ToArray());

        return folders
            .Where(folder => folder.ParentProviderId is null || !ids.Contains(folder.ParentProviderId))
            .Select(Build)
            .ToArray();
    }

    private async Task ShowUnifiedInboxAsync()
    {
        IsSettingsOpen = false;
        ActiveModule = "Mail";
        IsDraftsView = false;
        IsSearchResultsView = false;
        _selectedFolder = null;
        RaisePropertyChanged(nameof(CurrentFolderName));
        RaisePropertyChanged(nameof(IsUnifiedInbox));
        await LoadMessagesAsync();
    }

    private async Task SelectFolderAsync(MailFolderItem folder)
    {
        IsSettingsOpen = false;
        ActiveModule = "Mail";
        IsDraftsView = false;
        IsSearchResultsView = false;
        _selectedFolder = folder;
        RaisePropertyChanged(nameof(CurrentFolderName));
        RaisePropertyChanged(nameof(IsUnifiedInbox));
        await LoadMessagesAsync();
    }

    private Task ShowDraftsAsync()
    {
        IsSettingsOpen = false;
        ActiveModule = "Mail";
        IsDraftsView = true;
        IsSearchResultsView = false;
        SelectedMessage = null;
        return Task.CompletedTask;
    }

    private bool CanOpenWorkspaceModule() =>
        Accounts.Count > 0 && _workspaceProvider is not null && !IsWorkspaceLoading;

    private Task RefreshWorkspaceAsync() => IsMailModule
        ? Task.CompletedTask
        : IsCalendarModule
            ? LoadCalendarWorkspaceAsync()
            : IsFilesModule
                ? LoadDriveWorkspaceAsync()
            : IsNotesModule
                ? LoadNotesWorkspaceAsync()
            : IsTasksModule
                ? LoadTasksWorkspaceAsync()
            : ShowWorkspaceModuleAsync(ActiveModule);

    private async Task ShowWorkspaceModuleAsync(string module)
    {
        var account = Accounts.FirstOrDefault();
        if (account is null || _workspaceProvider is null || IsWorkspaceLoading)
        {
            return;
        }

        IsSettingsOpen = false;
        ActiveModule = module;
        if (module == "Calendar")
        {
            Error = null;
            Status = "Loading Calendar...";
            try
            {
                await LoadCalendarWorkspaceAsync();
                Status = "Up to date";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Error = exception.Message;
                Status = "Calendar could not be loaded";
            }
            return;
        }
        if (module == "Notes")
        {
            Error = null;
            Status = "Loading Notes...";
            try
            {
                await LoadNotesWorkspaceAsync();
                Status = "Up to date";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Error = exception.Message;
                Status = "Notes could not be loaded";
            }
            return;
        }
        if (module == "OneDrive")
        {
            Error = null;
            Status = "Loading OneDrive...";
            try
            {
                await LoadDriveWorkspaceAsync();
                Status = "Up to date";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Error = exception.Message;
                Status = "OneDrive could not be loaded";
            }
            return;
        }
        if (module == "To Do")
        {
            Error = null;
            Status = "Loading To Do...";
            try
            {
                await LoadTasksWorkspaceAsync();
                Status = "Up to date";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Error = exception.Message;
                Status = "To Do could not be loaded";
            }
            return;
        }

        IsWorkspaceLoading = true;
        RefreshWorkspaceCommands();
        Error = null;
        Status = $"Loading {module}...";
        try
        {
            switch (module)
            {
                case "People":
                    await LoadPeopleAsync();
                    break;
            }
            Status = "Up to date";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            Status = $"{module} could not be loaded";
        }
        finally
        {
            IsWorkspaceLoading = false;
            RaisePropertyChanged(nameof(IsWorkspaceEmpty));
            RefreshWorkspaceCommands();
        }
    }

    private async Task LoadCalendarWorkspaceAsync()
    {
        if (_workspaceProvider is null)
        {
            return;
        }

        CalendarWorkspace ??= new CalendarWorkspaceViewModel(
            _workspaceProvider, Accounts.ToArray(), store: _store);
        await CalendarWorkspace.UpdateAccountsAsync(Accounts.ToArray());
    }

    private async Task LoadNotesWorkspaceAsync()
    {
        if (_workspaceProvider is null)
        {
            return;
        }

        NotesWorkspace ??= new NotesWorkspaceViewModel(
            _workspaceProvider, Accounts.ToArray(), _renderer, _store);
        await NotesWorkspace.UpdateAccountsAsync(Accounts.ToArray());
    }

    private async Task LoadDriveWorkspaceAsync()
    {
        if (_workspaceProvider is null)
        {
            return;
        }

        if (DriveWorkspace is null)
        {
            DriveWorkspace = new DriveWorkspaceViewModel(
                _workspaceProvider, Accounts.ToArray(), _store);
            DriveWorkspace.ItemChosen += HandleDriveItemChosen;
            DriveWorkspace.LinkChosen += HandleDriveLinkChosen;
        }
        await DriveWorkspace.UpdateAccountsAsync(Accounts.ToArray());
        await DriveWorkspace.InitializeAsync();
    }

    private async Task LoadTasksWorkspaceAsync()
    {
        if (_workspaceProvider is null)
        {
            return;
        }

        TasksWorkspace ??= new TasksWorkspaceViewModel(
            _workspaceProvider, Accounts.ToArray(), _store);
        await TasksWorkspace.UpdateAccountsAsync(Accounts.ToArray());
    }

    private async Task RefreshOwnedWorkspaceAccountsIfCreatedAsync()
    {
        if (CalendarWorkspace is not null)
        {
            await CalendarWorkspace.UpdateAccountsAsync(Accounts.ToArray());
        }
        if (NotesWorkspace is not null)
        {
            await NotesWorkspace.UpdateAccountsAsync(Accounts.ToArray());
        }
        if (DriveWorkspace is not null)
        {
            await DriveWorkspace.UpdateAccountsAsync(Accounts.ToArray());
        }
        if (TasksWorkspace is not null)
        {
            await TasksWorkspace.UpdateAccountsAsync(Accounts.ToArray());
        }
    }

    internal Task RefreshConnectedAccountsAsync() =>
        RefreshOwnedWorkspaceAccountsIfCreatedAsync();

    private void HandleDriveItemChosen(DriveProviderSelection selection) =>
        _ = AttachDriveItemAsync(selection);

    private void HandleDriveLinkChosen(DriveProviderSelection selection)
    {
        if (selection.Item.WebUrl is null)
        {
            Error = "OneDrive did not provide a sharing link for this file.";
            return;
        }

        var mailbox = Mailboxes.FirstOrDefault(candidate =>
            candidate.AccountId == selection.Account.AccountId && !candidate.IsShared) ??
            Mailboxes.FirstOrDefault(candidate => candidate.AccountId == selection.Account.AccountId);
        ComposeRequested?.Invoke(new ComposeRequest(
            Body: $"<p><a href='{WebUtility.HtmlEncode(selection.Item.WebUrl.AbsoluteUri)}'>{WebUtility.HtmlEncode(selection.Item.Name)}</a></p>",
            AccountId: selection.Account.AccountId,
            MailboxId: mailbox?.Id,
            IsHtml: true));
    }

    internal async Task AttachDriveItemAsync(
        DriveProviderSelection selection,
        CancellationToken cancellationToken = default)
    {
        if (_workspaceProvider is null)
        {
            Error = "OneDrive is unavailable.";
            return;
        }
        if (selection.Item.IsFolder)
        {
            Error = "Folders cannot be attached to a message.";
            return;
        }
        if (selection.Item.Size > DraftAttachment.MaximumSizeBytes)
        {
            Error = $"'{selection.Item.Name}' is larger than the 150 MB Microsoft Graph attachment limit.";
            return;
        }

        var account = Accounts.FirstOrDefault(candidate =>
            candidate.ProviderId == selection.Account.ProviderId &&
            candidate.AccountId == selection.Account.AccountId);
        var mailbox = Mailboxes.FirstOrDefault(candidate =>
            candidate.AccountId == selection.Account.AccountId &&
            !candidate.IsShared) ??
            Mailboxes.FirstOrDefault(candidate => candidate.AccountId == selection.Account.AccountId);
        if (account is null || mailbox is null ||
            selection.Item.AccountId != selection.Account.AccountId ||
            selection.Item.AccountProviderId != selection.Account.ProviderId)
        {
            Error = "The drive file's owning account is no longer available.";
            return;
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _driveAttachmentCancellation, linkedCancellation);
        previous?.Cancel();
        Error = null;
        Status = $"Downloading {selection.Item.Name} from OneDrive...";
        try
        {
            await using var content = new LimitedMemoryStream(DraftAttachment.MaximumSizeBytes);
            await _workspaceProvider.DownloadFileAsync(
                account,
                selection.Item,
                content,
                linkedCancellation.Token);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            ComposeRequested?.Invoke(new ComposeRequest(
                AccountId: account.AccountId,
                MailboxId: mailbox.Id,
                Attachments:
                [
                    new DraftAttachment(
                        selection.Item.Name,
                        selection.Item.ContentType ?? "application/octet-stream",
                        content.ToArray())
                ]));
            Status = "Drive file attached";
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_driveAttachmentCancellation, linkedCancellation))
            {
                Status = "Drive attachment download cancelled";
            }
        }
        catch (Exception exception)
        {
            Error = $"'{selection.Item.Name}' could not be attached: {exception.Message}";
            Status = "Drive attachment failed";
        }
        finally
        {
            Interlocked.CompareExchange(ref _driveAttachmentCancellation, null, linkedCancellation);
            linkedCancellation.Dispose();
        }
    }

    private void RefreshWorkspaceCommands()
    {
        ((AsyncCommand)ShowCalendarCommand).Refresh();
        ((AsyncCommand)ShowContactsCommand).Refresh();
        ((AsyncCommand)ShowTasksCommand).Refresh();
        ((AsyncCommand)ShowFilesCommand).Refresh();
        ((AsyncCommand)ShowNotesCommand).Refresh();
        ((AsyncCommand)RefreshWorkspaceCommand).Refresh();
        ((AsyncCommand)SearchWorkspaceCommand).Refresh();
    }

    private async Task LoadPeopleAsync()
    {
        if (_workspaceProvider is null)
        {
            return;
        }

        var results = await Task.WhenAll(Accounts.ToArray().Select(async account =>
        {
            try
            {
                var contacts = await _workspaceProvider.SearchContactsAsync(account, ModuleSearchText);
                if (_store is not null)
                {
                    if (string.IsNullOrWhiteSpace(ModuleSearchText))
                    {
                        await _store.ReplaceWorkspaceItemsAsync(
                            "contact", account.AccountId, "all", contacts,
                            static item => item.ProviderId,
                            static item => $"{item.DisplayName} {string.Join(' ', item.EmailAddresses)}");
                    }
                    else
                    {
                        await _store.UpsertWorkspaceItemsAsync(
                            "contact", account.AccountId, "all", contacts,
                            static item => item.ProviderId,
                            static item => $"{item.DisplayName} {string.Join(' ', item.EmailAddresses)}");
                    }
                }
                return new AccountContactResult(account, contacts, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new AccountContactResult(account, [], exception.Message);
            }
        }));
        var saved = results.SelectMany(static result => result.Contacts).ToArray();
        Replace(Contacts, saved);

        IReadOnlyList<DiscoveredPerson> discovered = _store is null
            ? Array.Empty<DiscoveredPerson>()
            : await _store.GetDiscoveredPeopleAsync(ModuleSearchText);
        var ownAddresses = Accounts.Select(static account => account.EmailAddress)
            .Concat(Mailboxes.Select(static mailbox => mailbox.Address))
            .Select(NormalizeEmail)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var savedAddresses = saved.SelectMany(static contact => contact.EmailAddresses)
            .Select(NormalizeEmail)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var people = saved
            .GroupBy(contact => $"{contact.AccountId}\n{contact.ProviderId}", StringComparer.Ordinal)
            .Select(group =>
            {
                var contact = group.First();
                var account = Accounts.FirstOrDefault(candidate => candidate.AccountId == contact.AccountId);
                return PersonEntry.Saved(contact, account?.EmailAddress ?? "Unknown account");
            })
            .Concat(discovered
                .Where(person => !ownAddresses.Contains(NormalizeEmail(person.EmailAddress)) &&
                    !savedAddresses.Contains(NormalizeEmail(person.EmailAddress)))
                .Select(person => PersonEntry.Discovered(
                    person,
                    string.Join(", ", person.MailboxIds
                        .Select(id => Mailboxes.FirstOrDefault(mailbox => mailbox.Id == id))
                        .Where(static mailbox => mailbox is not null)
                        .Select(static mailbox => mailbox!)
                        .Select(mailbox =>
                        {
                            var account = Accounts.FirstOrDefault(candidate =>
                                candidate.AccountId == mailbox.AccountId);
                            return $"{mailbox.DisplayName} ({account?.EmailAddress ?? mailbox.Address})";
                        })
                        .Distinct(StringComparer.OrdinalIgnoreCase)))))
            .OrderByDescending(static person => person.IsSaved)
            .ThenByDescending(static person => person.Frequency)
            .ThenBy(static person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Replace(People, people);

        PeopleErrorText = string.Join(Environment.NewLine, results
            .Where(static result => result.Error is not null)
            .Select(result =>
                $"{result.Account.EmailAddress}: {result.Error} Open Settings > Accounts and re-authenticate if access expired."));
        RaisePropertyChanged(nameof(IsWorkspaceEmpty));
    }

    internal async Task<IReadOnlyList<RecipientSuggestion>> SearchRecipientSuggestionsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        var discoveredTask = _store is null
            ? Task.FromResult<IReadOnlyList<DiscoveredPerson>>([])
            : _store.GetDiscoveredPeopleAsync(query, 20, cancellationToken);
        var contactsTask = _workspaceProvider is null
            ? Task.FromResult<IReadOnlyList<ContactInfo>>(Contacts
                .Where(contact => Contains(contact.DisplayName, query) ||
                    contact.EmailAddresses.Any(address => Contains(address, query)))
                .ToArray())
            : SearchSavedContactsAsync();
        await Task.WhenAll(discoveredTask, contactsTask);
        return contactsTask.Result
            .SelectMany(contact => contact.EmailAddresses.Select(address => new RecipientSuggestion(
                contact.DisplayName,
                address,
                Accounts.FirstOrDefault(account => account.AccountId == contact.AccountId)?.EmailAddress ?? "Saved contact")))
            .Concat(discoveredTask.Result.Select(person => new RecipientSuggestion(
                person.DisplayName,
                person.EmailAddress,
                "Mail history")))
            .DistinctBy(static suggestion => suggestion.Address, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();

        async Task<IReadOnlyList<ContactInfo>> SearchSavedContactsAsync()
        {
            var results = await Task.WhenAll(Accounts.Select(async account =>
            {
                try
                {
                    return await GetSavedContactsAsync(account, query, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return Array.Empty<ContactInfo>();
                }
            }));
            return results.SelectMany(static contacts => contacts).ToArray();
        }
    }

    private Task EditContactAsync(PersonEntry person)
    {
        if (person.SavedContact is null)
        {
            return Task.CompletedTask;
        }
        _editingContact = person;
        _pendingDeleteContact = null;
        SelectedContactAccount = Accounts.FirstOrDefault(account => account.AccountId == person.SavedContact.AccountId);
        ContactName = person.SavedContact.DisplayName;
        ContactEmails = string.Join("; ", person.SavedContact.EmailAddresses);
        IsContactEditorOpen = true;
        RaisePropertyChanged(nameof(IsEditingContact));
        RaisePropertyChanged(nameof(IsConfirmingContactDelete));
        RaisePropertyChanged(nameof(IsContactPaneOpen));
        ((AsyncCommand)SaveContactCommand).Refresh();
        return Task.CompletedTask;
    }

    private Task OpenNewContactAsync()
    {
        _editingContact = null;
        _pendingDeleteContact = null;
        SelectedContactAccount = Accounts.FirstOrDefault();
        ContactName = "";
        ContactEmails = "";
        IsContactEditorOpen = true;
        RaisePropertyChanged(nameof(IsEditingContact));
        RaisePropertyChanged(nameof(IsConfirmingContactDelete));
        RaisePropertyChanged(nameof(IsContactPaneOpen));
        ((AsyncCommand)SaveContactCommand).Refresh();
        return Task.CompletedTask;
    }

    private Task CancelEditContactAsync()
    {
        _editingContact = null;
        _pendingDeleteContact = null;
        IsContactEditorOpen = false;
        RaisePropertyChanged(nameof(IsEditingContact));
        RaisePropertyChanged(nameof(IsConfirmingContactDelete));
        RaisePropertyChanged(nameof(IsContactPaneOpen));
        ((AsyncCommand)SaveContactCommand).Refresh();
        ((AsyncCommand)ConfirmDeleteContactCommand).Refresh();
        return Task.CompletedTask;
    }

    private async Task SaveContactAsync()
    {
        var contact = _editingContact?.SavedContact;
        var account = SelectedContactAccount;
        if (!IsContactEditorOpen || account is null || _workspaceProvider is null || _isContactActionRunning)
        {
            return;
        }

        try
        {
            _isContactActionRunning = true;
            RefreshContactCommands();
            var addresses = ComposeWindowViewModel.ParseRecipients(ContactEmails)
                .Select(static address => address.Address)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (addresses.Length == 0)
            {
                throw new InvalidOperationException("Add at least one email address.");
            }
            var draft = new ContactDraft(account.AccountId, ContactName.Trim(), addresses);
            if (contact is null)
            {
                await _workspaceProvider.CreateContactAsync(account, draft);
            }
            else
            {
                await _workspaceProvider.UpdateContactAsync(account, contact.ProviderId, draft);
            }
            _editingContact = null;
            IsContactEditorOpen = false;
            RaisePropertyChanged(nameof(IsEditingContact));
            await LoadPeopleAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PeopleErrorText = $"{account.EmailAddress}: contact could not be saved: {exception.Message}";
        }
        finally
        {
            _isContactActionRunning = false;
            RefreshContactCommands();
        }
    }

    private Task RequestDeleteContactAsync(PersonEntry person)
    {
        if (person.SavedContact is not null)
        {
            _pendingDeleteContact = person;
            IsContactEditorOpen = false;
            RaisePropertyChanged(nameof(IsConfirmingContactDelete));
            RaisePropertyChanged(nameof(IsContactPaneOpen));
            RaisePropertyChanged(nameof(ContactDeleteText));
            ((AsyncCommand)ConfirmDeleteContactCommand).Refresh();
        }
        return Task.CompletedTask;
    }

    private Task CancelDeleteContactAsync()
    {
        _pendingDeleteContact = null;
        RaisePropertyChanged(nameof(IsConfirmingContactDelete));
        RaisePropertyChanged(nameof(IsContactPaneOpen));
        RaisePropertyChanged(nameof(ContactDeleteText));
        ((AsyncCommand)ConfirmDeleteContactCommand).Refresh();
        return Task.CompletedTask;
    }

    private async Task ConfirmDeleteContactAsync()
    {
        var contact = _pendingDeleteContact?.SavedContact;
        var account = contact?.AccountId is null
            ? null
            : Accounts.FirstOrDefault(candidate => candidate.AccountId == contact.AccountId);
        if (contact is null || account is null || _workspaceProvider is null || _isContactActionRunning)
        {
            return;
        }

        try
        {
            _isContactActionRunning = true;
            RefreshContactCommands();
            await _workspaceProvider.DeleteContactAsync(account, contact);
            _pendingDeleteContact = null;
            RaisePropertyChanged(nameof(IsConfirmingContactDelete));
            RaisePropertyChanged(nameof(IsContactPaneOpen));
            RaisePropertyChanged(nameof(ContactDeleteText));
            await LoadPeopleAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            PeopleErrorText = $"{account.EmailAddress}: contact could not be deleted: {exception.Message}";
        }
        finally
        {
            _isContactActionRunning = false;
            RefreshContactCommands();
        }
    }

    private void RefreshContactCommands()
    {
        ((AsyncCommand)SaveContactCommand).Refresh();
        ((AsyncCommand)ConfirmDeleteContactCommand).Refresh();
        ((AsyncCommand)NewContactCommand).Refresh();
    }

    private static string NormalizeEmail(string value) => value.Trim().ToLowerInvariant();

    private async Task ToggleReadAsync()
    {
        var messages = ActionMessages();
        if (messages.Count == 0 || _isMailActionRunning)
        {
            return;
        }

        Error = null;
        var isRead = !messages[0].IsRead;
        BeginMailAction(isRead ? "Marking read..." : "Marking unread...");
        try
        {
            foreach (var message in messages)
            {
                await UpdateReadStateAsync(message, isRead);
            }
            Status = "Up to date";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            Status = "Action failed";
        }
        finally
        {
            EndMailAction();
        }
    }

    private bool CanRunSelectedMailAction() =>
        IsMailInteractionContext && ActionMessages().Count > 0 && _provider is not null && !_isMailActionRunning;

    private bool CanReplyToSelectedMessage() =>
        IsMailInteractionContext && ActionMessages().Count == 1;

    private bool CanViewHeaders() =>
        CanReplyToSelectedMessage() && !_isMailActionRunning;

    private bool IsMailInteractionContext => IsMailModule && !IsSettingsOpen && !IsGlobalSearchOpen;

    private IReadOnlyList<MailMessage> ActionMessages()
    {
        var selectedKeys = SelectedMessages.Select(MessageKey).ToHashSet(StringComparer.Ordinal);
        var current = Messages.Where(message => selectedKeys.Contains(MessageKey(message))).ToArray();
        return current.Length > 0
            ? current
            : SelectedMessage is null ? [] : [SelectedMessage];
    }

    private async Task MoveSelectedMessageAsync(
        string destinationFolderId,
        string actionStatus,
        string successStatus)
    {
        await MoveMessagesAsync(ActionMessages(), destinationFolderId, actionStatus, successStatus);
    }

    internal bool CanMoveSelectionToFolder(MailFolderItem? folder)
    {
        var messages = ActionMessages();
        return folder is not null && IsMailInteractionContext && !_isMailActionRunning &&
            messages.Count > 0 && messages.All(message => message.MailboxId == folder.MailboxId) &&
            messages.All(message => message.FolderId != folder.ProviderId);
    }

    internal Task MoveSelectionToFolderAsync(MailFolderItem folder) =>
        MoveMessagesAsync(
            ActionMessages(),
            folder.ProviderId,
            $"Moving to {folder.DisplayName}...",
            $"Moved to {folder.DisplayName}");

    private async Task MoveMessagesAsync(
        IReadOnlyList<MailMessage> messages,
        string destinationFolderId,
        string actionStatus,
        string successStatus)
    {
        if (messages.Count == 0 || _provider is null || _store is null || _isMailActionRunning)
        {
            return;
        }

        _selectionWorkCancellation?.Cancel();
        Error = null;
        BeginMailAction(actionStatus);
        try
        {
            var firstIndex = messages.Select(Messages.IndexOf).Where(static index => index >= 0).DefaultIfEmpty(0).Min();
            foreach (var message in messages)
            {
                if (!TryGetMessageContext(message, out var account, out var mailbox))
                {
                    continue;
                }
                var moved = message;
                if (!message.IsRead)
                {
                    await _provider.MarkReadAsync(account, mailbox, message.ProviderId, isRead: true);
                    moved = message with { IsRead = true };
                }
                await _provider.MoveMessageAsync(account, mailbox, message.ProviderId, destinationFolderId);
                await _store.ApplySyncPageAsync(
                    $"manual:{mailbox.Id}",
                    new MailSyncPage([moved with { IsDeleted = true }], null, false));
                var displayed = Messages.FirstOrDefault(candidate => SameMessage(candidate, message));
                if (displayed is not null)
                {
                    Messages.Remove(displayed);
                }
            }

            SelectedMessages.Clear();
            SelectedMessage = Messages.Count == 0 ? null : Messages[Math.Min(firstIndex, Messages.Count - 1)];
            RaiseMessageState();
            Status = successStatus;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            Status = "Action failed";
        }
        finally
        {
            EndMailAction();
        }
    }

    private async Task ToggleFlagAsync()
    {
        var messages = ActionMessages();
        if (messages.Count == 0 || _provider is null || _store is null || _isMailActionRunning)
        {
            return;
        }

        Error = null;
        var isFlagged = !messages[0].IsFlagged;
        BeginMailAction(isFlagged ? "Flagging message..." : "Clearing flag...");
        try
        {
            foreach (var message in messages)
            {
                if (!TryGetMessageContext(message, out var account, out var mailbox))
                {
                    continue;
                }
                var updated = message with { IsFlagged = isFlagged };
                await _provider.SetFlaggedAsync(account, mailbox, message.ProviderId, isFlagged);
                await _store.ApplySyncPageAsync($"manual:{mailbox.Id}", new MailSyncPage([updated], null, false));
                ApplyMessageUpdate(message, updated);
            }

            Status = isFlagged ? "Message flagged" : "Flag cleared";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            Status = "Action failed";
        }
        finally
        {
            EndMailAction();
        }
    }

    private void BeginMailAction(string status)
    {
        MailActionStatus = status;
        _isMailActionRunning = true;
        RaisePropertyChanged(nameof(IsMailActionRunning));
        RefreshMailActionCommands();
    }

    private void EndMailAction()
    {
        _isMailActionRunning = false;
        RaisePropertyChanged(nameof(IsMailActionRunning));
        RefreshMailActionCommands();
    }

    private void RefreshMailActionCommands()
    {
        ((AsyncCommand)ToggleReadCommand).Refresh();
        ((AsyncCommand)ArchiveCommand).Refresh();
        ((AsyncCommand)DeleteCommand).Refresh();
        ((AsyncCommand)JunkCommand).Refresh();
        ((AsyncCommand)NotJunkCommand).Refresh();
        ((AsyncCommand)ToggleFlagCommand).Refresh();
        ((AsyncCommand<MailFolderItem>)MoveToFolderCommand).Refresh();
        ((AsyncCommand)ViewHeadersCommand).Refresh();
    }

    private async Task ViewHeadersAsync()
    {
        var message = ConversationThread.SelectedMessage?.Message ?? SelectedMessage;
        if (message is null || _provider is null || _isMailActionRunning ||
            !TryGetMessageContext(message, out var account, out var mailbox))
        {
            return;
        }
        BeginMailAction("Loading message headers...");
        try
        {
            Status = "Loading message headers...";
            var headers = await _provider.GetMessageHeadersAsync(account, mailbox, message.ProviderId);
            HeadersRequested?.Invoke(new MailHeadersDocument(message.Subject, headers));
            Status = "Message headers loaded";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"Message headers could not be loaded: {exception.Message}";
            Status = "Header loading failed";
        }
        finally
        {
            EndMailAction();
        }
    }

    private async Task MarkReadAfterDelayAsync(
        MailMessage? message,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        if (message is null || message.IsRead)
        {
            return;
        }

        try
        {
            await Task.Delay(_markReadDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (selectionVersion != _selectionVersion || !IsCurrentMessage(message))
        {
            return;
        }

        try
        {
            await UpdateReadStateAsync(message, isRead: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsCurrentMessage(message))
            {
                Error = $"Message could not be marked as read: {exception.Message}";
            }
        }
    }

    private async Task UpdateReadStateAsync(
        MailMessage message,
        bool isRead,
        CancellationToken cancellationToken = default)
    {
        if (_provider is null || _store is null || !TryGetMessageContext(message, out var account, out var mailbox))
        {
            return;
        }

        var updated = message with { IsRead = isRead };
        await _provider.MarkReadAsync(account, mailbox, message.ProviderId, updated.IsRead, cancellationToken);
        await _store.ApplySyncPageAsync(
            $"manual:{mailbox.Id}",
            new MailSyncPage([updated], null, false),
            cancellationToken);
        ApplyMessageUpdate(message, updated);
    }

    private void ApplyMessageUpdate(MailMessage original, MailMessage updated)
    {
        var wasSelected = IsCurrentMessage(original);
        var selectedIndex = SelectedMessages.ToList().FindIndex(message => SameMessage(message, original));
        var index = Messages.ToList().FindIndex(message => SameMessage(message, original));
        if (index >= 0)
        {
            Messages[index] = updated;
        }
        if (wasSelected)
        {
            SelectedMessage = updated;
        }
        if (selectedIndex >= 0)
        {
            SelectedMessages[selectedIndex] = updated;
        }
    }

    private void ReconcileMessages(IReadOnlyList<MailMessage> updated)
    {
        var selected = SelectedMessage;
        var updatedKeys = updated.Select(MessageKey).ToHashSet(StringComparer.Ordinal);
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (!updatedKeys.Contains(MessageKey(Messages[index])))
            {
                Messages.RemoveAt(index);
            }
        }

        for (var index = 0; index < updated.Count; index++)
        {
            var message = updated[index];
            if (index >= Messages.Count)
            {
                Messages.Add(message);
                continue;
            }

            if (!SameMessage(Messages[index], message))
            {
                var existingIndex = IndexOfMessage(message, index + 1);
                if (existingIndex < 0)
                {
                    Messages.Insert(index, message);
                    continue;
                }

                Messages.Move(existingIndex, index);
            }

            if (Messages[index] != message)
            {
                var wasSelected = SameMessage(selected, message);
                _isReplacingSelectedMessage = wasSelected;
                try
                {
                    Messages[index] = message;
                }
                finally
                {
                    _isReplacingSelectedMessage = false;
                }
                if (wasSelected)
                {
                    SelectedMessage = message;
                }
            }
        }

        var current = selected is null ? null : Messages.FirstOrDefault(message => SameMessage(message, selected));
        SelectedMessage = current ?? Messages.FirstOrDefault();
    }

    private int IndexOfMessage(MailMessage message, int startIndex)
    {
        for (var index = startIndex; index < Messages.Count; index++)
        {
            if (SameMessage(Messages[index], message))
            {
                return index;
            }
        }
        return -1;
    }

    private static string MessageKey(MailMessage message) => $"{message.MailboxId}\n{message.ProviderId}";

    private async Task LoadAttachmentsAsync(
        MailMessage? message,
        CancellationToken cancellationToken = default)
    {
        var hasCidImages = message?.Body?.Contains("cid:", StringComparison.OrdinalIgnoreCase) == true;
        if (message is null || (!message.HasAttachments && !hasCidImages) || _provider is null ||
            !TryGetMessageContext(message, out var account, out var mailbox))
        {
            return;
        }

        IsLoadingAttachments = true;
        try
        {
            var attachments = await _provider.GetAttachmentsAsync(
                account,
                mailbox,
                message.ProviderId,
                cancellationToken);
            if (!IsCurrentMessage(message))
            {
                return;
            }

            Replace(Attachments, attachments);
            ConversationThread.SetAttachments(message, attachments);
            RaisePropertyChanged(nameof(SelectedMessageBodyUri));
            RaisePropertyChanged(nameof(AttachmentSummary));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsCurrentMessage(message))
            {
                Error = $"Attachments could not be loaded: {exception.Message}";
            }
        }
        finally
        {
            if (IsCurrentMessage(message))
            {
                IsLoadingAttachments = false;
            }
        }
    }

    private Task AllowRemoteContentAsync()
    {
        _allowRemoteContent = true;
        RaisePropertyChanged(nameof(SelectedMessageBodyUri));
        RaisePropertyChanged(nameof(HasBlockedRemoteContent));
        return Task.CompletedTask;
    }

    private bool TryGetMessageContext(MailMessage message, out MailAccount account, out Mailbox mailbox)
    {
        mailbox = Mailboxes.FirstOrDefault(candidate => candidate.Id == message.MailboxId)!;
        var accountId = mailbox?.AccountId;
        account = accountId is null
            ? null!
            : Accounts.FirstOrDefault(candidate => candidate.AccountId == accountId)!;
        return mailbox is not null && account is not null;
    }

    private bool IsCurrentMessage(MailMessage message) =>
        SelectedMessage?.MailboxId == message.MailboxId && SelectedMessage.ProviderId == message.ProviderId;

    private static bool SameMessage(MailMessage? left, MailMessage? right) =>
        left is not null && right is not null &&
        left.MailboxId == right.MailboxId &&
        left.ProviderId == right.ProviderId;

    private static string Initials(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "?";
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Concat(parts.Take(2).Select(static part => char.ToUpperInvariant(part[0])));
    }

    public async Task SendDraftAsync(ComposeSender sender, string localDraftId, DraftMessage draft)
    {
        if (_provider is null)
        {
            throw new InvalidOperationException("Connect a Microsoft 365 account before sending mail.");
        }

        var mailboxLock = _draftSyncLocks.GetOrAdd(sender.Mailbox.Id, static _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync();
        try
        {
            var local = await GetStoredDraftAsync(localDraftId);
            if (_provider.SupportsCloudDrafts &&
                local?.ProviderDraftId is { Length: > 0 } providerDraftId &&
                local.AccountId == sender.Account.AccountId &&
                local.MailboxId == sender.Mailbox.Id)
            {
                await _provider.UpdateDraftAsync(
                    sender.Account, sender.Mailbox, providerDraftId, draft);
                await _provider.SendDraftAsync(
                    sender.Account, sender.Mailbox, providerDraftId);
            }
            else
            {
                await _provider.SendAsync(sender.Account, sender.Mailbox, draft);
            }
            try
            {
                await DeleteLocalDraftRecordAsync(localDraftId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Error = $"Message sent, but its encrypted local draft could not be removed: {exception.Message}";
                Status = "Message sent; draft cleanup needs attention";
            }
            _ = SyncAsync();
        }
        finally
        {
            mailboxLock.Release();
        }
    }

    public void ConfigureSenderPreferences(
        string legacySignature,
        string? defaultSenderMailboxId,
        IReadOnlyDictionary<string, string>? senderSignatures,
        IReadOnlyList<SignaturePreference>? signatures = null,
        IReadOnlyDictionary<string, MailboxSignaturePreferences>? mailboxSignatures = null)
    {
        _defaultSenderMailboxId = defaultSenderMailboxId;
        _legacyFallbackSignatureId = null;
        _mailboxSignatures.Clear();
        while (Signatures.Count > 1)
        {
            SignatureChoices.Remove(Signatures[^1]);
            Signatures.RemoveAt(Signatures.Count - 1);
        }

        if (signatures is not null || mailboxSignatures is not null)
        {
            foreach (var preference in signatures ?? [])
            {
                if (string.IsNullOrWhiteSpace(preference.Id) || preference.Id == SignatureCatalog.DefaultId ||
                    Signatures.Any(candidate => candidate.Id == preference.Id))
                {
                    continue;
                }
                AddCustomSignature(new SignatureItem(
                    preference.Id,
                    UniqueSignatureName(preference.Name),
                    _renderer.SanitizeComposeHtml(preference.Html)));
            }
            foreach (var preference in mailboxSignatures ?? new Dictionary<string, MailboxSignaturePreferences>())
            {
                _mailboxSignatures[preference.Key] = NormalizeSignaturePreferences(preference.Value);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(legacySignature))
            {
                var imported = new SignatureItem(
                    Guid.NewGuid().ToString("N"),
                    UniqueSignatureName("Imported default"),
                    _renderer.PrepareComposeHtml(legacySignature, isHtml: false));
                AddCustomSignature(imported);
                _legacyFallbackSignatureId = imported.Id;
            }
            foreach (var preference in senderSignatures ?? new Dictionary<string, string>())
            {
                if (string.IsNullOrWhiteSpace(preference.Value))
                {
                    continue;
                }
                var imported = new SignatureItem(
                    Guid.NewGuid().ToString("N"),
                    UniqueSignatureName("Imported signature"),
                    _renderer.PrepareComposeHtml(preference.Value, isHtml: false));
                AddCustomSignature(imported);
                _mailboxSignatures[preference.Key] = AllSignatureActions(imported.Id);
            }
        }
        RebuildSenderSettings();
        SelectedSignature = Signatures[0];
        RaiseSenderPreferencesChanged();
    }

    public List<SignaturePreference> GetSignaturePreferences() =>
        Signatures.Where(signature =>
                !signature.IsReadOnly &&
                (!_isCreatingSignature || signature != SelectedSignature))
            .Select(static signature => signature.ToPreference()).ToList();

    public Dictionary<string, MailboxSignaturePreferences> GetMailboxSignaturePreferences() =>
        new(_mailboxSignatures, StringComparer.Ordinal);

    public Dictionary<string, string> GetSenderSignatures() => SenderSettings
        .Where(static sender => !string.IsNullOrWhiteSpace(sender.NewMailSignature.Html))
        .ToDictionary(static sender => sender.MailboxId, static sender => sender.NewMailSignature.Html, StringComparer.Ordinal);

    public SignatureContent? SignatureForSender(ComposeSender sender, ComposeIntent intent)
    {
        var preferences = _mailboxSignatures.TryGetValue(sender.Mailbox.Id, out var saved)
            ? saved
            : new MailboxSignaturePreferences();
        var id = preferences.For(intent);
        var signature = Signatures.FirstOrDefault(candidate => candidate.Id == id);
        return signature is null ? null : new SignatureContent(signature.Id, signature.Html);
    }

    private Task SetDefaultSenderAsync(SenderSettingsItem item)
    {
        _defaultSenderMailboxId = item.MailboxId;
        foreach (var sender in SenderSettings)
        {
            sender.IsDefault = sender.MailboxId == _defaultSenderMailboxId;
        }
        RaiseSenderPreferencesChanged();
        return Task.CompletedTask;
    }

    private void RebuildSenderSettings()
    {
        var senders = (
            from mailbox in Mailboxes
            join account in Accounts on mailbox.AccountId equals account.AccountId
            where !mailbox.IsShared || mailbox.CanSendAs || mailbox.CanSendOnBehalf
            select new ComposeSender(account, mailbox)).ToArray();
        var addedDefaults = false;
        foreach (var sender in senders)
        {
            if (_mailboxSignatures.ContainsKey(sender.Mailbox.Id))
            {
                continue;
            }
            _mailboxSignatures[sender.Mailbox.Id] = AllSignatureActions(
                _legacyFallbackSignatureId ?? SignatureCatalog.DefaultId);
            addedDefaults = true;
        }
        Replace(SenderSettings, senders.Select(sender => new SenderSettingsItem(
            sender,
            sender.Mailbox.Id == _defaultSenderMailboxId,
            SignatureChoices,
            _mailboxSignatures[sender.Mailbox.Id],
            OnSenderSignatureChanged)));
        Replace(SignatureAccountSettings, Accounts.Select(account => new SignatureAccountSettingsItem(
            account,
            SenderSettings.Where(sender => sender.Account.AccountId == account.AccountId).ToArray())));
        if (addedDefaults)
        {
            RaiseSenderPreferencesChanged();
        }
    }

    private void OnSenderSignatureChanged(SenderSettingsItem item)
    {
        _mailboxSignatures[item.MailboxId] = item.ToPreferences();
        RaiseSenderPreferencesChanged();
    }

    private static MailboxSignaturePreferences AllSignatureActions(string? signatureId) =>
        new(signatureId, signatureId, signatureId, signatureId);

    private MailboxSignaturePreferences NormalizeSignaturePreferences(MailboxSignaturePreferences preferences)
    {
        string? Known(string? id) => string.IsNullOrWhiteSpace(id)
            ? null
            : Signatures.Any(signature => signature.Id == id) ? id : SignatureCatalog.DefaultId;
        return new(
            Known(preferences.NewMailSignatureId),
            Known(preferences.ReplySignatureId),
            Known(preferences.ReplyAllSignatureId),
            Known(preferences.ForwardSignatureId));
    }

    private Task OpenSignatureTemplatesAsync()
    {
        SelectedSignatureTemplate ??=
            SignatureTemplates.FirstOrDefault(static template => template.Id == "minimal");
        IsSignatureTemplatePickerOpen = true;
        SignatureEditorError = null;
        return Task.CompletedTask;
    }

    private Task CloseSignatureTemplatesAsync()
    {
        IsSignatureTemplatePickerOpen = false;
        return Task.CompletedTask;
    }

    private Task CreateSignatureFromTemplateAsync()
    {
        if (SelectedSignatureTemplate is not { } template)
        {
            return Task.CompletedTask;
        }
        var signature = new SignatureItem(
            Guid.NewGuid().ToString("N"),
            UniqueSignatureName(template.Id == "blank" ? "New signature" : template.Name),
            _renderer.SanitizeComposeHtml(template.Html));
        IsSignatureTemplatePickerOpen = false;
        BeginNewSignature(signature);
        return Task.CompletedTask;
    }

    private Task SaveSignatureAsync()
    {
        var signature = SelectedSignature;
        var name = SignatureEditorName.Trim();
        if (signature?.CanEdit != true)
        {
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            SignatureEditorError = "Give the signature a name.";
            return Task.CompletedTask;
        }
        if (Signatures.Any(candidate => candidate != signature &&
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            SignatureEditorError = "Signature names must be unique.";
            return Task.CompletedTask;
        }
        signature.Name = name;
        signature.Html = _renderer.SanitizeComposeHtml(SignatureEditorHtml);
        if (_isCreatingSignature)
        {
            _isCreatingSignature = false;
            SignatureChoices.Add(signature);
        }
        SignatureEditorHtml = signature.Html;
        SignatureEditorError = null;
        RefreshSignatureEditorState();
        RaiseSenderPreferencesChanged();
        return Task.CompletedTask;
    }

    private Task ResetSignatureAsync()
    {
        if (_isCreatingSignature && SelectedSignature is { } draft)
        {
            _isCreatingSignature = false;
            Signatures.Remove(draft);
            SelectedSignature = Signatures.FirstOrDefault();
            return Task.CompletedTask;
        }
        LoadSelectedSignatureEditor();
        return Task.CompletedTask;
    }

    private Task DuplicateSignatureAsync()
    {
        if (SelectedSignature is not { } source)
        {
            return Task.CompletedTask;
        }
        var copy = new SignatureItem(
            Guid.NewGuid().ToString("N"),
            UniqueSignatureName($"{source.Name} copy"),
            source.Html);
        BeginNewSignature(copy);
        return Task.CompletedTask;
    }

    private Task RequestDeleteSignatureAsync()
    {
        if (SelectedSignature is not { CanEdit: true } signature)
        {
            return Task.CompletedTask;
        }
        _pendingSignatureDelete = signature;
        RaisePropertyChanged(nameof(IsConfirmingSignatureDelete));
        RaisePropertyChanged(nameof(SignatureDeleteText));
        RefreshSignatureEditorState();
        return Task.CompletedTask;
    }

    private Task CancelDeleteSignatureAsync()
    {
        ClearSignatureDeleteConfirmation();
        RefreshSignatureEditorState();
        return Task.CompletedTask;
    }

    private Task ConfirmDeleteSignatureAsync()
    {
        if (_pendingSignatureDelete is not { } signature)
        {
            return Task.CompletedTask;
        }
        ClearSignatureDeleteConfirmation();
        foreach (var mailboxId in _mailboxSignatures.Keys.ToArray())
        {
            var current = _mailboxSignatures[mailboxId];
            string? Clear(string? id) => id == signature.Id ? null : id;
            _mailboxSignatures[mailboxId] = new(
                Clear(current.NewMailSignatureId),
                Clear(current.ReplySignatureId),
                Clear(current.ReplyAllSignatureId),
                Clear(current.ForwardSignatureId));
        }
        Signatures.Remove(signature);
        SignatureChoices.Remove(signature);
        RebuildSenderSettings();
        if (SelectedSignature == signature)
        {
            SelectedSignature = Signatures.FirstOrDefault();
        }
        RaiseSenderPreferencesChanged();
        return Task.CompletedTask;
    }

    private int SignatureUsageCount(SignatureItem signature) =>
        _mailboxSignatures.Values.Count(preferences =>
            preferences.NewMailSignatureId == signature.Id ||
            preferences.ReplySignatureId == signature.Id ||
            preferences.ReplyAllSignatureId == signature.Id ||
            preferences.ForwardSignatureId == signature.Id);

    private void BeginNewSignature(SignatureItem signature)
    {
        Signatures.Add(signature);
        SelectedSignature = signature;
        _isCreatingSignature = true;
        RefreshSignatureEditorState();
    }

    private void AddCustomSignature(SignatureItem signature)
    {
        Signatures.Add(signature);
        SignatureChoices.Add(signature);
    }

    private string UniqueSignatureName(string proposed)
    {
        var root = string.IsNullOrWhiteSpace(proposed) ? "Signature" : proposed.Trim();
        var name = root;
        for (var number = 2; Signatures.Any(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); number++)
        {
            name = $"{root} {number}";
        }
        return name;
    }

    private void LoadSelectedSignatureEditor()
    {
        ClearSignatureDeleteConfirmation();
        SignatureEditorName = SelectedSignature?.Name ?? "";
        SignatureEditorHtml = SelectedSignature?.Html ?? "";
        SignatureEditorError = null;
    }

    private void SignatureEditorChanged()
    {
        ClearSignatureDeleteConfirmation();
        RefreshSignatureEditorState();
    }

    private void ClearSignatureDeleteConfirmation()
    {
        if (_pendingSignatureDelete is null)
        {
            return;
        }
        _pendingSignatureDelete = null;
        RaisePropertyChanged(nameof(IsConfirmingSignatureDelete));
        RaisePropertyChanged(nameof(SignatureDeleteText));
    }

    private void RefreshSignatureEditorState()
    {
        RaisePropertyChanged(nameof(HasUnsavedSignatureChanges));
        RaisePropertyChanged(nameof(CanManageSavedSignature));
        RaisePropertyChanged(nameof(CanDeleteSelectedSignature));
        ((AsyncCommand)SaveSignatureCommand).Refresh();
        ((AsyncCommand)ResetSignatureCommand).Refresh();
        ((AsyncCommand)DuplicateSignatureCommand).Refresh();
        ((AsyncCommand)DeleteSignatureCommand).Refresh();
        ((AsyncCommand)ConfirmDeleteSignatureCommand).Refresh();
    }

    private void RaiseSenderPreferencesChanged()
    {
        _senderPreferencesVersion++;
        RaisePropertyChanged(nameof(SenderPreferencesVersion));
        RaisePropertyChanged(nameof(DefaultSenderMailboxId));
    }

    public async Task SaveLocalDraftAsync(LocalDraft draft)
    {
        if (_store is null)
        {
            return;
        }

        await _store.SaveLocalDraftAsync(draft);
        var existing = Drafts.FirstOrDefault(candidate => candidate.Id == draft.Id);
        if (existing is not null)
        {
            Drafts.Remove(existing);
        }
        Drafts.Insert(0, draft);
        RaiseDraftState();
    }

    public async Task DeleteLocalDraftAsync(string id)
    {
        if (_store is null)
        {
            return;
        }

        var draft = await GetStoredDraftAsync(id);
        if (_provider?.SupportsCloudDrafts == true &&
            draft?.ProviderDraftId is { Length: > 0 } providerDraftId &&
            TryGetDraftContext(draft, out var account, out var mailbox))
        {
            var mailboxLock = _draftSyncLocks.GetOrAdd(mailbox.Id, static _ => new SemaphoreSlim(1, 1));
            await mailboxLock.WaitAsync();
            try
            {
                draft = await GetStoredDraftAsync(id);
                if (draft?.ProviderDraftId is { Length: > 0 } currentProviderDraftId)
                {
                    await _provider.DeleteDraftAsync(account, mailbox, currentProviderDraftId);
                }
                await DeleteLocalDraftRecordAsync(id);
            }
            finally
            {
                mailboxLock.Release();
            }
            return;
        }

        await DeleteLocalDraftRecordAsync(id);
    }

    private async Task<LocalDraft?> GetStoredDraftAsync(string id) =>
        _store is null
            ? null
            : (await _store.GetLocalDraftsAsync()).FirstOrDefault(candidate => candidate.Id == id);

    private async Task DeleteLocalDraftRecordAsync(string id)
    {
        if (_store is null)
        {
            return;
        }
        await _store.DeleteLocalDraftAsync(id);
        var existing = Drafts.FirstOrDefault(candidate => candidate.Id == id);
        if (existing is not null)
        {
            Drafts.Remove(existing);
            RaiseDraftState();
        }
    }

    private bool TryGetDraftContext(LocalDraft draft, out MailAccount account, out Mailbox mailbox)
    {
        account = Accounts.FirstOrDefault(candidate => candidate.AccountId == draft.AccountId)!;
        mailbox = Mailboxes.FirstOrDefault(candidate => candidate.Id == draft.MailboxId)!;
        return account is not null && mailbox is not null && mailbox.AccountId == account.AccountId;
    }

    private Task OpenLocalDraftAsync(LocalDraft draft)
    {
        IsSettingsOpen = false;
        ActiveModule = "Mail";
        ComposeRequested?.Invoke(new ComposeRequest(
            draft.To,
            draft.Subject,
            draft.Body,
            draft.Cc,
            draft.Bcc,
            draft.Id,
            draft.AccountId,
            draft.MailboxId,
            draft.Attachments,
            draft.IsHtml));

        return Task.CompletedTask;
    }

    private Task ReplyAsync()
    {
        var message = SelectedMessage ?? throw new InvalidOperationException("Select a message first.");
        return ReplyToAsync(message);
    }

    private async Task ReplyToAsync(MailMessage message)
    {
        var attachments = await LoadComposeSourceAttachmentsAsync(message, includeFiles: false);
        if (attachments is null)
        {
            return;
        }
        var (accountId, mailboxId) = ReplySender(message);
        await RequestComposeAsync(new ComposeRequest(
            message.From.Address,
            PrefixSubject(message.Subject, "Re:"),
            Body: _renderer.PrepareQuotedMessageHtml(message, attachments),
            AccountId: accountId,
            MailboxId: mailboxId,
            IsHtml: true,
            Intent: ComposeIntent.Reply));
    }

    private Task ReplyAllAsync()
    {
        var message = SelectedMessage ?? throw new InvalidOperationException("Select a message first.");
        return ReplyAllToAsync(message);
    }

    private async Task ReplyAllToAsync(MailMessage message)
    {
        var recipients = MailReplyRecipients.ReplyAll(
            message,
            Accounts.Select(static account => account.EmailAddress)
                .Concat(Mailboxes.Select(static mailbox => mailbox.Address)));
        if (recipients.Count == 0)
        {
            Error = "Reply all has no external recipients after removing your linked addresses.";
            return;
        }

        var attachments = await LoadComposeSourceAttachmentsAsync(message, includeFiles: false);
        if (attachments is null)
        {
            return;
        }
        var (accountId, mailboxId) = ReplySender(message);
        await RequestComposeAsync(new ComposeRequest(
            recipients[0],
            PrefixSubject(message.Subject, "Re:"),
            Body: _renderer.PrepareQuotedMessageHtml(message, attachments),
            Cc: string.Join("; ", recipients.Skip(1)),
            AccountId: accountId,
            MailboxId: mailboxId,
            IsHtml: true,
            Intent: ComposeIntent.ReplyAll));
    }

    private Task ForwardAsync()
    {
        var message = SelectedMessage ?? throw new InvalidOperationException("Select a message first.");
        return ForwardMessageAsync(message);
    }

    private async Task ForwardMessageAsync(MailMessage message)
    {
        var attachments = await LoadComposeSourceAttachmentsAsync(message, includeFiles: true);
        if (attachments is null)
        {
            return;
        }
        var subject = PrefixSubject(message.Subject, "Fwd:");
        await RequestComposeAsync(new ComposeRequest(
            Subject: subject,
            Body: _renderer.PrepareQuotedMessageHtml(message, attachments),
            Attachments: attachments
                .Where(static attachment => !attachment.IsInline && attachment.ContentBytes is { Length: > 0 })
                .Select(static attachment => new DraftAttachment(
                    attachment.Name,
                    attachment.ContentType,
                    attachment.ContentBytes!))
                .ToArray(),
            IsHtml: true,
            Intent: ComposeIntent.Forward));
    }

    private async Task<IReadOnlyList<MailAttachment>?> LoadComposeSourceAttachmentsAsync(
        MailMessage message,
        bool includeFiles)
    {
        var hasCidImages = _renderer.HasCidImages(message.Body, message.IsHtml);
        if (!hasCidImages && (!includeFiles || !message.HasAttachments))
        {
            return [];
        }
        if (IsCurrentMessage(message) && Attachments.Count > 0)
        {
            return Attachments.ToArray();
        }
        if (_provider is null || !TryGetMessageContext(message, out var account, out var mailbox))
        {
            Error = "Original message pictures could not be loaded.";
            return null;
        }
        try
        {
            return await _provider.GetAttachmentsAsync(account, mailbox, message.ProviderId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"Original message pictures could not be loaded: {exception.Message}";
            return null;
        }
    }

    private Task SelectAdjacentMessageAsync(int offset)
    {
        if (SelectedMessage is null || Messages.Count < 2)
        {
            return Task.CompletedTask;
        }
        var index = Messages.IndexOf(SelectedMessage);
        if (index < 0)
        {
            return Task.CompletedTask;
        }
        var next = Math.Clamp(index + offset, 0, Messages.Count - 1);
        if (next != index)
        {
            SelectedMessage = Messages[next];
        }
        return Task.CompletedTask;
    }

    private (string? AccountId, string? MailboxId) ReplySender(MailMessage message) =>
        TryGetMessageContext(message, out var account, out var mailbox)
            ? (account.AccountId, mailbox.Id)
            : (null, null);

    private static string PrefixSubject(string subject, string prefix) =>
        subject.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? subject
            : string.IsNullOrWhiteSpace(subject) ? prefix : $"{prefix} {subject}";

    private Task RequestComposeAsync(ComposeRequest request)
    {
        if (Accounts.Count == 0)
        {
            Error = "Connect a Microsoft 365 account before composing mail.";
            return Task.CompletedTask;
        }

        if (request.MailboxId is null)
        {
            var sender = SenderSettings.FirstOrDefault(candidate => candidate.IsDefault) ??
                SenderSettings.FirstOrDefault();
            if (sender is not null)
            {
                request = request with
                {
                    AccountId = sender.Account.AccountId,
                    MailboxId = sender.Mailbox.Id
                };
            }
        }

        ComposeRequested?.Invoke(request);
        return Task.CompletedTask;
    }

    private async Task RunBusyAsync(string status, Func<Task> work)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = status;
        Error = null;
        try
        {
            await work();
            Status = "Up to date";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
            Status = "Action failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseMessageState()
    {
        if (Messages.Count == 0 && SelectedMessage is not null)
        {
            SelectedMessage = null;
        }
        else
        {
            ReconcileConversation(SelectedMessage);
        }
        RaisePropertyChanged(nameof(MessageCountText));
        RaisePropertyChanged(nameof(ShowEmptyState));
    }

    private void ReconcileConversation(MailMessage? selected)
    {
        if (selected is null)
        {
            ConversationThread.Reconcile([], null);
            return;
        }

        var threadIdentity = BetterMail.Core.ConversationThread.ThreadIdentity(selected);
        var messages = _store is null
            ? Messages.Where(message => BetterMail.Core.ConversationThread.ThreadIdentity(message) == threadIdentity).ToList()
            : ConversationThread.SelectedThread?.Identity == threadIdentity
                ? ConversationThread.SelectedThread.Messages.Select(item => item.Message).ToList()
                : [selected];
        for (var index = 0; index < messages.Count; index++)
        {
            if (SameMessage(messages[index], selected))
            {
                messages[index] = selected;
            }
        }
        if (messages.All(message => !SameMessage(message, selected)))
        {
            messages.Add(selected);
        }
        ConversationThread.Reconcile(messages, selected);
    }

    private async Task LoadConversationAsync(
        MailMessage? selected,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        if (selected is null || _store is null)
        {
            return;
        }
        try
        {
            var messages = (await _store.GetThreadMessagesAsync(
                    BetterMail.Core.ConversationThread.ThreadIdentity(selected), cancellationToken))
                .Select(message => SameMessage(message, selected) ? selected : message)
                .ToList();
            if (selectionVersion != _selectionVersion || !IsCurrentMessage(selected))
            {
                return;
            }
            if (messages.All(message => !SameMessage(message, selected)))
            {
                messages.Add(selected);
            }
            ConversationThread.Reconcile(messages, selected);
            ConversationThread.SetAttachments(selected, Attachments.ToArray());
        }
        catch (OperationCanceledException)
        {
            // A newer selection owns the reading pane now.
        }
    }

    private void HandleConversationAction(ConversationActionRequest request)
    {
        _ = request.Action switch
        {
            ConversationAction.Reply => ReplyToAsync(request.Message),
            ConversationAction.ReplyAll => ReplyAllToAsync(request.Message),
            ConversationAction.Forward => ForwardMessageAsync(request.Message),
            _ => Task.CompletedTask
        };
    }

    private void RaiseDraftState()
    {
        RaisePropertyChanged(nameof(DraftCountText));
        RaisePropertyChanged(nameof(HasDrafts));
        RaisePropertyChanged(nameof(CurrentItemCountText));
        RaisePropertyChanged(nameof(ShowDraftEmptyState));
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source)
        {
            destination.Add(item);
        }
    }
}

public sealed record MailFolderItem(MailFolder Folder, string MailboxDisplayName)
{
    public string MailboxId => Folder.MailboxId;
    public string ProviderId => Folder.ProviderId;
    public string DisplayName => Folder.DisplayName;
    public int UnreadCount => Folder.UnreadCount;
    public int TotalCount => Folder.TotalCount;
    public string? WellKnownName => Folder.WellKnownName;
    public string? ParentProviderId => Folder.ParentProviderId;
    public string CountText => UnreadCount > 0 ? UnreadCount.ToString("N0") : "";
}

public sealed record MailFolderNode(MailFolderItem Item, IReadOnlyList<MailFolderNode> Children)
{
    public string DisplayName => Item.DisplayName;
    public string CountText => Item.CountText;
}

public sealed record MailboxFolderGroup(Mailbox Mailbox, IReadOnlyList<MailFolderNode> Folders)
{
    public string DisplayName => Mailbox.DisplayName;
    public string Address => Mailbox.Address;
    public string Color => AccountColors.For(Mailbox.Id);
}

public sealed record AccountSettingsItem(MailAccount Account, IReadOnlyList<Mailbox> SharedMailboxes)
{
    public string DisplayName => Account.DisplayName;
    public string EmailAddress => Account.EmailAddress;
    public bool HasSharedMailboxes => SharedMailboxes.Count > 0;
}

public sealed record SettingsTabItem(string Name);

public sealed record MailQuickActionOption(
    string Id,
    string Label,
    string Icon,
    bool IsMove = false,
    bool IsMore = false)
{
    public bool IsStandard => !IsMove && !IsMore;
}

public sealed class MailQuickActionSlot(
    int position,
    MailQuickActionOption selectedOption,
    Action changed) : ViewModelBase
{
    private MailQuickActionOption _selectedOption = selectedOption;

    public string Label => $"Button {position}";

    public MailQuickActionOption SelectedOption
    {
        get => _selectedOption;
        set
        {
            if (SetProperty(ref _selectedOption, value))
            {
                changed();
            }
        }
    }
}

public sealed record SignatureAccountSettingsItem(
    MailAccount Account,
    IReadOnlyList<SenderSettingsItem> Mailboxes)
{
    public string DisplayName => Account.DisplayName;
    public string EmailAddress => Account.EmailAddress;
}

public sealed record MailboxStatisticsItem(
    string DisplayName,
    string Address,
    bool IsShared,
    int SyncedMessages,
    int CloudMessages,
    int SyncedUnread,
    int CloudUnread,
    int Flagged,
    int FolderCount)
{
    public string MailboxType => IsShared ? "Shared mailbox" : "Mailbox";
    public string MessageCounts => $"{SyncedMessages:N0} synced locally | {CloudMessages:N0} on Microsoft 365";
    public string DetailCounts =>
        $"Unread: {SyncedUnread:N0} local / {CloudUnread:N0} cloud | {Flagged:N0} flagged | {FolderCount:N0} folders";
}

public sealed class SenderSettingsItem(
    ComposeSender sender,
    bool isDefault,
    ObservableCollection<SignatureItem> signatureChoices,
    MailboxSignaturePreferences preferences,
    Action<SenderSettingsItem> signatureChanged) : ViewModelBase
{
    private SignatureItem _newMailSignature = Choice(signatureChoices, preferences.NewMailSignatureId);
    private SignatureItem _replySignature = Choice(signatureChoices, preferences.ReplySignatureId);
    private SignatureItem _replyAllSignature = Choice(signatureChoices, preferences.ReplyAllSignatureId);
    private SignatureItem _forwardSignature = Choice(signatureChoices, preferences.ForwardSignatureId);
    private bool _isDefault = isDefault;

    public MailAccount Account => sender.Account;
    public Mailbox Mailbox => sender.Mailbox;
    public string MailboxId => Mailbox.Id;
    public string DisplayName => sender.DisplayName;
    public string MailboxType => Mailbox.IsShared ? "Shared mailbox" : "Primary mailbox";
    public ObservableCollection<SignatureItem> SignatureChoices => signatureChoices;

    public SignatureItem NewMailSignature
    {
        get => _newMailSignature;
        set
        {
            if (SetProperty(ref _newMailSignature, value))
            {
                signatureChanged(this);
            }
        }
    }

    public SignatureItem ReplySignature
    {
        get => _replySignature;
        set
        {
            if (SetProperty(ref _replySignature, value))
            {
                signatureChanged(this);
            }
        }
    }

    public SignatureItem ReplyAllSignature
    {
        get => _replyAllSignature;
        set
        {
            if (SetProperty(ref _replyAllSignature, value))
            {
                signatureChanged(this);
            }
        }
    }

    public SignatureItem ForwardSignature
    {
        get => _forwardSignature;
        set
        {
            if (SetProperty(ref _forwardSignature, value))
            {
                signatureChanged(this);
            }
        }
    }

    public bool IsDefault
    {
        get => _isDefault;
        internal set
        {
            if (SetProperty(ref _isDefault, value))
            {
                RaisePropertyChanged(nameof(DefaultText));
            }
        }
    }

    public string DefaultText => IsDefault ? "Default sender" : "Make default";

    public MailboxSignaturePreferences ToPreferences() => new(
        IdOrNone(NewMailSignature),
        IdOrNone(ReplySignature),
        IdOrNone(ReplyAllSignature),
        IdOrNone(ForwardSignature));

    private static string? IdOrNone(SignatureItem signature) =>
        string.IsNullOrWhiteSpace(signature.Id) ? null : signature.Id;

    private static SignatureItem Choice(ObservableCollection<SignatureItem> choices, string? id) =>
        choices.FirstOrDefault(candidate => candidate.Id == id) ?? choices[0];
}

public sealed record PersonEntry(
    string DisplayName,
    string EmailText,
    string KindText,
    string ProvenanceText,
    int Frequency,
    string ActivityText,
    ContactInfo? SavedContact,
    DiscoveredPerson? DiscoveredPerson)
{
    public bool IsSaved => SavedContact is not null;
    public string AvatarText => string.IsNullOrWhiteSpace(DisplayName)
        ? "?"
        : char.ToUpperInvariant(DisplayName.Trim()[0]).ToString();

    public static PersonEntry Saved(ContactInfo contact, string account) => new(
        string.IsNullOrWhiteSpace(contact.DisplayName) ? contact.EmailText : contact.DisplayName,
        contact.EmailText,
        "Saved",
        account,
        0,
        "Saved contact",
        contact,
        null);

    public static PersonEntry Discovered(DiscoveredPerson person, string provenance) => new(
        person.DisplayName,
        person.EmailAddress,
        "Discovered",
        string.IsNullOrWhiteSpace(provenance) ? "Mail history" : provenance,
        person.Frequency,
        $"Seen {person.Frequency:N0} times · last {person.LastContactedAt.ToLocalTime():g}",
        null,
        person);
}

internal sealed record AccountContactResult(
    MailAccount Account,
    IReadOnlyList<ContactInfo> Contacts,
    string? Error);

internal sealed record CachedMailPreview(
    MailMessage Selected,
    IReadOnlyList<MailMessage> Messages);

public sealed record GlobalSearchResult(
    string Category,
    string Title,
    string Subtitle,
    string Module,
    object? Value = null,
    bool StartsCategory = false,
    string AccountGroup = "All accounts",
    bool StartsAccountGroup = false)
{
    public string Glyph => Module switch
    {
        "Mail" => "✉",
        "People" => "♙",
        "Calendar" => "▦",
        "To Do" => "✓",
        "OneDrive" => "☁",
        "Notes" => "▤",
        _ => "•"
    };
}

public sealed record SearchAccountFilter(string DisplayName, string? AccountId);
public sealed record SearchFolderFilter(string DisplayName, string? MailboxId, string? FolderId);
