using System.Collections.ObjectModel;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class DriveWorkspaceViewModel : ViewModelBase
{
    private readonly IFilesProvider _provider;
    private readonly EncryptedMailStore? _store;
    private IReadOnlyList<MailAccount> _accounts;
    private Func<CancellationToken, Task<DriveUploadSource?>>? _pickUpload;
    private Func<CloudDriveItem, CancellationToken, Task<Stream?>>? _pickDownload;
    private DriveTreeNode? _selectedDirectory;
    private DriveItemEntry? _selectedItem;
    private DriveSearchResult? _selectedSearchResult;
    private DriveAccountFilter? _selectedAccountFilter;
    private DriveItemEntry? _pendingDelete;
    private string _searchQuery = "";
    private string _newFolderName = "";
    private string _renameName = "";
    private string? _operationError;
    private bool _isBusy;
    private bool _isSearchMode;
    private bool _initialized;

    public DriveWorkspaceViewModel(
        IFilesProvider provider,
        IReadOnlyList<MailAccount> accounts,
        EncryptedMailStore? store = null)
    {
        _provider = provider;
        _store = store;
        _accounts = accounts.ToArray();
        RebuildAccountFilters();

        ToggleNodeCommand = new AsyncCommand<DriveTreeNode>(ToggleNodeAsync);
        OpenItemCommand = new AsyncCommand<DriveItemEntry>(OpenItemAsync);
        RefreshCommand = new AsyncCommand(RefreshSelectedAsync, () => SelectedDirectory is not null && !IsBusy);
        SearchCommand = new AsyncCommand(SearchAsync, () => !IsBusy);
        ClearSearchCommand = new AsyncCommand(ClearSearchAsync);
        CreateFolderCommand = new AsyncCommand(CreateFolderAsync, () =>
            SelectedDirectory is not null && !string.IsNullOrWhiteSpace(NewFolderName) && !IsBusy);
        UploadCommand = new AsyncCommand(UploadFromPickerAsync, () =>
            SelectedDirectory is not null && _pickUpload is not null && !IsBusy);
        DownloadCommand = new AsyncCommand(DownloadToPickerAsync, () =>
            SelectedItem?.Item.IsFolder == false && _pickDownload is not null && !IsBusy);
        RenameCommand = new AsyncCommand(RenameSelectedAsync, () =>
            SelectedItem is not null && !string.IsNullOrWhiteSpace(RenameName) && !IsBusy);
        RequestDeleteCommand = new AsyncCommand(RequestDeleteAsync, () => SelectedItem is not null && !IsBusy);
        ConfirmDeleteCommand = new AsyncCommand(ConfirmDeleteAsync, () => _pendingDelete is not null && !IsBusy);
        CancelDeleteCommand = new AsyncCommand(CancelDeleteAsync);
        ChooseItemCommand = new AsyncCommand(ChooseItemAsync, () => SelectedProviderItem?.Item.IsFolder == false);
        ShareLinkCommand = new AsyncCommand(ShareLinkAsync, () => SelectedProviderItem?.Item.WebUrl is not null);
    }

    public ObservableCollection<DriveTreeNode> Roots { get; } = [];
    public ObservableCollection<DriveItemEntry> CurrentItems { get; } = [];
    public ObservableCollection<DriveSearchResult> SearchResults { get; } = [];
    public ObservableCollection<string> LoadIssues { get; } = [];
    public ObservableCollection<DriveAccountFilter> AccountFilters { get; } = [];

    public ICommand ToggleNodeCommand { get; }
    public ICommand OpenItemCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand CreateFolderCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand RequestDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand ChooseItemCommand { get; }
    public ICommand ShareLinkCommand { get; }

    public event Action<DriveProviderSelection>? ItemChosen;
    public event Action<DriveProviderSelection>? LinkChosen;

    public DriveTreeNode? SelectedDirectory
    {
        get => _selectedDirectory;
        private set
        {
            if (SetProperty(ref _selectedDirectory, value))
            {
                RaisePropertyChanged(nameof(DirectoryTitle));
                RefreshCommands();
            }
        }
    }

    public DriveItemEntry? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                RenameName = value?.Item.Name ?? "";
                RefreshCommands();
            }
        }
    }

    public DriveSearchResult? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (SetProperty(ref _selectedSearchResult, value))
            {
                RefreshCommands();
            }
        }
    }

    public DriveAccountFilter? SelectedAccountFilter
    {
        get => _selectedAccountFilter;
        set
        {
            if (SetProperty(ref _selectedAccountFilter, value) && IsSearchMode)
            {
                _ = SearchAsync();
            }
        }
    }

    public string SearchQuery { get => _searchQuery; set => SetProperty(ref _searchQuery, value); }
    public string NewFolderName
    {
        get => _newFolderName;
        set
        {
            if (SetProperty(ref _newFolderName, value))
            {
                ((AsyncCommand)CreateFolderCommand).Refresh();
            }
        }
    }
    public string RenameName
    {
        get => _renameName;
        set
        {
            if (SetProperty(ref _renameName, value))
            {
                ((AsyncCommand)RenameCommand).Refresh();
            }
        }
    }
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
    public bool HasLoadIssues => LoadIssues.Count > 0;
    public bool IsSearchMode { get => _isSearchMode; private set => SetProperty(ref _isSearchMode, value); }
    public bool IsDeletePending => _pendingDelete is not null;
    public string DeletePrompt => _pendingDelete is null ? "" : $"Delete “{_pendingDelete.Item.Name}”?";
    public string DirectoryTitle => SelectedDirectory?.PathLabel ?? "Drive";
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommands();
            }
        }
    }

    public DriveProviderSelection? SelectedProviderItem => IsSearchMode
        ? SelectedSearchResult?.Selection
        : SelectedItem is null
            ? null
            : new DriveProviderSelection(SelectedItem.Account, SelectedItem.Item);

    public void ConfigureFileTransfers(
        Func<CancellationToken, Task<DriveUploadSource?>> pickUpload,
        Func<CloudDriveItem, CancellationToken, Task<Stream?>> pickDownload)
    {
        _pickUpload = pickUpload;
        _pickDownload = pickDownload;
        RefreshCommands();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        foreach (var account in _accounts)
        {
            Roots.Add(DriveTreeNode.Root(account, LoadNodeFromExpansionAsync));
        }
        if (Roots.Count > 0)
        {
            await SelectDirectoryAsync(Roots[0], cancellationToken);
        }
    }

    public async Task UpdateAccountsAsync(
        IReadOnlyList<MailAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        _accounts = accounts.ToArray();
        RebuildAccountFilters();
        if (!_initialized)
        {
            return;
        }

        var accountKeys = _accounts.Select(AccountKey).ToHashSet(StringComparer.Ordinal);
        foreach (var root in Roots.Where(root => !accountKeys.Contains(AccountKey(root.Account))).ToArray())
        {
            Roots.Remove(root);
        }
        foreach (var account in _accounts)
        {
            var root = Roots.FirstOrDefault(candidate =>
                AccountKey(candidate.Account) == AccountKey(account));
            if (root is null)
            {
                Roots.Add(DriveTreeNode.Root(account, LoadNodeFromExpansionAsync));
            }
            else
            {
                root.UpdateAccount(account);
            }
        }

        if (SelectedDirectory is not null &&
            !accountKeys.Contains(AccountKey(SelectedDirectory.Account)))
        {
            SelectedDirectory = null;
            SelectedItem = null;
            CurrentItems.Clear();
        }
        if (SelectedDirectory is null && Roots.Count > 0)
        {
            await SelectDirectoryAsync(Roots[0], cancellationToken);
        }
    }

    public async Task SelectDirectoryAsync(DriveTreeNode node, CancellationToken cancellationToken = default)
    {
        if (node.IsPlaceholder || node.Item?.IsFolder == false)
        {
            return;
        }
        await LoadNodeAsync(node, force: false, cancellationToken);
        SelectedDirectory = node;
        Replace(CurrentItems, node.Items.Select(item => new DriveItemEntry(node.Account, item)));
        SelectedItem = null;
        SelectedSearchResult = null;
        IsSearchMode = false;
    }

    public async Task UploadAsync(DriveUploadSource source, CancellationToken cancellationToken = default)
    {
        if (SelectedDirectory is null)
        {
            throw new InvalidOperationException("Select a drive directory before uploading.");
        }
        await RunOperationAsync(async () =>
        {
            await _provider.UploadFileAsync(
                SelectedDirectory.Account,
                SelectedDirectory.Item,
                source.Name,
                source.Content,
                source.Length,
                source.ContentType,
                cancellationToken);
            await RefreshNodeAsync(SelectedDirectory, cancellationToken);
        });
    }

    public async Task DownloadAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var selected = SelectedItem;
        if (selected is null || selected.Item.IsFolder)
        {
            throw new InvalidOperationException("Select a file before downloading.");
        }
        await RunOperationAsync(() =>
            _provider.DownloadFileAsync(selected.Account, selected.Item, destination, cancellationToken));
    }

    private Task ToggleNodeAsync(DriveTreeNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        return Task.CompletedTask;
    }

    private async Task LoadNodeFromExpansionAsync(DriveTreeNode node)
    {
        if (node.IsExpanded)
        {
            await SelectDirectoryAsync(node);
        }
    }

    private async Task OpenItemAsync(DriveItemEntry entry)
    {
        SelectedItem = entry;
        if (!entry.Item.IsFolder || SelectedDirectory is null)
        {
            return;
        }
        var child = SelectedDirectory.Children.FirstOrDefault(node =>
            node.Item?.ProviderId == entry.Item.ProviderId);
        if (child is not null)
        {
            await SelectDirectoryAsync(child);
            child.IsExpanded = true;
        }
    }

    private async Task LoadNodeAsync(DriveTreeNode node, bool force, CancellationToken cancellationToken)
    {
        if (node.IsLoaded && !force)
        {
            return;
        }
        node.IsLoading = true;
        node.Error = null;
        try
        {
            var items = await _provider.GetDriveItemsAsync(node.Account, node.Item, cancellationToken);
            if (_store is not null)
            {
                var scope = node.Item?.ProviderId ?? "root";
                await _store.ReplaceWorkspaceItemsAsync(
                    "drive-directory", node.Account.AccountId, scope, items,
                    static item => item.ProviderId,
                    static item => $"{item.Name} {item.Path} {item.ContentType}", cancellationToken);
                var files = items.Where(static item => !item.IsFolder).Select(ToCloudFile).ToArray();
                await _store.ReplaceDriveDirectoryFilesAsync(
                    node.Account.AccountId,
                    node.Item?.Path ?? "/drive/root:",
                    files,
                    cancellationToken);
                await _store.GarbageCollectWorkspaceAsync(node.Account.AccountId, cancellationToken);
            }
            node.SetItems(items, LoadNodeFromExpansionAsync);
            RemoveIssues(node.Account);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var cached = _store is null
                ? []
                : await _store.GetWorkspaceItemsAsync<CloudDriveItem>(
                    "drive-directory", node.Account.AccountId, node.Item?.ProviderId ?? "root", cancellationToken);
            if (cached.Count > 0)
            {
                node.SetItems(cached, LoadNodeFromExpansionAsync);
            }
            else
            {
                node.Error = exception.Message;
                RecordIssue(node.Account, exception.Message);
            }
        }
        finally
        {
            node.IsLoading = false;
        }
    }

    private async Task RefreshNodeAsync(DriveTreeNode node, CancellationToken cancellationToken = default)
    {
        await LoadNodeAsync(node, force: true, cancellationToken);
        if (ReferenceEquals(node, SelectedDirectory))
        {
            Replace(CurrentItems, node.Items.Select(item => new DriveItemEntry(node.Account, item)));
            SelectedItem = null;
        }
    }

    private Task RefreshSelectedAsync() =>
        SelectedDirectory is null ? Task.CompletedTask : RefreshNodeAsync(SelectedDirectory);

    private async Task SearchAsync()
    {
        OperationError = null;
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            await ClearSearchAsync();
            return;
        }
        IsBusy = true;
        try
        {
            var accounts = SelectedAccountFilter?.Account is { } account ? [account] : _accounts;
            if (_store is not null)
            {
                var cached = await _store.SearchWorkspaceItemsAsync<CloudFile>(
                    "drive-file", SearchQuery.Trim(), 200,
                    SelectedAccountFilter?.Account?.AccountId);
                Replace(SearchResults, cached
                    .Select(file => (File: file, Account: accounts.FirstOrDefault(
                        candidate => candidate.AccountId == file.AccountId)))
                    .Where(static item => item.Account is not null)
                    .Select(static item => new DriveSearchResult(item.Account!, item.File))
                    .OrderBy(static result => result.File.Name, StringComparer.OrdinalIgnoreCase));
                IsSearchMode = true;
            }
            var batches = await Task.WhenAll(accounts.Select(async candidate =>
            {
                try
                {
                    var files = await _provider.SearchFilesAsync(candidate, SearchQuery.Trim());
                    if (_store is not null)
                    {
                        await _store.UpsertWorkspaceItemsAsync(
                            "drive-file", candidate.AccountId, "index", files,
                            static item => item.ProviderId,
                            static item => $"{item.Name} {item.Path}");
                    }
                    RemoveIssues(candidate);
                    return new DriveSearchBatch(candidate, files, null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return new DriveSearchBatch(candidate, [], exception.Message);
                }
            }));
            foreach (var failed in batches.Where(static batch => batch.Error is not null))
            {
                RecordIssue(failed.Account, failed.Error!);
            }
            var online = batches
                .SelectMany(batch => batch.Files.Select(file => new DriveSearchResult(batch.Account, file)))
                .ToArray();
            if (online.Length > 0 || SearchResults.Count == 0)
            {
                Replace(SearchResults, online.OrderBy(
                    static result => result.File.Name, StringComparer.OrdinalIgnoreCase));
            }
            SelectedSearchResult = null;
            IsSearchMode = true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static CloudFile ToCloudFile(CloudDriveItem item) => new(
        item.ProviderId,
        item.Name,
        item.Size,
        item.WebUrl,
        item.AccountId,
        item.AccountProviderId,
        item.ParentPath);

    private Task ClearSearchAsync()
    {
        SearchQuery = "";
        SearchResults.Clear();
        SelectedSearchResult = null;
        IsSearchMode = false;
        return Task.CompletedTask;
    }

    private async Task CreateFolderAsync()
    {
        var directory = SelectedDirectory;
        if (directory is null)
        {
            return;
        }
        await RunOperationAsync(async () =>
        {
            await _provider.CreateFolderAsync(directory.Account, directory.Item, NewFolderName.Trim());
            NewFolderName = "";
            await RefreshNodeAsync(directory);
        });
    }

    private async Task UploadFromPickerAsync()
    {
        if (_pickUpload is null)
        {
            return;
        }
        DriveUploadSource? source;
        try
        {
            source = await _pickUpload(CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationError = exception.Message;
            return;
        }
        if (source is null)
        {
            return;
        }
        await using (source.Content)
        {
            await UploadAsync(source);
        }
    }

    private async Task DownloadToPickerAsync()
    {
        var selected = SelectedItem;
        if (selected is null || _pickDownload is null)
        {
            return;
        }
        Stream? destination;
        try
        {
            destination = await _pickDownload(selected.Item, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationError = exception.Message;
            return;
        }
        if (destination is null)
        {
            return;
        }
        await using (destination)
        {
            await DownloadAsync(destination);
        }
    }

    private async Task RenameSelectedAsync()
    {
        var selected = SelectedItem;
        var directory = SelectedDirectory;
        if (selected is null || directory is null)
        {
            return;
        }
        await RunOperationAsync(async () =>
        {
            await _provider.RenameDriveItemAsync(selected.Account, selected.Item, RenameName.Trim());
            await RefreshNodeAsync(directory);
        });
    }

    private Task RequestDeleteAsync()
    {
        _pendingDelete = SelectedItem;
        RaisePropertyChanged(nameof(IsDeletePending));
        RaisePropertyChanged(nameof(DeletePrompt));
        ((AsyncCommand)ConfirmDeleteCommand).Refresh();
        return Task.CompletedTask;
    }

    private Task CancelDeleteAsync()
    {
        _pendingDelete = null;
        RaisePropertyChanged(nameof(IsDeletePending));
        RaisePropertyChanged(nameof(DeletePrompt));
        ((AsyncCommand)ConfirmDeleteCommand).Refresh();
        return Task.CompletedTask;
    }

    private async Task ConfirmDeleteAsync()
    {
        var pending = _pendingDelete;
        var directory = SelectedDirectory;
        if (pending is null || directory is null)
        {
            return;
        }
        await RunOperationAsync(async () =>
        {
            await _provider.DeleteDriveItemAsync(pending.Account, pending.Item);
            await CancelDeleteAsync();
            await RefreshNodeAsync(directory);
        });
    }

    private Task ChooseItemAsync()
    {
        if (SelectedProviderItem is { } selection)
        {
            ItemChosen?.Invoke(selection);
        }
        return Task.CompletedTask;
    }

    private Task ShareLinkAsync()
    {
        if (SelectedProviderItem is { Item.WebUrl: not null } selection)
        {
            LinkChosen?.Invoke(selection);
        }
        return Task.CompletedTask;
    }

    private async Task RunOperationAsync(Func<Task> operation)
    {
        IsBusy = true;
        OperationError = null;
        try
        {
            await operation();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            OperationError = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RecordIssue(MailAccount account, string error)
    {
        RemoveIssues(account);
        LoadIssues.Add($"{account.EmailAddress}: {error}");
        RaisePropertyChanged(nameof(HasLoadIssues));
    }

    private void RemoveIssues(MailAccount account)
    {
        var prefix = $"{account.EmailAddress}:";
        foreach (var issue in LoadIssues.Where(issue =>
            issue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            LoadIssues.Remove(issue);
        }
        RaisePropertyChanged(nameof(HasLoadIssues));
    }

    private void RefreshCommands()
    {
        ((AsyncCommand)RefreshCommand).Refresh();
        ((AsyncCommand)SearchCommand).Refresh();
        ((AsyncCommand)CreateFolderCommand).Refresh();
        ((AsyncCommand)UploadCommand).Refresh();
        ((AsyncCommand)DownloadCommand).Refresh();
        ((AsyncCommand)RenameCommand).Refresh();
        ((AsyncCommand)RequestDeleteCommand).Refresh();
        ((AsyncCommand)ConfirmDeleteCommand).Refresh();
        ((AsyncCommand)ChooseItemCommand).Refresh();
        ((AsyncCommand)ShareLinkCommand).Refresh();
    }

    private void RebuildAccountFilters()
    {
        var selectedAccountId = SelectedAccountFilter?.Account?.AccountId;
        AccountFilters.Clear();
        AccountFilters.Add(new DriveAccountFilter("All accounts", null));
        foreach (var account in _accounts)
        {
            AccountFilters.Add(new DriveAccountFilter(
                $"{ProviderName(account.ProviderId)} · {account.EmailAddress}",
                account));
        }
        SelectedAccountFilter = AccountFilters.FirstOrDefault(filter =>
            filter.Account?.AccountId == selectedAccountId) ?? AccountFilters[0];
    }

    private static string AccountKey(MailAccount account) =>
        $"{account.ProviderId}\n{account.AccountId}";

    private static string ProviderName(string providerId) =>
        providerId.Equals("microsoft365", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : providerId;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}

public sealed class DriveTreeNode : ViewModelBase
{
    private readonly Func<DriveTreeNode, Task>? _expanded;
    private bool _isExpanded;
    private bool _isLoading;
    private bool _isLoaded;
    private string? _error;

    private DriveTreeNode(
        MailAccount account,
        CloudDriveItem? item,
        DriveTreeNode? parent,
        bool isPlaceholder,
        Func<DriveTreeNode, Task>? expanded)
    {
        Account = account;
        Item = item;
        Parent = parent;
        IsPlaceholder = isPlaceholder;
        _expanded = expanded;
    }

    public MailAccount Account { get; private set; }
    public CloudDriveItem? Item { get; }
    public DriveTreeNode? Parent { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<DriveTreeNode> Children { get; } = [];
    public IReadOnlyList<CloudDriveItem> Items { get; private set; } = [];
    public string DisplayName => IsPlaceholder ? "Expand to load…" :
        Item?.Name ?? $"{ProviderName(Account.ProviderId)} · {Account.EmailAddress}";
    public string PathLabel => Item?.Path ?? $"{ProviderName(Account.ProviderId)} · {Account.EmailAddress}";
    public bool IsFolder => Item is null || Item.IsFolder;
    public bool IsLoaded { get => _isLoaded; private set => SetProperty(ref _isLoaded, value); }
    public bool IsLoading { get => _isLoading; set => SetProperty(ref _isLoading, value); }
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
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !IsLoaded)
            {
                _ = _expanded?.Invoke(this);
            }
        }
    }

    public static DriveTreeNode Root(MailAccount account, Func<DriveTreeNode, Task> expanded)
    {
        var root = new DriveTreeNode(account, null, null, false, expanded);
        root.Children.Add(new DriveTreeNode(account, null, root, true, null));
        return root;
    }

    public void SetItems(IReadOnlyList<CloudDriveItem> items, Func<DriveTreeNode, Task> expanded)
    {
        Items = items;
        Children.Clear();
        foreach (var folder in items.Where(static item => item.IsFolder)
                     .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var child = new DriveTreeNode(Account, folder, this, false, expanded);
            child.Children.Add(new DriveTreeNode(Account, null, child, true, null));
            Children.Add(child);
        }
        IsLoaded = true;
    }

    public void UpdateAccount(MailAccount account)
    {
        Account = account;
        RaisePropertyChanged(nameof(Account));
        RaisePropertyChanged(nameof(DisplayName));
        RaisePropertyChanged(nameof(PathLabel));
        foreach (var child in Children)
        {
            child.UpdateAccount(account);
        }
    }

    private static string ProviderName(string providerId) =>
        providerId.Equals("microsoft365", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : providerId;
}

public sealed record DriveItemEntry(MailAccount Account, CloudDriveItem Item)
{
    public string TypeText => Item.IsFolder ? "Folder" : Item.ContentType ?? "File";
}

public sealed record DriveSearchResult(MailAccount Account, CloudFile File)
{
    public string SourceText => $"{ProviderName(Account.ProviderId)} · {Account.EmailAddress} · {File.Path}";
    private static string ProviderName(string providerId) =>
        providerId.Equals("microsoft365", StringComparison.OrdinalIgnoreCase) ? "OneDrive" : providerId;

    public DriveProviderSelection Selection => new(Account, new CloudDriveItem(
        File.ProviderId,
        File.Name,
        File.Size,
        false,
        null,
        File.WebUrl,
        Account.AccountId,
        Account.ProviderId,
        ParentPath: File.ParentPath));
}

public sealed record DriveAccountFilter(string DisplayName, MailAccount? Account);
public sealed record DriveProviderSelection(MailAccount Account, CloudDriveItem Item);
public sealed record DriveUploadSource(string Name, Stream Content, long Length, string? ContentType = null);
internal sealed record DriveSearchBatch(MailAccount Account, IReadOnlyList<CloudFile> Files, string? Error);
