using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class TasksWorkspaceViewModelTests
{
    private static readonly MailAccount AccountA = new(
        "microsoft365",
        "account-a",
        "tenant",
        "a@example.com",
        "Account A",
        ProviderCapabilities.Tasks);

    private static readonly MailAccount AccountB = AccountA with
    {
        AccountId = "account-b",
        EmailAddress = "b@example.com",
        DisplayName = "Account B"
    };

    [Fact]
    public void UsesPhoneFriendlyTasksBreakpoint()
    {
        Assert.True(TasksWorkspaceView.IsCompactWidth(759));
        Assert.False(TasksWorkspaceView.IsCompactWidth(760));
        Assert.True(TasksWorkspaceView.IsPhoneWidth(559));
        Assert.False(TasksWorkspaceView.IsPhoneWidth(560));
    }

    [Fact]
    public async Task AggregatesEveryAccountAndIsolatesAccountFailures()
    {
        var provider = new FakeTasksProvider("account-b");
        var viewModel = new TasksWorkspaceViewModel(provider, [AccountA, AccountB]);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, viewModel.AccountGroups.Count);
        Assert.True(viewModel.IsAllTasksSelected);
        Assert.Equal("All tasks", viewModel.Heading);
        Assert.Single(viewModel.VisibleTasks);
        Assert.Equal("a@example.com / Tasks", viewModel.VisibleTasks[0].Provenance);
        Assert.True(viewModel.HasPartialErrors);
        Assert.Contains("b@example.com", viewModel.PartialErrorText);
    }

    [Fact]
    public async Task FiltersAllAccountsAndPreservesListSelection()
    {
        var provider = new FakeTasksProvider();
        var viewModel = new TasksWorkspaceViewModel(provider, [AccountA, AccountB]);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        viewModel.SearchText = "account b task";
        await viewModel.SearchAsync();

        var result = Assert.Single(viewModel.VisibleTasks);
        Assert.Equal(AccountB.AccountId, result.Info.AccountId);
        Assert.Contains("b@example.com", result.Provenance);

        viewModel.SearchText = "";
        var list = viewModel.AccountGroups[0].Lists[0];
        await viewModel.SelectListAsync(list);

        Assert.Equal(list, viewModel.SelectedList);
        Assert.All(viewModel.VisibleTasks, task => Assert.Same(list, task.List));
        Assert.True(list.IsSelected);
        await viewModel.ShowAllTasksAsync();
        Assert.False(list.IsSelected);
        Assert.True(viewModel.IsAllTasksSelected);
    }

    [Fact]
    public async Task CreatesEditsCompletesAndDeletesOwnedTask()
    {
        var provider = new FakeTasksProvider();
        var viewModel = new TasksWorkspaceViewModel(provider, [AccountA]);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        var list = Assert.Single(viewModel.AccountGroups).Lists[0];
        await viewModel.SelectListAsync(list);

        await viewModel.OpenNewTaskAsync();
        viewModel.EditorTitle = " File report ";
        viewModel.EditorHasDueDate = true;
        viewModel.EditorDueDate = new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero);
        viewModel.EditorDueTime = new TimeSpan(14, 30, 0);
        await viewModel.SaveEditorAsync();

        Assert.Equal("File report", provider.LastDraft!.Title);
        Assert.Equal(14, provider.LastDraft.DueAt!.Value.ToLocalTime().Hour);
        Assert.Equal(30, provider.LastDraft.DueAt.Value.ToLocalTime().Minute);
        var created = Assert.Single(viewModel.VisibleTasks, task => task.Info.ProviderId == "created-task");
        Assert.Equal(AccountA.AccountId, created.Info.AccountId);

        await viewModel.OpenEditTaskAsync(created);
        viewModel.EditorTitle = "Updated report";
        viewModel.EditorHasDueDate = false;
        await viewModel.SaveEditorAsync();
        Assert.Equal("Updated report", provider.LastDraft.Title);
        Assert.Null(provider.LastDraft.DueAt);

        var updated = Assert.Single(viewModel.VisibleTasks, task => task.Info.ProviderId == "created-task");
        await viewModel.ToggleCompleteAsync(updated);
        Assert.True(provider.LastCompletion);
        var completed = Assert.Single(viewModel.VisibleTasks, task => task.Info.ProviderId == "created-task");
        Assert.True(completed.Info.IsComplete);

        viewModel.SelectedTask = completed;
        await viewModel.RequestDeleteAsync();
        Assert.True(viewModel.IsDeleteConfirmationOpen);
        await viewModel.ConfirmDeleteAsync();

        Assert.Equal("created-task", provider.DeletedTaskId);
        Assert.DoesNotContain(
            viewModel.VisibleTasks,
            static task => task.Info.ProviderId == "created-task");
    }

    [Fact]
    public void CombinesLocalDueDateAndTime()
    {
        var due = TasksWorkspaceViewModel.CombineDueDate(
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            new TimeSpan(9, 45, 0));

        Assert.Equal(new DateOnly(2026, 7, 20), DateOnly.FromDateTime(due.LocalDateTime));
        Assert.Equal(new TimeOnly(9, 45), TimeOnly.FromDateTime(due.LocalDateTime));
    }

    private sealed class FakeTasksProvider(string? failingAccountId = null) : ITasksProvider
    {
        private readonly Dictionary<(string AccountId, string ListId), List<TaskInfo>> _tasks = new()
        {
            [("account-a", "list-account-a")] =
            [
                new(
                    "task-a",
                    "list-account-a",
                    "Account A task",
                    new DateTimeOffset(2026, 7, 18, 9, 0, 0, TimeSpan.Zero),
                    false,
                    "account-a")
            ],
            [("account-b", "list-account-b")] =
            [
                new(
                    "task-b",
                    "list-account-b",
                    "Account B task",
                    null,
                    false,
                    "account-b")
            ]
        };

        public TaskDraft? LastDraft { get; private set; }
        public bool LastCompletion { get; private set; }
        public string? DeletedTaskId { get; private set; }

        public async Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            var lists = await GetTaskListsAsync(account, cancellationToken);
            var tasks = new List<TaskInfo>();
            foreach (var list in lists)
            {
                tasks.AddRange(await GetTasksAsync(account, list, cancellationToken));
            }
            return tasks;
        }

        public Task<IReadOnlyList<TaskListInfo>> GetTaskListsAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            if (account.AccountId == failingAccountId)
            {
                throw new HttpRequestException("Tasks permission denied.");
            }
            return Task.FromResult<IReadOnlyList<TaskListInfo>>(
                [new($"list-{account.AccountId}", "Tasks", account.AccountId, "defaultList")]);
        }

        public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account,
            TaskListInfo list,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskInfo>>(
                _tasks.GetValueOrDefault((account.AccountId, list.ProviderId))?.ToArray() ?? []);

        public Task<TaskInfo> CreateTaskAsync(
            MailAccount account,
            TaskDraft draft,
            CancellationToken cancellationToken = default)
        {
            LastDraft = draft;
            var task = new TaskInfo(
                "created-task",
                draft.ListId,
                draft.Title,
                draft.DueAt,
                false,
                draft.AccountId);
            _tasks[(account.AccountId, draft.ListId)].Add(task);
            return Task.FromResult(task);
        }

        public Task<TaskInfo> UpdateTaskAsync(
            MailAccount account,
            string taskId,
            TaskDraft draft,
            CancellationToken cancellationToken = default)
        {
            LastDraft = draft;
            var tasks = _tasks[(account.AccountId, draft.ListId)];
            var index = tasks.FindIndex(task => task.ProviderId == taskId);
            tasks[index] = tasks[index] with { Title = draft.Title, DueAt = draft.DueAt };
            return Task.FromResult(tasks[index]);
        }

        public Task<TaskInfo> SetTaskCompletedAsync(
            MailAccount account,
            TaskListInfo list,
            string taskId,
            bool isCompleted,
            CancellationToken cancellationToken = default)
        {
            LastCompletion = isCompleted;
            var tasks = _tasks[(account.AccountId, list.ProviderId)];
            var index = tasks.FindIndex(task => task.ProviderId == taskId);
            tasks[index] = tasks[index] with { IsComplete = isCompleted };
            return Task.FromResult(tasks[index]);
        }

        public Task DeleteTaskAsync(
            MailAccount account,
            TaskListInfo list,
            string taskId,
            CancellationToken cancellationToken = default)
        {
            DeletedTaskId = taskId;
            _tasks[(account.AccountId, list.ProviderId)]
                .RemoveAll(task => task.ProviderId == taskId);
            return Task.CompletedTask;
        }
    }
}
