using System.Collections.ObjectModel;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class TasksWorkspaceViewModel : ViewModelBase
{
    private readonly ITasksProvider _provider;
    private readonly EncryptedMailStore? _store;
    private IReadOnlyList<MailAccount> _accounts;
    private TaskListChoice? _selectedList;
    private TaskWorkspaceItem? _selectedTask;
    private TaskWorkspaceItem? _editingTask;
    private TaskWorkspaceItem? _pendingDelete;
    private string _searchText = "";
    private string _editorTitle = "";
    private DateTimeOffset? _editorDueDate;
    private TimeSpan? _editorDueTime;
    private string? _operationError;
    private string? _editorError;
    private bool _editorHasDueDate;
    private bool _isLoading;
    private bool _isEditorOpen;

    public TasksWorkspaceViewModel(
        ITasksProvider provider,
        IReadOnlyList<MailAccount> accounts,
        EncryptedMailStore? store = null)
    {
        _provider = provider;
        _store = store;
        _accounts = accounts;
        ShowAllTasksCommand = new AsyncCommand(ShowAllTasksAsync);
        SelectListCommand = new AsyncCommand<TaskListChoice>(SelectListAsync);
        SearchCommand = new AsyncCommand(SearchAsync);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsLoading);
        NewTaskCommand = new AsyncCommand(OpenNewTaskAsync, () => SelectedList is not null && !IsLoading);
        EditTaskCommand = new AsyncCommand<TaskWorkspaceItem>(OpenEditTaskAsync);
        ToggleCompleteCommand = new AsyncCommand<TaskWorkspaceItem>(ToggleCompleteAsync);
        RequestDeleteItemCommand = new AsyncCommand<TaskWorkspaceItem>(RequestDeleteItemAsync);
        RequestDeleteCommand = new AsyncCommand(RequestDeleteAsync, () => SelectedTask is not null);
        ConfirmDeleteCommand = new AsyncCommand(ConfirmDeleteAsync, () => PendingDelete is not null);
        CancelDeleteCommand = new AsyncCommand(CancelDeleteAsync);
        SaveEditorCommand = new AsyncCommand(SaveEditorAsync);
        CloseEditorCommand = new AsyncCommand(CloseEditorAsync);
    }

    public ObservableCollection<TaskAccountGroup> AccountGroups { get; } = [];
    public ObservableCollection<TaskWorkspaceItem> VisibleTasks { get; } = [];
    public ICommand ShowAllTasksCommand { get; }
    public ICommand SelectListCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand NewTaskCommand { get; }
    public ICommand EditTaskCommand { get; }
    public ICommand ToggleCompleteCommand { get; }
    public ICommand RequestDeleteItemCommand { get; }
    public ICommand RequestDeleteCommand { get; }
    public ICommand ConfirmDeleteCommand { get; }
    public ICommand CancelDeleteCommand { get; }
    public ICommand SaveEditorCommand { get; }
    public ICommand CloseEditorCommand { get; }

    public TaskListChoice? SelectedList
    {
        get => _selectedList;
        private set
        {
            var previous = _selectedList;
            if (SetProperty(ref _selectedList, value))
            {
                if (previous is not null)
                {
                    previous.IsSelected = false;
                }
                if (value is not null)
                {
                    value.IsSelected = true;
                }
                RaisePropertyChanged(nameof(IsAllTasksSelected));
                RaisePropertyChanged(nameof(Heading));
                ((AsyncCommand)NewTaskCommand).Refresh();
            }
        }
    }
    public TaskWorkspaceItem? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (SetProperty(ref _selectedTask, value))
            {
                ((AsyncCommand)RequestDeleteCommand).Refresh();
            }
        }
    }
    public bool IsAllTasksSelected => SelectedList is null;
    public string Heading => SelectedList?.DisplayName ?? "All tasks";
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                ((AsyncCommand)RefreshCommand).Refresh();
                ((AsyncCommand)NewTaskCommand).Refresh();
            }
        }
    }
    public bool HasTasks => VisibleTasks.Count > 0;
    public bool HasNoTasks => VisibleTasks.Count == 0 && !IsLoading;
    public int OpenCount => VisibleTasks.Count(static item => !item.Info.IsComplete);
    public int CompletedCount => VisibleTasks.Count(static item => item.Info.IsComplete);
    public bool HasPartialErrors => AccountGroups.Any(static group => group.HasError);
    public string PartialErrorText => string.Join(
        Environment.NewLine,
        AccountGroups.SelectMany(static group => group.Errors()));
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
    public bool IsEditorOpen { get => _isEditorOpen; private set => SetProperty(ref _isEditorOpen, value); }
    public bool IsEditing => _editingTask is not null;
    public string EditorHeading => IsEditing ? "Edit task" : "New task";
    public string EditorTitle { get => _editorTitle; set => SetProperty(ref _editorTitle, value); }
    public bool EditorHasDueDate { get => _editorHasDueDate; set => SetProperty(ref _editorHasDueDate, value); }
    public DateTimeOffset? EditorDueDate { get => _editorDueDate; set => SetProperty(ref _editorDueDate, value); }
    public TimeSpan? EditorDueTime { get => _editorDueTime; set => SetProperty(ref _editorDueTime, value); }
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
    public TaskWorkspaceItem? PendingDelete
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
        PendingDelete is null ? "" : $"Delete “{PendingDelete.Info.Title}” from {PendingDelete.List.DisplayName}?";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        OperationError = null;
        AccountGroups.Clear();
        try
        {
            var groups = await Task.WhenAll(_accounts.Select(
                account => LoadAccountAsync(account, cancellationToken)));
            foreach (var group in groups)
            {
                AccountGroups.Add(group);
            }
            await Task.WhenAll(AccountGroups.SelectMany(static group => group.Lists)
                .Select(list => LoadListAsync(list, cancellationToken)));
            SelectedList = null;
            RebuildVisibleTasks();
            RaisePartialErrors();
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task UpdateAccountsAsync(
        IReadOnlyList<MailAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        _accounts = accounts;
        return InitializeAsync(cancellationToken);
    }

    private async Task<TaskAccountGroup> LoadAccountAsync(
        MailAccount account,
        CancellationToken cancellationToken)
    {
        var group = new TaskAccountGroup(account);
        try
        {
            var lists = await _provider.GetTaskListsAsync(account, cancellationToken);
            if (_store is not null)
            {
                await _store.ReplaceWorkspaceItemsAsync(
                    "task-list", account.AccountId, "all", lists,
                    static item => item.ProviderId,
                    static item => item.DisplayName, cancellationToken);
                await _store.GarbageCollectWorkspaceAsync(account.AccountId, cancellationToken);
            }
            foreach (var list in lists)
            {
                if (list.AccountId != account.AccountId)
                {
                    throw new InvalidOperationException("The task list belongs to a different account.");
                }
                group.Lists.Add(new(group, list));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cached = _store is null
                ? []
                : await _store.GetWorkspaceItemsAsync<TaskListInfo>(
                    "task-list", account.AccountId, "all", cancellationToken);
            foreach (var list in cached)
            {
                group.Lists.Add(new(group, list));
            }
            if (cached.Count == 0)
            {
                group.Error = $"{account.EmailAddress}: task lists could not be loaded ({ex.Message})";
            }
        }
        return group;
    }

    private async Task LoadListAsync(
        TaskListChoice list,
        CancellationToken cancellationToken)
    {
        list.Error = null;
        try
        {
            var tasks = await _provider.GetTasksAsync(list.Group.Account, list.Info, cancellationToken);
            if (tasks.Any(task =>
                    task.AccountId != list.Group.Account.AccountId ||
                    task.ListId != list.Info.ProviderId))
            {
                throw new InvalidOperationException("The task provider returned data for a different account or list.");
            }
            if (_store is not null)
            {
                await _store.ReplaceWorkspaceItemsAsync(
                    "task", list.Group.Account.AccountId, list.Info.ProviderId, tasks,
                    static item => item.ProviderId,
                    static item => $"{item.Title} {item.Notes} {string.Join(' ', item.Categories ?? [])}", cancellationToken);
            }
            list.Replace(tasks.Select(task => new TaskWorkspaceItem(list, task)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cached = _store is null
                ? []
                : await _store.GetWorkspaceItemsAsync<TaskInfo>(
                    "task", list.Group.Account.AccountId, list.Info.ProviderId, cancellationToken);
            list.Replace(cached.Select(task => new TaskWorkspaceItem(list, task)));
            if (cached.Count == 0)
            {
                list.Error = $"{list.Group.Account.EmailAddress} / {list.DisplayName}: {ex.Message}";
            }
        }
    }

    internal Task ShowAllTasksAsync()
    {
        SelectedList = null;
        RebuildVisibleTasks();
        return Task.CompletedTask;
    }

    internal Task SelectListAsync(TaskListChoice list)
    {
        SelectedList = list;
        RebuildVisibleTasks();
        return Task.CompletedTask;
    }

    internal Task SearchAsync()
    {
        RebuildVisibleTasks();
        return Task.CompletedTask;
    }

    internal async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }
        IsLoading = true;
        OperationError = null;
        try
        {
            if (SelectedList is null)
            {
                await InitializeAsync();
                return;
            }
            var taskId = SelectedTask?.Info.ProviderId;
            await LoadListAsync(SelectedList, CancellationToken.None);
            RebuildVisibleTasks();
            SelectedTask = VisibleTasks.FirstOrDefault(task => task.Info.ProviderId == taskId);
            RaisePartialErrors();
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal Task OpenNewTaskAsync()
    {
        if (SelectedList is null)
        {
            return Task.CompletedTask;
        }
        _editingTask = null;
        EditorTitle = "";
        EditorHasDueDate = false;
        EditorDueDate = DateTimeOffset.Now.Date.AddDays(1);
        EditorDueTime = new TimeSpan(9, 0, 0);
        EditorError = null;
        OpenEditor();
        return Task.CompletedTask;
    }

    internal Task OpenEditTaskAsync(TaskWorkspaceItem item)
    {
        _editingTask = item;
        SelectedList = item.List;
        EditorTitle = item.Info.Title;
        EditorHasDueDate = item.Info.DueAt is not null;
        EditorDueDate = item.Info.DueAt?.ToLocalTime();
        EditorDueTime = item.Info.DueAt?.ToLocalTime().TimeOfDay;
        EditorError = null;
        OpenEditor();
        return Task.CompletedTask;
    }

    private void OpenEditor()
    {
        IsEditorOpen = true;
        RaisePropertyChanged(nameof(IsEditing));
        RaisePropertyChanged(nameof(EditorHeading));
    }

    internal async Task SaveEditorAsync()
    {
        var list = _editingTask?.List ?? SelectedList;
        if (list is null)
        {
            EditorError = "Select a task list first.";
            return;
        }
        if (string.IsNullOrWhiteSpace(EditorTitle))
        {
            EditorError = "Enter a task title.";
            return;
        }
        if (EditorHasDueDate && EditorDueDate is null)
        {
            EditorError = "Choose a due date.";
            return;
        }

        EditorError = null;
        try
        {
            var draft = new TaskDraft(
                list.Group.Account.AccountId,
                list.Info.ProviderId,
                EditorTitle.Trim(),
                EditorHasDueDate ? CombineDueDate(EditorDueDate!.Value, EditorDueTime) : null);
            var saved = _editingTask is null
                ? await _provider.CreateTaskAsync(list.Group.Account, draft)
                : await _provider.UpdateTaskAsync(
                    list.Group.Account, _editingTask.Info.ProviderId, draft);
            ValidateOwned(saved, list);
            IsEditorOpen = false;
            await RefreshListAndSelectAsync(list, saved.ProviderId);
        }
        catch (Exception ex)
        {
            EditorError = ex.Message;
        }
    }

    internal async Task ToggleCompleteAsync(TaskWorkspaceItem item)
    {
        OperationError = null;
        try
        {
            var updated = await _provider.SetTaskCompletedAsync(
                item.List.Group.Account,
                item.List.Info,
                item.Info.ProviderId,
                !item.Info.IsComplete);
            ValidateOwned(updated, item.List);
            await RefreshListAndSelectAsync(item.List, updated.ProviderId);
        }
        catch (Exception ex)
        {
            OperationError = ex.Message;
        }
    }

    private Task RequestDeleteItemAsync(TaskWorkspaceItem item)
    {
        PendingDelete = item;
        return Task.CompletedTask;
    }

    internal Task RequestDeleteAsync()
    {
        PendingDelete = SelectedTask;
        return Task.CompletedTask;
    }

    internal async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { } item)
        {
            return;
        }
        OperationError = null;
        try
        {
            await _provider.DeleteTaskAsync(
                item.List.Group.Account, item.List.Info, item.Info.ProviderId);
            PendingDelete = null;
            SelectedTask = null;
            await RefreshListAndSelectAsync(item.List, null);
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

    private Task CloseEditorAsync()
    {
        IsEditorOpen = false;
        EditorError = null;
        return Task.CompletedTask;
    }

    private async Task RefreshListAndSelectAsync(TaskListChoice list, string? taskId)
    {
        await LoadListAsync(list, CancellationToken.None);
        RebuildVisibleTasks();
        SelectedTask = taskId is null
            ? null
            : VisibleTasks.FirstOrDefault(task =>
                task.List == list && task.Info.ProviderId == taskId);
        RaisePartialErrors();
    }

    private void RebuildVisibleTasks()
    {
        var query = SearchText.Trim();
        var source = SelectedList is null
            ? AccountGroups.SelectMany(static group => group.Lists).SelectMany(static list => list.Tasks)
            : SelectedList.Tasks;
        var tasks = source
            .Where(task => query.Length == 0 ||
                           task.Info.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           task.AccountIdentity.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           task.List.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static task => task.Info.IsComplete)
            .ThenBy(static task => task.Info.DueAt is null)
            .ThenBy(static task => task.Info.DueAt)
            .ThenBy(static task => task.Info.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        VisibleTasks.Clear();
        foreach (var task in tasks)
        {
            VisibleTasks.Add(task);
        }
        RaisePropertyChanged(nameof(HasTasks));
        RaisePropertyChanged(nameof(HasNoTasks));
        RaisePropertyChanged(nameof(OpenCount));
        RaisePropertyChanged(nameof(CompletedCount));
    }

    private void RaisePartialErrors()
    {
        RaisePropertyChanged(nameof(HasPartialErrors));
        RaisePropertyChanged(nameof(PartialErrorText));
    }

    private static void ValidateOwned(TaskInfo task, TaskListChoice list)
    {
        if (task.AccountId != list.Group.Account.AccountId ||
            task.ListId != list.Info.ProviderId)
        {
            throw new InvalidOperationException(
                "The task provider returned data for a different account or list.");
        }
    }

    internal static DateTimeOffset CombineDueDate(
        DateTimeOffset date,
        TimeSpan? time)
    {
        var localDate = date.ToLocalTime();
        var localTime = time ?? TimeSpan.Zero;
        return new DateTimeOffset(
            localDate.Year,
            localDate.Month,
            localDate.Day,
            localTime.Hours,
            localTime.Minutes,
            0,
            TimeZoneInfo.Local.GetUtcOffset(localDate.Date));
    }
}

public sealed class TaskAccountGroup(MailAccount account) : ViewModelBase
{
    private string? _error;
    public MailAccount Account { get; } = account;
    public ObservableCollection<TaskListChoice> Lists { get; } = [];
    public string DisplayName => string.IsNullOrWhiteSpace(Account.DisplayName)
        ? Account.EmailAddress
        : Account.DisplayName;
    public string Identity => $"{Account.EmailAddress} · {Account.ProviderId}";
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
                            Lists.Any(static list => list.HasError);

    public IEnumerable<string> Errors()
    {
        if (!string.IsNullOrWhiteSpace(Error))
        {
            yield return Error;
        }
        foreach (var error in Lists.Select(static list => list.Error)
                     .Where(static error => !string.IsNullOrWhiteSpace(error)))
        {
            yield return error!;
        }
    }
}

public sealed class TaskListChoice(TaskAccountGroup group, TaskListInfo info) : ViewModelBase
{
    private string? _error;
    private bool _isSelected;
    public TaskAccountGroup Group { get; } = group;
    public TaskListInfo Info { get; } = info;
    public ObservableCollection<TaskWorkspaceItem> Tasks { get; } = [];
    public string DisplayName => Info.DisplayName;
    public string Provenance => $"{Group.Account.EmailAddress} · {Info.DisplayName}";
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
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public void Replace(IEnumerable<TaskWorkspaceItem> tasks)
    {
        Tasks.Clear();
        foreach (var task in tasks)
        {
            Tasks.Add(task);
        }
    }
}

public sealed class TaskWorkspaceItem(TaskListChoice list, TaskInfo info)
{
    public TaskListChoice List { get; } = list;
    public TaskInfo Info { get; } = info;
    public string AccountIdentity =>
        $"{List.Group.Account.DisplayName} · {List.Group.Account.EmailAddress} · {List.Group.Account.ProviderId}";
    public string Provenance => $"{List.Group.Account.EmailAddress} / {List.DisplayName}";
    public string CompletionGlyph => Info.IsComplete ? "☑" : "☐";
    public string CompletionAction => Info.IsComplete ? "Mark incomplete" : "Mark complete";
}
