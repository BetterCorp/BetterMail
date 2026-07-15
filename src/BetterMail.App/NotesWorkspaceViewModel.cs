using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class NotesWorkspaceViewModel : ViewModelBase
{
    private readonly INotesProvider _provider;
    private IReadOnlyList<MailAccount> _accounts;
    private readonly MailContentRenderer _renderer;
    private NoteTreeNode? _selectedNode;
    private NoteTreeNode? _editorSection;
    private NoteTreeNode? _editingPage;
    private NoteTreeNode? _pendingDelete;
    private string _searchText = "";
    private string _editorTitle = "";
    private string _editorBody = "";
    private string? _operationError;
    private string? _editorError;
    private string? _rawPageHtml;
    private Uri? _pageBodyUri;
    private bool _isLoadingPage;
    private bool _isEditorOpen;
    private bool _allowRemoteContent;
    private int _selectionVersion;

    public NotesWorkspaceViewModel(
        INotesProvider provider,
        IReadOnlyList<MailAccount> accounts,
        MailContentRenderer? renderer = null)
    {
        _provider = provider;
        _accounts = accounts;
        _renderer = renderer ?? new MailContentRenderer();
        RefreshCommand = new AsyncCommand(RefreshAsync);
        SearchCommand = new AsyncCommand(SearchAsync);
        NewPageCommand = new AsyncCommand(OpenNewPageAsync, () => SelectedSection is not null);
        EditPageCommand = new AsyncCommand(OpenEditPageAsync, () => SelectedPage is not null);
        SaveEditorCommand = new AsyncCommand(SaveEditorAsync);
        CloseEditorCommand = new AsyncCommand(CloseEditorAsync);
        RequestDeleteCommand = new AsyncCommand(RequestDeleteAsync, () => SelectedPage is not null);
        ConfirmDeleteCommand = new AsyncCommand(ConfirmDeleteAsync, () => PendingDelete is not null);
        CancelDeleteCommand = new AsyncCommand(CancelDeleteAsync);
        AllowRemoteContentCommand = new AsyncCommand(AllowRemoteContentAsync, () => HasBlockedRemoteContent);
    }

    public ObservableCollection<NoteTreeNode> AccountRoots { get; } = [];
    public ObservableCollection<NoteTreeNode> VisibleRoots { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand NewPageCommand { get; }
    public ICommand EditPageCommand { get; }
    public ICommand SaveEditorCommand { get; }
    public ICommand CloseEditorCommand { get; }
    public ICommand RequestDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand AllowRemoteContentCommand { get; }

    public NoteTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!SetProperty(ref _selectedNode, value))
            {
                return;
            }
            RefreshCommands();
            _ = LoadSelectedPageAsync(value);
        }
    }

    public NoteSection? SelectedSection =>
        SelectedNode?.Section ?? SelectedNode?.Parent?.Section;
    public NotePage? SelectedPage => SelectedNode?.Page;
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string? OperationError
    {
        get => _operationError;
        private set
        {
            if (SetProperty(ref _operationError, value))
            {
                RaisePropertyChanged(nameof(HasOperationError));
            }
        }
    }
    public bool HasOperationError => !string.IsNullOrWhiteSpace(OperationError);
    public bool HasPartialErrors => AccountRoots.Any(static root => root.HasError);
    public string PartialErrorText => string.Join(
        Environment.NewLine,
        AccountRoots.SelectMany(static root => root.Errors()));
    public bool IsLoadingPage { get => _isLoadingPage; private set => SetProperty(ref _isLoadingPage, value); }
    public Uri? PageBodyUri { get => _pageBodyUri; private set => SetProperty(ref _pageBodyUri, value); }
    public bool HasSelectedPage => SelectedPage is not null;
    public bool HasNoSelectedPage => SelectedPage is null;
    public bool HasBlockedRemoteContent =>
        !_allowRemoteContent && _renderer.HasRemoteImages(_rawPageHtml, isHtml: true);
    public bool IsEditorOpen { get => _isEditorOpen; private set => SetProperty(ref _isEditorOpen, value); }
    public bool IsEditing => _editingPage is not null;
    public string EditorHeading => IsEditing ? "Edit OneNote page" : "New OneNote page";
    public string EditorBodyLabel => IsEditing ? "Append text" : "Page text";
    public string EditorHelpText => IsEditing
        ? "OneNote supports changing the title and appending text; it does not replace the whole page body."
        : "Text is encoded before it is sent to OneNote.";
    public string EditorTitle { get => _editorTitle; set => SetProperty(ref _editorTitle, value); }
    public string EditorBody { get => _editorBody; set => SetProperty(ref _editorBody, value); }
    public string? EditorError
    {
        get => _editorError;
        private set
        {
            if (SetProperty(ref _editorError, value))
            {
                RaisePropertyChanged(nameof(HasEditorError));
            }
        }
    }
    public bool HasEditorError => !string.IsNullOrWhiteSpace(EditorError);
    public NoteTreeNode? PendingDelete
    {
        get => _pendingDelete;
        private set
        {
            if (SetProperty(ref _pendingDelete, value))
            {
                RaisePropertyChanged(nameof(IsDeleteConfirmationOpen));
                RaisePropertyChanged(nameof(DeleteConfirmationText));
                ((AsyncCommand)ConfirmDeleteCommand).Refresh();
            }
        }
    }
    public bool IsDeleteConfirmationOpen => PendingDelete is not null;
    public string DeleteConfirmationText =>
        PendingDelete?.Page is { } page ? $"Delete “{page.Title}” from OneNote?" : "";

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        AccountRoots.Clear();
        foreach (var account in _accounts)
        {
            AccountRoots.Add(NoteTreeNode.ForAccount(account, LoadChildrenAsync));
        }
        ApplyFilter("");
        return Task.CompletedTask;
    }

    public Task UpdateAccountsAsync(
        IReadOnlyList<MailAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        _accounts = accounts;
        return InitializeAsync(cancellationToken);
    }

    public async Task LoadChildrenAsync(
        NoteTreeNode node,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (node.IsLoading || (!force && node.IsLoaded) || node.Kind is NoteNodeKind.Page or NoteNodeKind.Placeholder)
        {
            return;
        }

        node.IsLoading = true;
        node.Error = null;
        try
        {
            var children = node.Kind switch
            {
                NoteNodeKind.Account => (await _provider.GetNotebooksAsync(node.Account, cancellationToken))
                    .Select(item => NoteTreeNode.ForNotebook(node, item, LoadChildrenAsync)),
                NoteNodeKind.Notebook => (await _provider.GetSectionsAsync(
                        node.Account, node.Notebook!, cancellationToken))
                    .Select(item => NoteTreeNode.ForSection(node, item, LoadChildrenAsync)),
                NoteNodeKind.Section => (await _provider.GetPagesAsync(
                        node.Account, node.Section!, cancellationToken))
                    .OrderBy(static page => page.Order)
                    .Select(item => NoteTreeNode.ForPage(node, item, LoadChildrenAsync)),
                _ => []
            };
            node.ReplaceChildren(children);
            node.IsLoaded = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            node.ReplaceChildren([]);
            node.Error = $"{node.Account.EmailAddress}: {ex.Message}";
        }
        finally
        {
            node.IsLoading = false;
            ApplyFilter(SearchText.Trim());
            RaisePartialErrors();
        }
    }

    internal async Task SearchAsync()
    {
        OperationError = null;
        var query = SearchText.Trim();
        if (query.Length > 0)
        {
            foreach (var root in AccountRoots)
            {
                await LoadDescendantsAsync(root);
            }
        }
        ApplyFilter(query);
    }

    internal async Task RefreshAsync()
    {
        OperationError = null;
        var node = SelectedNode;
        if (node?.Kind == NoteNodeKind.Page)
        {
            var pageId = node.Page!.ProviderId;
            await RefreshNodeAsync(node.Parent!);
            SelectedNode = node.Parent!.AllChildren.FirstOrDefault(child => child.Page?.ProviderId == pageId);
            return;
        }
        if (node is not null)
        {
            await RefreshNodeAsync(node);
            return;
        }
        foreach (var root in AccountRoots)
        {
            await RefreshNodeAsync(root);
        }
    }

    private async Task RefreshNodeAsync(NoteTreeNode node)
    {
        await LoadChildrenAsync(node, force: true);
        node.IsExpanded = true;
    }

    private async Task LoadDescendantsAsync(NoteTreeNode node)
    {
        await LoadChildrenAsync(node);
        foreach (var child in node.AllChildren.Where(
                     static child => child.Kind is NoteNodeKind.Account or NoteNodeKind.Notebook or NoteNodeKind.Section))
        {
            await LoadDescendantsAsync(child);
        }
    }

    private async Task LoadSelectedPageAsync(NoteTreeNode? node)
    {
        var version = ++_selectionVersion;
        _rawPageHtml = null;
        _allowRemoteContent = false;
        PageBodyUri = null;
        RaisePageState();
        if (node?.Page is not { } page)
        {
            return;
        }

        IsLoadingPage = true;
        OperationError = null;
        try
        {
            var content = await _provider.GetPageContentAsync(node.Account, page);
            if (version != _selectionVersion)
            {
                return;
            }
            if (content.AccountId != page.AccountId ||
                content.AccountProviderId != page.AccountProviderId ||
                content.PageProviderId != page.ProviderId)
            {
                throw new InvalidOperationException("OneNote returned content for a different account or page.");
            }
            _rawPageHtml = content.UntrustedHtml;
            RenderPage();
        }
        catch (Exception ex)
        {
            if (version == _selectionVersion)
            {
                OperationError = $"{node.Account.EmailAddress}: {ex.Message}";
            }
        }
        finally
        {
            if (version == _selectionVersion)
            {
                IsLoadingPage = false;
            }
        }
    }

    internal Task OpenNewPageAsync()
    {
        var sectionNode = SelectedNode?.Section is not null ? SelectedNode : SelectedNode?.Parent;
        if (sectionNode?.Section is null)
        {
            return Task.CompletedTask;
        }
        _editorSection = sectionNode;
        _editingPage = null;
        EditorTitle = "";
        EditorBody = "";
        EditorError = null;
        OpenEditor();
        return Task.CompletedTask;
    }

    internal Task OpenEditPageAsync()
    {
        if (SelectedNode?.Page is null)
        {
            return Task.CompletedTask;
        }
        _editingPage = SelectedNode;
        _editorSection = SelectedNode.Parent;
        EditorTitle = SelectedNode.Page.Title;
        EditorBody = "";
        EditorError = null;
        OpenEditor();
        return Task.CompletedTask;
    }

    private void OpenEditor()
    {
        IsEditorOpen = true;
        RaisePropertyChanged(nameof(IsEditing));
        RaisePropertyChanged(nameof(EditorHeading));
        RaisePropertyChanged(nameof(EditorBodyLabel));
        RaisePropertyChanged(nameof(EditorHelpText));
    }

    internal async Task SaveEditorAsync()
    {
        if (_editorSection?.Section is not { } section)
        {
            EditorError = "Select a OneNote section first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(EditorTitle))
        {
            EditorError = "Enter a page title.";
            return;
        }

        EditorError = null;
        try
        {
            string pageId;
            if (_editingPage?.Page is { } page)
            {
                var changes = new List<NotePagePatch>();
                if (!string.Equals(EditorTitle.Trim(), page.Title, StringComparison.Ordinal))
                {
                    changes.Add(new("title", NotePatchAction.Replace, WebUtility.HtmlEncode(EditorTitle.Trim())));
                }
                if (!string.IsNullOrWhiteSpace(EditorBody))
                {
                    changes.Add(new("body", NotePatchAction.Append, PlainTextToNoteHtml(EditorBody)));
                }
                if (changes.Count > 0)
                {
                    await _provider.UpdatePageAsync(_editingPage.Account, page, changes);
                }
                pageId = page.ProviderId;
            }
            else
            {
                var created = await _provider.CreatePageAsync(
                    _editorSection.Account,
                    new(
                        section.AccountId,
                        section.AccountProviderId,
                        section.ProviderId,
                        EditorTitle.Trim(),
                        PlainTextToNoteHtml(EditorBody)));
                pageId = created.ProviderId;
            }

            IsEditorOpen = false;
            await RefreshNodeAsync(_editorSection);
            SelectedNode = _editorSection.AllChildren.FirstOrDefault(
                child => child.Page?.ProviderId == pageId);
        }
        catch (Exception ex)
        {
            EditorError = ex.Message;
        }
    }

    private Task CloseEditorAsync()
    {
        IsEditorOpen = false;
        EditorError = null;
        return Task.CompletedTask;
    }

    internal Task RequestDeleteAsync()
    {
        PendingDelete = SelectedNode?.Page is null ? null : SelectedNode;
        return Task.CompletedTask;
    }

    internal async Task ConfirmDeleteAsync()
    {
        if (PendingDelete?.Page is not { } page || PendingDelete.Parent is not { } sectionNode)
        {
            return;
        }
        OperationError = null;
        try
        {
            await _provider.DeletePageAsync(PendingDelete.Account, page);
            PendingDelete = null;
            SelectedNode = null;
            await RefreshNodeAsync(sectionNode);
        }
        catch (Exception ex)
        {
            OperationError = ex.Message;
        }
    }

    private Task CancelDeleteAsync()
    {
        PendingDelete = null;
        return Task.CompletedTask;
    }

    internal Task AllowRemoteContentAsync()
    {
        _allowRemoteContent = true;
        RenderPage();
        return Task.CompletedTask;
    }

    private void RenderPage()
    {
        PageBodyUri = _rawPageHtml is null
            ? null
            : _renderer.Render(_rawPageHtml, isHtml: true, allowRemoteContent: _allowRemoteContent);
        RaisePageState();
    }

    private void RaisePageState()
    {
        RaisePropertyChanged(nameof(HasSelectedPage));
        RaisePropertyChanged(nameof(HasNoSelectedPage));
        RaisePropertyChanged(nameof(HasBlockedRemoteContent));
        ((AsyncCommand)AllowRemoteContentCommand).Refresh();
    }

    private void RefreshCommands()
    {
        RaisePageState();
        RaisePropertyChanged(nameof(SelectedSection));
        RaisePropertyChanged(nameof(SelectedPage));
        ((AsyncCommand)NewPageCommand).Refresh();
        ((AsyncCommand)EditPageCommand).Refresh();
        ((AsyncCommand)RequestDeleteCommand).Refresh();
    }

    private void RaisePartialErrors()
    {
        RaisePropertyChanged(nameof(HasPartialErrors));
        RaisePropertyChanged(nameof(PartialErrorText));
    }

    private void ApplyFilter(string query)
    {
        VisibleRoots.Clear();
        foreach (var root in AccountRoots)
        {
            if (root.ApplyFilter(query))
            {
                VisibleRoots.Add(root);
            }
        }
    }

    internal static string PlainTextToNoteHtml(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return string.Concat(lines.Select(line =>
            line.Length == 0 ? "<p><br></p>" : $"<p>{WebUtility.HtmlEncode(line)}</p>"));
    }
}

public enum NoteNodeKind
{
    Account,
    Notebook,
    Section,
    Page,
    Placeholder
}

public sealed class NoteTreeNode : ViewModelBase
{
    private readonly Func<NoteTreeNode, bool, CancellationToken, Task>? _load;
    private bool _isExpanded;
    private bool _isLoading;
    private bool _isLoaded;
    private string? _error;

    private NoteTreeNode(
        NoteNodeKind kind,
        MailAccount account,
        string displayName,
        string secondaryText,
        NoteTreeNode? parent,
        NoteNotebook? notebook,
        NoteSection? section,
        NotePage? page,
        Func<NoteTreeNode, bool, CancellationToken, Task>? load)
    {
        Kind = kind;
        Account = account;
        DisplayName = displayName;
        SecondaryText = secondaryText;
        Parent = parent;
        Notebook = notebook;
        Section = section;
        Page = page;
        _load = load;
        if (kind is NoteNodeKind.Account or NoteNodeKind.Notebook or NoteNodeKind.Section)
        {
            AllChildren.Add(Placeholder(account, this));
            Children.Add(AllChildren[0]);
        }
    }

    public NoteNodeKind Kind { get; }
    public MailAccount Account { get; }
    public string DisplayName { get; }
    public string SecondaryText { get; }
    public NoteTreeNode? Parent { get; }
    public NoteNotebook? Notebook { get; }
    public NoteSection? Section { get; }
    public NotePage? Page { get; }
    public List<NoteTreeNode> AllChildren { get; } = [];
    public ObservableCollection<NoteTreeNode> Children { get; } = [];
    public string Glyph => Kind switch
    {
        NoteNodeKind.Account => "●",
        NoteNodeKind.Notebook => "▣",
        NoteNodeKind.Section => "▤",
        NoteNodeKind.Page => "□",
        _ => "…"
    };
    public string AutomationName => $"{Kind}: {DisplayName}. {SecondaryText}";
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && _load is not null)
            {
                _ = _load(this, false, CancellationToken.None);
            }
        }
    }
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
    public bool IsLoaded { get => _isLoaded; set => SetProperty(ref _isLoaded, value); }
    public string? Error
    {
        get => _error;
        set
        {
            if (SetProperty(ref _error, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(Error) ||
                            AllChildren.Any(static child => child.HasError);

    public IEnumerable<string> Errors()
    {
        if (!string.IsNullOrWhiteSpace(Error))
        {
            yield return Error;
        }
        foreach (var error in AllChildren.SelectMany(static child => child.Errors()))
        {
            yield return error;
        }
    }

    public void ReplaceChildren(IEnumerable<NoteTreeNode> children)
    {
        AllChildren.Clear();
        AllChildren.AddRange(children);
        Children.Clear();
        foreach (var child in AllChildren)
        {
            Children.Add(child);
        }
    }

    public bool ApplyFilter(string query)
    {
        Children.Clear();
        var selfMatches = query.Length == 0 ||
                          DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          SecondaryText.Contains(query, StringComparison.OrdinalIgnoreCase);
        foreach (var child in AllChildren)
        {
            if (child.Kind != NoteNodeKind.Placeholder && child.ApplyFilter(query))
            {
                Children.Add(child);
            }
            else if (query.Length == 0 && child.Kind == NoteNodeKind.Placeholder)
            {
                Children.Add(child);
            }
        }
        if (query.Length > 0 && Children.Count > 0)
        {
            IsExpanded = true;
        }
        return selfMatches || Children.Count > 0;
    }

    public static NoteTreeNode ForAccount(
        MailAccount account,
        Func<NoteTreeNode, bool, CancellationToken, Task> load) =>
        new(
            NoteNodeKind.Account,
            account,
            string.IsNullOrWhiteSpace(account.DisplayName) ? account.EmailAddress : account.DisplayName,
            $"{account.EmailAddress} · {account.ProviderId}",
            null,
            null,
            null,
            null,
            load);

    public static NoteTreeNode ForNotebook(
        NoteTreeNode parent,
        NoteNotebook notebook,
        Func<NoteTreeNode, bool, CancellationToken, Task> load) =>
        new(
            NoteNodeKind.Notebook,
            parent.Account,
            notebook.Name,
            "Notebook",
            parent,
            notebook,
            null,
            null,
            load);

    public static NoteTreeNode ForSection(
        NoteTreeNode parent,
        NoteSection section,
        Func<NoteTreeNode, bool, CancellationToken, Task> load) =>
        new(
            NoteNodeKind.Section,
            parent.Account,
            section.Name,
            "Section",
            parent,
            null,
            section,
            null,
            load);

    public static NoteTreeNode ForPage(
        NoteTreeNode parent,
        NotePage page,
        Func<NoteTreeNode, bool, CancellationToken, Task> load) =>
        new(
            NoteNodeKind.Page,
            parent.Account,
            page.Title,
            page.ModifiedAt == DateTimeOffset.MinValue
                ? "Page"
                : page.ModifiedAt.ToLocalTime().ToString("g"),
            parent,
            null,
            null,
            page,
            load);

    private static NoteTreeNode Placeholder(MailAccount account, NoteTreeNode parent) =>
        new(
            NoteNodeKind.Placeholder,
            account,
            "Expand to load",
            "",
            parent,
            null,
            null,
            null,
            null);
}
