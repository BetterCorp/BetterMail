using BetterMail.App;
using BetterMail.Core;
using Avalonia.Input;

namespace BetterMail.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task UncachedPeopleShowNonBlockingProgressUntilProviderCompletes()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-empty-people-" + Guid.NewGuid());
        var provider = new FakeWorkspaceProvider { ContactGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.test", "Me", ProviderCapabilities.Contacts);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, workspaceProvider: provider);
            vm.Accounts.Add(account);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            vm.Mailboxes.Add(mailbox);
            vm.ContactOwners.Add(new(account, mailbox));
            await ((AsyncCommand)vm.ShowContactsCommand).ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Empty(vm.People);
            Assert.True(vm.IsPeopleRefreshing);
            Assert.False(vm.IsWorkspaceLoading);
            Assert.False(vm.IsWorkspaceEmpty);
            provider.ContactGate.SetResult();
            await vm.PeopleBackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(vm.IsPeopleRefreshing);
            Assert.True(vm.IsWorkspaceEmpty);
        }
        finally
        {
            provider.ContactGate.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("drive", "Drive")]
    [InlineData("notes", "Notes")]
    public async Task OpeningStructuredWorkspaceSearchUsesOnlyContentTerms(string type, string module)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-open-search-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "alex@example.test", "Alex", ProviderCapabilities.Files | ProviderCapabilities.Notes);
            await store.SaveAccountAsync(account, token);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, workspaceProvider: new FakeWorkspaceProvider());
            vm.Accounts.Add(account);
            vm.SearchText = $"Planning type:{type} account:{{alex@example.test}}";
            await ((AsyncCommand)vm.SearchCommand).ExecuteAsync();
            var result = Assert.Single(vm.GlobalSearchResults, item => item.Module == module);
            await ((AsyncCommand<GlobalSearchResult>)vm.OpenGlobalSearchResultCommand).ExecuteAsync(result);
            if (module == "Drive")
            {
                Assert.Equal("Planning", vm.DriveWorkspace!.SearchQuery);
                await WaitUntilAsync(() => !vm.DriveWorkspace.IsBusy, token);
                Assert.NotEmpty(vm.DriveWorkspace.SearchResults);
            }
            else
            {
                Assert.Equal("Planning", vm.NotesWorkspace!.SearchText);
                Assert.NotEmpty(vm.NotesWorkspace.VisibleRoots);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MovePickerResolvesCurrentCachedMessageAfterSync(bool alreadyInDestination, bool standalone)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-move-picker-" + Guid.NewGuid());
        var provider = new RecordingProvider { MoveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        MainWindowViewModel? vm = null;
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            vm.Accounts.Add(account);
            vm.Mailboxes.Add(mailbox);
            var destination = new MailFolderItem(new(mailbox.Id, "archive", "Archive", 0, 0), "Me");
            vm.Folders.Add(destination);
            var stale = Message(mailbox.Id, "inbox", "Old subject", "Old body") with { IsRead = true };
            vm.Messages.Add(stale);
            vm.SetSelectedMessages([stale], stale);
            var snapshot = vm.MoveSelectionSnapshot();
            var fresh = stale with { FolderId = alreadyInDestination ? "archive" : "other", Subject = "Updated subject", Body = "Updated body", IsFlagged = true, IsRead = false };
            await store.ApplySyncPageAsync("fresh", new([fresh], null, false), token);
            if (standalone) await vm.HandlePreviewActionAsync(new(ConversationAction.Move, snapshot[0], destination));
            else await vm.MoveSnapshotToFolderAsync(snapshot, destination);
            var actions = await store.GetMailActionsAsync(token);
            if (alreadyInDestination) Assert.Empty(actions);
            else
            {
                var action = Assert.Single(actions);
                Assert.Equal("other", action.SourceFolderId);
                Assert.True(action.SourceWasUnread);
                Assert.Equal("Updated subject", action.Subject);
            }
            var cached = await store.GetMessageAsync(fresh.MailboxId, fresh.ProviderId, token);
            Assert.Equal("Updated body", cached!.Body);
            Assert.True(cached.IsFlagged);
            provider.MoveRelease.SetResult();
            await WaitUntilAsync(() => !vm.IsSyncing, token);
        }
        finally
        {
            provider.MoveRelease.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UncachedCalendarShowsProgressWhileProviderIsBlocked()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-empty-calendar-" + Guid.NewGuid());
        var provider = new FakeWorkspaceProvider
        {
            CalendarGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "cached", "tenant", "me@example.test", "Me", ProviderCapabilities.Calendar);
            var vm = new CalendarWorkspaceViewModel(provider, [account], store: store);
            await vm.InitializeAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Empty(vm.CalendarGroups.SelectMany(group => group.Calendars));
            Assert.True(vm.IsLoading);
            Assert.False(vm.BackgroundRefresh.IsCompleted);
            provider.CalendarGate.SetResult();
            await vm.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(vm.IsLoading);
            Assert.NotEmpty(vm.CalendarGroups.SelectMany(group => group.Calendars));
        }
        finally
        {
            provider.CalendarGate.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CachedPeopleAndCalendarOpenBeforeBlockedProviderCompletes()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-cached-views-" + Guid.NewGuid());
        var provider = new FakeWorkspaceProvider
        {
            ContactGate = new(TaskCreationOptions.RunContinuationsAsynchronously),
            CalendarGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "cached", "tenant", "me@example.test", "Me", ProviderCapabilities.Contacts | ProviderCapabilities.Calendar);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            var contact = new ContactInfo("cached-contact", "Cached person", ["cached@example.test"], account.AccountId);
            var calendar = new CalendarInfo("calendar", "Calendar", "#0F6CBD", true, account.AccountId);
            await store.ReplaceWorkspaceItemsAsync("contact", account.AccountId, "all", new[] { contact }, c => c.ProviderId, c => c.DisplayName, token);
            await store.ReplaceWorkspaceItemsAsync("calendar", account.AccountId, "all", new[] { calendar }, c => c.ProviderId, c => c.Name, token);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, workspaceProvider: provider);
            vm.Accounts.Add(account);
            vm.Mailboxes.Add(mailbox);
            vm.ContactOwners.Add(new(account, mailbox));
            await ((AsyncCommand)vm.ShowContactsCommand).ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(provider.ContactGate.Task.IsCompleted);
            Assert.Contains(vm.People, person => person.DisplayName == "Cached person");
            Assert.False(vm.IsWorkspaceLoading);
            vm.ModuleSearchText = "Planning";
            await ((AsyncCommand)vm.ShowContactsCommand).ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            await ((AsyncCommand)vm.ShowCalendarCommand).ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.False(provider.CalendarGate.Task.IsCompleted);
            Assert.Single(vm.CalendarWorkspace!.CalendarGroups.Single().Calendars);
            provider.CalendarGate.SetResult();
            provider.ContactGate.SetResult();
            await vm.CalendarWorkspace.BackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(5), token);
            await vm.PeopleBackgroundRefresh.WaitAsync(TimeSpan.FromSeconds(5), token);
            Assert.Contains(vm.People, person => person.DisplayName == "Planning Person");
        }
        finally
        {
            provider.ContactGate.TrySetResult();
            provider.CalendarGate.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BusyCancellationRestoresDraftAndDisablesMailActionsWithStaleSelection()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-cancel-ui-{Guid.NewGuid():N}");
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var provider = new RecordingProvider { SyncRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), key);
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            // Empty folders are skipped by sync, so keep this folder eligible for the SyncRelease gate.
            provider.FolderResults = [new(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox")];
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            viewModel.Accounts.Add(account);
            viewModel.Mailboxes.Add(mailbox);
            // Hold an already-running sync so the newly queued send remains cancellable.
            viewModel.SyncCommand.Execute(null);
            await provider.SyncEntered.Task.WaitAsync(token);
            await viewModel.QueueSendAsync(new(account, mailbox), "cancel-send",
                new("Draft", [new("To", "to@example.com")], "Body", false, RequestReadReceipt: true, RequestDeliveryReceipt: true));
            await ((AsyncCommand)viewModel.ShowOutboxCommand).ExecuteAsync();
            // A late list refresh can select cached mail while Busy is visible.
            viewModel.SelectedMessage = new(mailbox.Id, "stale", null, null, "inbox", "Old selection",
                new("From", "from@example.com"), [], DateTimeOffset.UtcNow, "Body", "Body", false, true,
                false, MailImportance.Normal, [], null);
            foreach (var command in new[] { viewModel.DeleteCommand, viewModel.ArchiveCommand, viewModel.JunkCommand,
                         viewModel.NotJunkCommand, viewModel.ReplyCommand, viewModel.ReplyAllCommand, viewModel.ForwardCommand,
                         viewModel.ToggleReadCommand, viewModel.ToggleFlagCommand, viewModel.TogglePinCommand, viewModel.ViewHeadersCommand })
                Assert.False(command.CanExecute(null));
            Assert.False(viewModel.MoveToFolderCommand.CanExecute(new MailFolderItem(provider.FolderResults[0], "Me")));
            var action = Assert.Single(viewModel.BusyActions);
            Assert.True(viewModel.CancelBusyActionCommand.CanExecute(action));
            Assert.False(viewModel.CancelBusyActionCommand.CanExecute(action with { Running = true }));
            var cancellationCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.CancelBusyActionCommand.CanExecuteChanged += (_, _) =>
            {
                if (viewModel.CancelBusyActionCommand.CanExecute(action)) cancellationCompleted.TrySetResult();
            };
            viewModel.CancelBusyActionCommand.Execute(action);
            await cancellationCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Null(viewModel.Error);
            Assert.False(viewModel.HasOutbox);
            var draft = Assert.Single(viewModel.Drafts);
            Assert.True(draft.RequestReadReceipt);
            Assert.True(draft.RequestDeliveryReceipt);
            Assert.False(draft.IsQueued);
            provider.SyncRelease.SetResult();
            await WaitUntilAsync(() => !viewModel.IsSyncing, token);
            Assert.Equal(0, provider.SendCalls);
            Assert.Null(viewModel.Error);
        }
        finally
        {
            provider.SyncRelease.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SyncKeepsNavigationEnabledAndRetainsLoadedWorkspaceObjects()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-responsive-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingProvider { SyncRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('A', 64));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.test", "Me",
                ProviderCapabilities.Mail | ProviderCapabilities.Calendar | ProviderCapabilities.Contacts | ProviderCapabilities.Tasks);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            provider.FolderResults = [new(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox")];
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider, workspaceProvider: new FakeWorkspaceProvider());
            vm.Accounts.Add(account);
            vm.Mailboxes.Add(mailbox);
            vm.ContactOwners.Add(new(account, mailbox));
            await ((AsyncCommand)vm.ShowCalendarCommand).ExecuteAsync();
            await vm.CalendarWorkspace!.BackgroundRefresh;
            var group = Assert.Single(vm.CalendarWorkspace!.CalendarGroups);
            var sync = ((AsyncCommand)vm.SyncCommand).ExecuteAsync();
            await provider.SyncEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.True(vm.IsSyncing);
            Assert.False(vm.IsBusy);
            Assert.True(vm.ShowContactsCommand.CanExecute(null));
            vm.ModuleSearchText = "Planning";
            await ((AsyncCommand)vm.ShowContactsCommand).ExecuteAsync();
            await vm.PeopleBackgroundRefresh;
            Assert.Single(vm.People);
            Assert.True(vm.ShowCalendarCommand.CanExecute(null));
            await ((AsyncCommand)vm.ShowCalendarCommand).ExecuteAsync();
            Assert.Same(group, Assert.Single(vm.CalendarWorkspace.CalendarGroups));
            await ((AsyncCommand)vm.ShowUnifiedInboxCommand).ExecuteAsync();
            Assert.True(vm.IsMailModule);
            Assert.Contains(vm.SyncSteps, step => step.Running);
            provider.SyncRelease.TrySetResult();
            await sync.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.All(vm.SyncSteps, step => Assert.False(step.Running));
        }
        finally
        {
            provider.SyncRelease.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task AccountOrderPersistsAndOrdersPrimaryAndSharedMailboxes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('B', 64));
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            var provider = new FakeWorkspaceProvider();
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, workspaceProvider: provider);
            var first = new MailAccount("microsoft365", "first", "tenant", "first@example.test", "First", ProviderCapabilities.Mail | ProviderCapabilities.Calendar | ProviderCapabilities.Notes | ProviderCapabilities.Tasks | ProviderCapabilities.Files);
            var second = first with { AccountId = "second", EmailAddress = "second@example.test", DisplayName = "Second" };
            vm.Accounts.Add(first); vm.Accounts.Add(second);
            vm.Mailboxes.Add(new(first.AccountId, first.EmailAddress, "First"));
            vm.Mailboxes.Add(new(second.AccountId, second.EmailAddress, "Second"));
            vm.Mailboxes.Add(new(second.AccountId, "shared@example.test", "Shared", IsShared: true));
            foreach (var command in new[] { vm.ShowCalendarCommand, vm.ShowNotesCommand, vm.ShowTasksCommand, vm.ShowFilesCommand })
                await ((AsyncCommand)command).ExecuteAsync();
            var calendarGroups = vm.CalendarWorkspace!.CalendarGroups.ToArray();
            var notesRoots = vm.NotesWorkspace!.AccountRoots.ToArray();
            var taskGroups = vm.TasksWorkspace!.AccountGroups.ToArray();
            var driveRoots = vm.DriveWorkspace!.Roots.ToArray();
            var selectedDrive = vm.DriveWorkspace.SelectedDirectory;
            var calendarRequests = provider.CalendarRequests;
            await ((AsyncCommand<MailAccount>)vm.MoveAccountUpCommand).ExecuteAsync(second);
            Assert.Equal(calendarGroups.Reverse(), vm.CalendarWorkspace.CalendarGroups);
            Assert.Same(calendarGroups[0], vm.CalendarWorkspace.CalendarGroups[1]);
            Assert.Equal(notesRoots.Reverse(), vm.NotesWorkspace.AccountRoots);
            Assert.Equal(taskGroups.Reverse(), vm.TasksWorkspace.AccountGroups);
            Assert.Equal(driveRoots.Reverse(), vm.DriveWorkspace.Roots);
            Assert.Same(selectedDrive, vm.DriveWorkspace.SelectedDirectory);
            Assert.Equal(calendarRequests, provider.CalendarRequests);
            Assert.Equal(new[] { "second", "first" }, vm.CalendarWorkspace.EditableCalendars.Select(option => option.Account.AccountId));
            Assert.Equal(["second", "second", "first"], vm.FolderGroups.Select(group => group.Mailbox.AccountId));
            AppPreferencesStore.Save(directory, new AppPreferences(AccountOrder: vm.GetAccountOrderPreferences()));
            var restored = new MainWindowViewModel(null, directory, _ => { }, _ => { }, null);
            restored.Accounts.Add(first); restored.Accounts.Add(second);
            restored.ConfigureAccountOrder(AppPreferencesStore.Load(directory).AccountOrder);
            Assert.Equal("second", restored.SettingsAccounts.First().Account.AccountId);
            Assert.False(restored.MoveAccountUpCommand.CanExecute(second));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NavigationCanReturnToLoadingCalendarWithoutDuplicateRequests()
    {
        var provider = new FakeWorkspaceProvider { CalendarGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vm = new MainWindowViewModel(null, Path.GetTempPath(), _ => { }, _ => { }, null, workspaceProvider: provider);
        vm.Accounts.Add(new("microsoft365", "account", "tenant", "me@example.test", "Me", ProviderCapabilities.Calendar | ProviderCapabilities.Contacts));
        var first = ((AsyncCommand)vm.ShowCalendarCommand).ExecuteAsync();
        try
        {
            Assert.Equal(1, provider.CalendarRequests);
            Assert.True(vm.ShowContactsCommand.CanExecute(null));
            await ((AsyncCommand)vm.ShowContactsCommand).ExecuteAsync();
            Assert.True(vm.ShowCalendarCommand.CanExecute(null));
            var back = ((AsyncCommand)vm.ShowCalendarCommand).ExecuteAsync();
            Assert.True(vm.IsCalendarModule);
            Assert.Equal(1, provider.CalendarRequests);
            provider.CalendarGate.TrySetResult();
            await Task.WhenAll(first, back);
            Assert.Single(vm.CalendarWorkspace!.CalendarGroups);
        }
        finally { provider.CalendarGate.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(390, 0)]
    [InlineData(719, 0)]
    [InlineData(720, 1)]
    [InlineData(1199, 1)]
    [InlineData(1200, 2)]
    public void ChoosesResponsiveShellLayout(double width, int expected) =>
        Assert.Equal((ResponsiveLayoutMode)expected, MainWindow.LayoutModeFor(width));

    [Theory]
    [InlineData(839, false)]
    [InlineData(840, true)]
    public void KeepsInlineMailActionsOnlyWhenTheyFit(double width, bool expected) =>
        Assert.Equal(expected, MainWindow.UsesInlineMailActions(width));

    [Theory]
    [InlineData(KeyModifiers.None, false)]
    [InlineData(KeyModifiers.Control, true)]
    [InlineData(KeyModifiers.Meta, true)]
    [InlineData(KeyModifiers.Shift, true)]
    public void PreservesMultipleMailSelectionOnlyWithASelectionModifier(
        KeyModifiers modifiers,
        bool expected) =>
        Assert.Equal(expected, MainWindow.PreservesMultiSelection(modifiers));

    [Fact]
    public void AccountDraftFolderShowsOnlyThatMailboxesEditableDrafts()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var first = new LocalDraft(
            "first", "account-one", "mailbox-one", "one@example.com", "", "", "First", "Body", [],
            DateTimeOffset.UtcNow);
        var second = first with
        {
            Id = "second",
            AccountId = "account-two",
            MailboxId = "mailbox-two",
            Subject = "Second"
        };
        viewModel.Drafts.Add(first);
        viewModel.Drafts.Add(second);
        var accountDrafts = new MailFolderItem(
            new MailFolder(first.MailboxId, "provider-drafts", "Drafts", 0, 1, "drafts"),
            "First account");

        viewModel.SelectFolderCommand.Execute(accountDrafts);

        Assert.True(viewModel.IsDraftsView);
        Assert.False(viewModel.IsAllDraftsView);
        Assert.True(accountDrafts.IsSelected);
        Assert.Equal(first.Id, Assert.Single(viewModel.VisibleDrafts).Id);

        viewModel.ShowDraftsCommand.Execute(null);

        Assert.True(viewModel.IsAllDraftsView);
        Assert.Equal(2, viewModel.VisibleDrafts.Count);
    }

    [Theory]
    [InlineData(Key.Escape, false, true, false, false, 1)]
    [InlineData(Key.Escape, true, false, true, false, 2)]
    [InlineData(Key.Escape, false, false, true, false, 0)]
    [InlineData(Key.Back, true, false, false, false, 3)]
    [InlineData(Key.Back, true, false, false, true, 0)]
    [InlineData(Key.Delete, true, false, false, false, 4)]
    [InlineData(Key.Delete, true, false, false, true, 0)]
    [InlineData(Key.Back, false, false, false, false, 0)]
    public void RoutesShellKeysOnlyToTheirSafeContext(
        Key key,
        bool isMailModule,
        bool isSettingsOpen,
        bool hasMailBackStage,
        bool isTextInput,
        int expected) =>
        Assert.Equal(expected, (int)MainWindow.ShellActionFor(
            key, isMailModule, isSettingsOpen, hasMailBackStage, isTextInput));

    [Fact]
    public async Task ShowsOnboardingAfterStartupWithoutAnAccount()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);

        Assert.False(viewModel.ShowOnboarding);
        Assert.True(viewModel.ShowFullScreenLoader);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.ShowOnboarding);
        Assert.False(viewModel.ShowFullScreenLoader);
    }

    [Fact]
    public async Task CancellingSignInReleasesTheLoader()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signIn = viewModel.RunSignInAsync("Opening sign-in...", async cancellationToken =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        await started.Task;

        Assert.True(viewModel.CanCancelSignIn);
        Assert.True(viewModel.ShowFullScreenLoader);
        await ((AsyncCommand)viewModel.CancelSignInCommand).ExecuteAsync();
        await signIn;

        Assert.False(viewModel.CanCancelSignIn);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("Sign-in cancelled", viewModel.Status);
    }

    [Fact]
    public async Task SettingsShowsLocalAndCloudMailboxStatistics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-stats-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [new(mailbox.Id, "inbox", "Inbox", 3, 10, "inbox")], cancellationToken);
            await store.ApplySyncPageAsync(
                "stats",
                new MailSyncPage([Message(mailbox.Id, "inbox", "Local mail", "Body")], null, false),
                cancellationToken);
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, new RecordingProvider());
            await viewModel.InitializeAsync();

            viewModel.OpenSettingsCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.HasMailStatistics && !viewModel.IsLoadingMailStatistics, cancellationToken);

            var statistics = Assert.Single(viewModel.MailStatistics);
            Assert.Equal(1, statistics.SyncedMessages);
            Assert.Equal(10, statistics.CloudMessages);
            Assert.Equal(1, statistics.SyncedUnread);
            Assert.Equal(3, statistics.CloudUnread);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NotificationPreviewMarksTheCachedMessageRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-notification-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [new(mailbox.Id, "inbox", "Inbox", 1, 1, "inbox")], cancellationToken);
            var message = Message(mailbox.Id, "inbox", "Notification", "Body") with { ProviderId = "notification-message" };
            await store.ApplySyncPageAsync("notification", new MailSyncPage([message], null, false), cancellationToken);
            var provider = new RecordingProvider();
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider, TimeSpan.FromHours(1));
            await viewModel.InitializeAsync();

            var preview = await viewModel.OpenNotificationAsync(mailbox.Id, "inbox", message.ProviderId);

            Assert.NotNull(preview);
            Assert.True(preview.Selected.IsRead);
            Assert.Contains(message.ProviderId, provider.MarkedReadIds);
            Assert.True((await store.GetMessageAsync(mailbox.Id, message.ProviderId, cancellationToken))!.IsRead);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
    [Fact]
    public void DismissesTheCurrentError()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, "Copy me");

        viewModel.DismissError();

        Assert.False(viewModel.HasError);
        Assert.Null(viewModel.Error);
    }

    [Fact]
    public void LoadsRemotePicturesOnlyAfterExplicitAction()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null)
        {
            SelectedMessage = Message(
                "mailbox",
                "inbox",
                "Pictures",
                "<p>Safe text</p><img src='https://images.example/banner.png'>")
        };

        Assert.True(viewModel.HasBlockedRemoteContent);
        Assert.DoesNotContain("images.example", Decode(viewModel.SelectedMessageBodyUri));

        viewModel.AllowRemoteContentCommand.Execute(null);

        Assert.False(viewModel.HasBlockedRemoteContent);
        Assert.Contains("https://images.example/banner.png", Decode(viewModel.SelectedMessageBodyUri));
    }

    [Fact]
    public void PersistsPerSenderSignaturesAndUsesTheSavedDefaultSender()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true, CanSendAs: true);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(primary);
        viewModel.Mailboxes.Add(shared);
        viewModel.ConfigureSenderPreferences(
            "Legacy signature",
            shared.Id,
            new Dictionary<string, string> { [shared.Id] = "Shared signature" });

        var sharedSettings = Assert.Single(viewModel.SenderSettings, sender => sender.MailboxId == shared.Id);
        var primarySettings = Assert.Single(viewModel.SenderSettings, sender => sender.MailboxId == primary.Id);
        Assert.True(sharedSettings.IsDefault);
        Assert.Contains("Shared signature", sharedSettings.NewMailSignature.Html);
        Assert.Contains("Legacy signature", primarySettings.NewMailSignature.Html);

        primarySettings.NewMailSignature = sharedSettings.NewMailSignature;
        viewModel.SetDefaultSenderCommand.Execute(primarySettings);
        ComposeRequest? requested = null;
        viewModel.ComposeRequested += request => requested = request;
        viewModel.ComposeCommand.Execute(null);

        Assert.Equal(primary.Id, viewModel.DefaultSenderMailboxId);
        Assert.Contains("Shared signature", viewModel.GetSenderSignatures()[primary.Id]);
        Assert.Equal(primary.Id, requested?.MailboxId);
        Assert.Equal(account.AccountId, requested?.AccountId);
        Assert.Empty(requested?.Body ?? "");
    }

    [Fact]
    public void NewPrimaryAndSharedMailboxesUseTheReadOnlyBetterMailDefault()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true, CanSendAs: true);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(primary);
        viewModel.Mailboxes.Add(shared);
        viewModel.ConfigureSenderPreferences("", null, null, [], new Dictionary<string, MailboxSignaturePreferences>());

        Assert.True(viewModel.Signatures[0].IsReadOnly);
        Assert.Equal(SignatureCatalog.DefaultId, viewModel.Signatures[0].Id);
        foreach (var sender in viewModel.SenderSettings)
        {
            Assert.Equal(SignatureCatalog.DefaultId, sender.NewMailSignature.Id);
            Assert.Equal(SignatureCatalog.DefaultId, sender.ReplySignature.Id);
            Assert.Equal(SignatureCatalog.DefaultId, sender.ReplyAllSignature.Id);
            Assert.Equal(SignatureCatalog.DefaultId, sender.ForwardSignature.Id);
        }
    }

    [Fact]
    public void SignatureTemplateSelectionShowsTheRealHtmlBeforeCreating()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var template = Assert.Single(
            viewModel.SignatureTemplates,
            static candidate => candidate.Id == "professional");

        viewModel.SelectedSignatureTemplate = template;

        Assert.Contains("Product Director", Decode(viewModel.SelectedSignatureTemplatePreviewUri));
        viewModel.CreateSignatureFromTemplateCommand.Execute(null);
        Assert.Equal("Professional", viewModel.SelectedSignature?.Name);
        Assert.Contains("Product Director", viewModel.SelectedSignature?.Html);
        Assert.True(viewModel.HasUnsavedSignatureChanges);
        Assert.False(viewModel.CanManageSavedSignature);
        Assert.DoesNotContain(viewModel.SelectedSignature, viewModel.SignatureChoices);

        viewModel.SaveSignatureCommand.Execute(null);
        Assert.False(viewModel.HasUnsavedSignatureChanges);
        Assert.True(viewModel.CanManageSavedSignature);
        Assert.Contains(viewModel.SelectedSignature, viewModel.SignatureChoices);

        viewModel.SignatureEditorHtml += "<p>Changed</p>";
        Assert.True(viewModel.HasUnsavedSignatureChanges);
        viewModel.ResetSignatureCommand.Execute(null);
        Assert.False(viewModel.HasUnsavedSignatureChanges);

        var saved = viewModel.SelectedSignature;
        viewModel.DeleteSignatureCommand.Execute(null);
        Assert.True(viewModel.IsConfirmingSignatureDelete);
        Assert.Contains(saved?.Name ?? "", viewModel.SignatureDeleteText);
        Assert.Contains(saved, viewModel.Signatures);
        viewModel.CancelDeleteSignatureCommand.Execute(null);
        Assert.False(viewModel.IsConfirmingSignatureDelete);
        viewModel.DeleteSignatureCommand.Execute(null);
        viewModel.ConfirmDeleteSignatureCommand.Execute(null);
        Assert.DoesNotContain(saved, viewModel.Signatures);
    }

    [Fact]
    public void ConfiguresFourPersistedMailQuickActionsAndStableAccountColours()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);

        viewModel.ConfigureMailQuickActions(["none", "move", "more", "archive"]);

        Assert.Equal(["none", "move", "more", "archive"], viewModel.GetMailQuickActionPreferences());
        Assert.Equal(3, viewModel.MailQuickActions.Count);
        Assert.True(viewModel.MailQuickActions[0].IsMove);
        Assert.True(viewModel.MailQuickActions[1].IsMore);
        Assert.True(viewModel.HasMailQuickActions);
        viewModel.ConfigureMailQuickActions(["none", "none", "none", "none"]);
        Assert.Empty(viewModel.MailQuickActions);
        Assert.False(viewModel.HasMailQuickActions);
        Assert.Equal(AccountColors.For("mailbox-a"), AccountColors.For("mailbox-a"));
        Assert.StartsWith("#", AccountColors.For("mailbox-a"));
    }

    [Fact]
    public async Task ReplyAllExcludesEveryLinkedAddressAndSafelyDeduplicatesRecipients()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
        var second = account with { AccountId = "second", EmailAddress = "other-me@example.com" };
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Accounts.Add(second);
        viewModel.Mailboxes.Add(mailbox);
        viewModel.Mailboxes.Add(shared);
        viewModel.SelectedMessage = Message(mailbox.Id, "inbox", "RE: Planning", "Body") with
        {
            From = new MailAddress("Sender", "sender@example.com"),
            To =
            [
                new("Me", "ME@example.com"),
                new("Shared", "shared@example.com"),
                new("Other me", "other-me@example.com"),
                new("Sender duplicate", "SENDER@example.com"),
                new("Colleague", "colleague@example.com"),
                new("Colleague duplicate", "COLLEAGUE@example.com"),
                new("Invalid", "not an address")
            ],
            Cc =
            [
                new("Shared Cc", "SHARED@example.com"),
                new("Colleague duplicate from Cc", "colleague@example.com"),
                new("Cc Person", "cc@example.com")
            ],
            IsRead = true
        };
        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;

        viewModel.ReplyAllCommand.Execute(null);
        await WaitUntilAsync(() => request is not null, cancellationToken);

        Assert.Equal("sender@example.com", request!.To);
        Assert.Equal("colleague@example.com; cc@example.com", request.Cc);
        Assert.Empty(request.Bcc);
        Assert.Equal("RE: Planning", request.Subject);
        Assert.Equal(account.AccountId, request.AccountId);
        Assert.Equal(mailbox.Id, request.MailboxId);
        Assert.True(request.IsHtml);
        Assert.Contains("Body", request.Body);
        Assert.Contains("sender@example.com", request.Body);
        var recipients = $"{request.To};{request.Cc};{request.Bcc}";
        Assert.DoesNotContain("me@example.com", recipients, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shared@example.com", recipients, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other-me@example.com", recipients, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not an address", recipients, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplyAllDoesNotOpenComposeWhenOnlyLinkedRecipientsRemain()
    {
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);
        viewModel.SelectedMessage = Message(mailbox.Id, "inbox", "Subject", "Body") with
        {
            From = new MailAddress("Me", "me@example.com"),
            To = [new("Me", "ME@example.com")],
            IsRead = true
        };
        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;

        viewModel.ReplyAllCommand.Execute(null);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Null(request);
        Assert.Contains("no external recipients", viewModel.Error);
    }

    [Fact]
    public async Task ReplyAndForwardPreserveOriginalPicturesAndHtml()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var provider = new RecordingProvider
        {
            AttachmentResults =
            [
                new("inline", "logo.png", "image/png", 3, true, "logo@example", [1, 2, 3]),
                new("file", "report.pdf", "application/pdf", 3, false, null, [4, 5, 6])
            ]
        };
        var viewModel = new MainWindowViewModel(
            null, "data", _ => { }, _ => { }, null, provider);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);
        viewModel.SelectedMessage = Message(mailbox.Id, "inbox", "Pictures", "Preview") with
        {
            From = new MailAddress("Sender", "sender@example.com"),
            Body = "<p>Full HTML</p><img src='cid:logo@example'><img src='https://images.example/photo.jpg'>",
            IsHtml = true,
            HasAttachments = true,
            IsRead = true
        };
        var requests = new List<ComposeRequest>();
        viewModel.ComposeRequested += requests.Add;

        viewModel.ReplyCommand.Execute(null);
        await WaitUntilAsync(() => requests.Count == 1, cancellationToken);
        Assert.Contains("Full HTML", requests[0].Body);
        Assert.Contains("data:image/png;base64,AQID", requests[0].Body);
        Assert.Contains("https://images.example/photo.jpg", requests[0].Body);
        Assert.Empty(requests[0].Attachments ?? []);

        viewModel.ForwardCommand.Execute(null);
        await WaitUntilAsync(() => requests.Count == 2, cancellationToken);
        Assert.True(requests[1].IsHtml);
        Assert.Contains("data:image/png;base64,AQID", requests[1].Body);
        Assert.Equal("report.pdf", Assert.Single(requests[1].Attachments!).Name);
    }

    [Fact]
    public async Task ConversationSelectionReconcilesStableRowsAndRoutesExistingActions()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);
        var first = Message(mailbox.Id, "inbox", "Thread", "<p>First</p>") with
        {
            ProviderId = "first",
            ConversationId = "thread",
            IsRead = true
        };
        var second = Message(mailbox.Id, "inbox", "RE: Thread", "<p>Second</p>") with
        {
            ProviderId = "second",
            ConversationId = "thread",
            IsRead = true
        };
        viewModel.Messages.Add(first);
        viewModel.Messages.Add(second);
        viewModel.SelectedMessage = first;

        var thread = Assert.Single(viewModel.ConversationThread.Threads);
        var secondItem = thread.Messages[1];
        viewModel.ConversationThread.SelectMessageCommand.Execute(secondItem);
        Assert.Same(first, viewModel.SelectedMessage);
        Assert.Same(secondItem, viewModel.ConversationThread.SelectedMessage);

        // A delayed read/flag update must not take the reading pane back to the mail-list row.
        viewModel.SelectedMessage = first with { IsFlagged = true };
        Assert.Same(secondItem, viewModel.ConversationThread.SelectedMessage);
        Assert.Same(thread, viewModel.ConversationThread.SelectedThread);

        // An explicit click on another mail-list row still selects that message.
        viewModel.SelectedMessage = second;
        viewModel.SelectedMessage = first;
        Assert.Equal(first.ProviderId, viewModel.ConversationThread.SelectedMessage!.Message.ProviderId);
        viewModel.ConversationThread.SelectMessageCommand.Execute(secondItem);

        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;
        viewModel.ConversationThread.ForwardCommand.Execute(null);
        await WaitUntilAsync(() => request is not null, cancellationToken);
        Assert.Equal("Fwd: RE: Thread", request!.Subject);
    }

    [Fact]
    public async Task MoveFeedbackIsImmediateAndDoesNotResetANewerSelectionOrQueueSameFolder()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-move-selection-" + Guid.NewGuid());
        var provider = new RecordingProvider { MoveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        MainWindowViewModel? vm = null;
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "alex@work.example", "Alex", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Alex");
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            vm.Accounts.Add(account); vm.Mailboxes.Add(mailbox);
            var first = Message(mailbox.Id, "inbox", "First", "Body") with { ProviderId = "first", IsRead = true };
            var second = first with { ProviderId = "second", Subject = "Second", FolderId = "archive" };
            await store.ApplySyncPageAsync("seed", new([first, second], null, false), token);
            vm.Messages.Add(first); vm.Messages.Add(second);
            vm.SetSelectedMessages([first, second], first);
            var folder = new MailFolderItem(new(mailbox.Id, "archive", "Archive", 0, 0), "Alex");
            Assert.True(MainWindowViewModel.CanMoveMessagesToFolder([first, second], folder));
            var moving = vm.MoveSelectionToFolderAsync(folder);
            Assert.True(vm.IsMessageActionPending(first));
            Assert.False(vm.IsMessageActionPending(second));
            Assert.False(vm.IsMailActionRunning);
            vm.SetSelectedMessages([second], second);
            await moving;
            Assert.Equal("second", vm.SelectedMessage?.ProviderId);
            Assert.Equal("second", vm.ConversationThread.SelectedMessage?.Message.ProviderId);
            Assert.Equal("first", Assert.Single(await store.GetMailActionsAsync(token)).ItemId);
            Assert.Equal("second", Assert.Single(vm.Messages).ProviderId);
            provider.MoveRelease.TrySetResult();
            await WaitUntilAsync(() => !vm.IsSyncing, token);
        }
        finally { provider.MoveRelease.TrySetResult(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("deleteditems")]
    public async Task BulkMailActionQueuesEverySelectedMessage(string destination)
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-bulk-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingProvider { MoveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        MainWindowViewModel? vm = null;
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(token);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            vm.Accounts.Add(account);
            vm.Mailboxes.Add(mailbox);
            var messages = Enumerable.Range(0, 4).Select(i => Message(mailbox.Id, "inbox", "Mail " + i, "Body") with { ProviderId = "bulk-" + i, IsRead = true }).ToArray();
            foreach (var message in messages) vm.Messages.Add(message);
            vm.SetSelectedMessages(messages.Take(3), messages[2]);
            Assert.Contains("3 selected", vm.MailSelectionText);
            if (destination == "deleteditems") await ((AsyncCommand)vm.DeleteCommand).ExecuteAsync();
            else await vm.MoveSelectionToFolderAsync(new(new(mailbox.Id, destination, "Archive", 0, 0), "Me"));
            var actions = await store.GetMailActionsAsync(token);
            Assert.Equal(3, actions.Count);
            Assert.All(actions, action => Assert.Equal(destination, action.DestinationId));
            Assert.Equal(new[] { "bulk-0", "bulk-1", "bulk-2" }, actions.Select(action => action.ItemId).Order());
            Assert.Equal("bulk-3", Assert.Single(vm.Messages).ProviderId);
            provider.MoveRelease.SetResult();
            await WaitUntilAsync(() => !vm.IsSyncing, token);
        }
        finally
        {
            provider.MoveRelease.TrySetResult();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void NewlyAddedMailSelectionBecomesPrimaryImmediately()
    {
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null);
        var first = Message("mailbox", "inbox", "First", "First body") with { ProviderId = "first" };
        var second = Message("mailbox", "inbox", "Second", "Second body") with { ProviderId = "second" };
        viewModel.SelectedMessage = first;

        viewModel.SetSelectedMessages([first, second], second);

        Assert.Same(second, viewModel.SelectedMessage);
    }

    [Fact]
    public async Task RapidSelectionCancelsTheOldReadDelayAndKeepsTheNewSelection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-rapid-read-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 2, 2, "inbox");
            var first = Message(mailbox.Id, "inbox", "First", "First body") with
            {
                ProviderId = "first",
                ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
            };
            var second = Message(mailbox.Id, "inbox", "Second", "Second body") with
            {
                ProviderId = "second",
                ReceivedAt = DateTimeOffset.UtcNow
            };
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync("test", new MailSyncPage([first, second], null, false), cancellationToken);

            var delays = new System.Collections.Concurrent.ConcurrentQueue<(TaskCompletionSource Release, CancellationToken Token)>();
            Task WaitForMarkRead(TimeSpan delay, CancellationToken token)
            {
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                delays.Enqueue((release, token));
                return release.Task.WaitAsync(token);
            }
            var provider = new RecordingProvider();
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider, waitForMarkRead: WaitForMarkRead);
            await viewModel.InitializeAsync();
            var oldDelay = Assert.Single(delays);
            Assert.Empty(provider.MarkedReadIds);
            var oldSelection = viewModel.SelectedMessage!;
            var newSelection = viewModel.Messages.Single(message => message.ProviderId != oldSelection.ProviderId);
            viewModel.SelectedMessage = newSelection;
            Assert.True(oldDelay.Token.IsCancellationRequested);
            Assert.Equal(2, delays.Count);
            Assert.Empty(provider.MarkedReadIds);
            // Deliberately release the stale delay too; it must never mark the old message read.
            oldDelay.Release.TrySetResult();
            delays.Last().Release.TrySetResult();

            await WaitUntilAsync(
                () => provider.MarkedReadIds.Count == 1 && viewModel.SelectedMessage?.IsRead == true,
                cancellationToken);

            Assert.Equal(newSelection.ProviderId, Assert.Single(provider.MarkedReadIds));
            Assert.Equal(newSelection.ProviderId, viewModel.SelectedMessage?.ProviderId);
            Assert.True(viewModel.SelectedMessage?.IsRead);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task IncludesSharedMailboxesInMailAndContactSelectors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-shared-selectors-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "account", "tenant", "person@example.com", "Person",
                ProviderCapabilities.Mail | ProviderCapabilities.Contacts);
            var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var shared = new Mailbox(account.AccountId, "shared@example.com", "Shared", IsShared: true);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(primary, cancellationToken);
            await store.SaveMailboxAsync(shared, cancellationToken);
            await store.ApplySyncPageAsync(
                "test",
                new MailSyncPage([
                    Message(primary.Id, "inbox", "Planning primary", "Primary"),
                    Message(shared.Id, "inbox", "Planning shared", "Shared")
                ], null, false),
                cancellationToken);

            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null,
                new RecordingProvider(), workspaceProvider: new FakeWorkspaceProvider());
            await viewModel.InitializeAsync();

            var sharedFilter = Assert.Single(viewModel.SearchAccountFilters, filter => filter.MailboxId == shared.Id);
            Assert.Contains(viewModel.ContactOwners, owner => owner.Mailbox.Id == shared.Id);

            viewModel.SearchText = $"Planning type:mail account:{{{shared.Address}}}";
            viewModel.SearchCommand.Execute(null);
            await WaitUntilAsync(
                () => !viewModel.IsGlobalSearchRunning && viewModel.GlobalSearchResults.Count > 0,
                cancellationToken);
            Assert.All(
                viewModel.GlobalSearchResults.Where(result => result.Category == "Mail"),
                result => Assert.Equal(shared.Id, Assert.IsType<MailMessage>(result.Value).MailboxId));
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
    [Fact]
    public async Task GlobalSearchStreamsEveryWorkspaceCategoryWithoutBusyOverlay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-global-search-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "account", "tenant", "person@example.com", "Person",
                ProviderCapabilities.Mail | ProviderCapabilities.Calendar | ProviderCapabilities.Contacts |
                ProviderCapabilities.Tasks | ProviderCapabilities.Files | ProviderCapabilities.Notes);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var secondAccount = account with
            {
                AccountId = "second",
                EmailAddress = "other@example.com",
                DisplayName = "Other"
            };
            var secondMailbox = new Mailbox(secondAccount.AccountId, secondAccount.EmailAddress, secondAccount.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveAccountAsync(secondAccount, cancellationToken);
            await store.SaveMailboxAsync(secondMailbox, cancellationToken);
            await store.ApplySyncPageAsync(
                "test",
                new MailSyncPage([
                    Message(mailbox.Id, "inbox", "Planning mail", "Planning body"),
                    Message(secondMailbox.Id, "inbox", "Planning update", "Planning body")
                ], null, false),
                cancellationToken);
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null,
                new RecordingProvider(), workspaceProvider: new FakeWorkspaceProvider());
            viewModel.Accounts.Add(account);
            viewModel.Accounts.Add(secondAccount);

            await ((AsyncCommand)viewModel.ShowFilesCommand).ExecuteAsync();

            viewModel.SearchText = "Planning";
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await ((AsyncCommand)viewModel.SearchCommand).ExecuteAsync();
            }

            Assert.False(viewModel.IsBusy);
            Assert.True(viewModel.IsGlobalSearchOpen);
            Assert.Equal(
                ["Calendar", "Drive", "Mail", "Notes", "People", "To Do"],
                viewModel.GlobalSearchResults.Select(result => result.Category).Distinct().Order().ToArray());
            Assert.Equal("Drive", viewModel.GlobalSearchResults[0].Category);
            Assert.All(viewModel.GlobalSearchResults, result => Assert.False(string.IsNullOrWhiteSpace(result.AccountGroup)));
            var mailResults = viewModel.GlobalSearchResults.Where(result => result.Category == "Mail").ToArray();
            Assert.Equal(2, mailResults.Length);
            Assert.Equal(mailResults.OrderByDescending(result => result.SortAt), mailResults);
            var mailResult = Assert.Single(mailResults, result => result.AccountGroup.Contains(account.EmailAddress));
            Assert.Contains(account.EmailAddress, mailResult.AccountGroup);
            Assert.DoesNotContain(account.EmailAddress, mailResult.Subtitle);
            Assert.True(mailResult.StartsAccountGroup);

            await ((AsyncCommand<GlobalSearchResult>)viewModel.OpenGlobalSearchGroupCommand).ExecuteAsync(mailResult);
            Assert.Single(viewModel.Messages);
            Assert.Equal(mailbox.Id, viewModel.Messages[0].MailboxId);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MailSearchShowsLocalResultsBeforeIncompleteCacheFallbackFinishes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-hybrid-search-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "account", "tenant", "person@example.com", "Person",
                ProviderCapabilities.Mail | ProviderCapabilities.ServerSearch);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var folder = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 2, "inbox");
            var local = Message(mailbox.Id, folder.ProviderId, "Needle local", "Local match") with
            {
                ProviderId = "local"
            };
            var remote = Message(mailbox.Id, folder.ProviderId, "Needle remote", "Remote match") with
            {
                ProviderId = "remote"
            };
            await store.ApplySyncPageAsync("seed", new MailSyncPage([local], null, false), cancellationToken);
            var provider = new RecordingProvider
            {
                SearchResults = [remote],
                SearchRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider);
            viewModel.Accounts.Add(account);
            viewModel.Mailboxes.Add(mailbox);
            viewModel.Folders.Add(new MailFolderItem(folder, mailbox.DisplayName));

            viewModel.SearchText = "Needle";
            viewModel.SearchCommand.Execute(null);
            await WaitUntilAsync(
                () => provider.SearchCalls == 1 &&
                    viewModel.GlobalSearchResults.Any(result => result.Title == local.Subject),
                cancellationToken);

            Assert.True(viewModel.IsGlobalSearchRunning);
            Assert.DoesNotContain(viewModel.GlobalSearchResults, result => result.Title == remote.Subject);
            provider.SearchRelease.SetResult();
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning, cancellationToken);

            Assert.Contains(viewModel.GlobalSearchResults, result => result.Title == remote.Subject);
            Assert.Contains(await store.SearchAsync("Needle", cancellationToken: cancellationToken),
                message => message.ProviderId == remote.ProviderId);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task KeyboardNavigationChangesSelectionWithoutFetchingOrReplacingRows()
    {
        var provider = new RecordingProvider();
        var viewModel = new MainWindowViewModel(
            null, "data", _ => { }, _ => { }, null, provider);
        var messages = new[]
        {
            Message("mailbox", "inbox", "First", "One") with { ProviderId = "first", IsRead = true },
            Message("mailbox", "inbox", "Second", "Two") with { ProviderId = "second", IsRead = true },
            Message("mailbox", "inbox", "Third", "Three") with { ProviderId = "third", IsRead = true }
        };
        foreach (var message in messages)
        {
            viewModel.Messages.Add(message);
        }
        viewModel.SelectedMessage = messages[1];

        viewModel.SelectNextMessageCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.SelectedMessage?.ProviderId == "third",
            TestContext.Current.CancellationToken);
        viewModel.SelectPreviousMessageCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.SelectedMessage?.ProviderId == "second",
            TestContext.Current.CancellationToken);

        Assert.Equal(messages, viewModel.Messages);
        Assert.Equal(0, provider.GetMessageCalls);
    }

    [Fact]
    public async Task LoadsAttachmentContentOnlyWhenRequested()
    {
        var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var metadata = new MailAttachment("attachment", "large.zip", "application/zip", 16 * 1024 * 1024, false, null, null);
        var provider = new RecordingProvider
        {
            AttachmentResults = [metadata],
            HydratedAttachmentResult = metadata with { ContentBytes = [1, 2, 3] }
        };
        var viewModel = new MainWindowViewModel(null, "data", _ => { }, _ => { }, null, provider);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);
        viewModel.SelectedMessage = Message(mailbox.Id, "inbox", "Attachment", "Body") with
        {
            HasAttachments = true
        };

        await WaitUntilAsync(() => viewModel.Attachments.Count == 1, TestContext.Current.CancellationToken);

        Assert.Null(viewModel.Attachments[0].ContentBytes);
        Assert.Equal(0, provider.GetAttachmentCalls);
        var hydrated = await viewModel.LoadAttachmentContentAsync(metadata, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 3], hydrated?.ContentBytes);
        Assert.Equal(1, provider.GetAttachmentCalls);
    }

    [Fact]
    public async Task MarksSelectedUnreadMessageAsReadAfterDelay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-read-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 1, 1, "inbox");
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync(
                "test",
                new MailSyncPage([Message(mailbox.Id, "inbox", "Unread", "<b>Body</b>") with { HasAttachments = true }], null, false),
                cancellationToken);

            var provider = new RecordingProvider
            {
                AttachmentResults = [new MailAttachment("attachment", "file.txt", "text/plain", 4, false, null, "test"u8.ToArray())]
            };
            var viewModel = new MainWindowViewModel(
                store,
                directory,
                _ => { },
                _ => { },
                null,
                provider,
                TimeSpan.FromMilliseconds(250));

            await viewModel.InitializeAsync();
            await WaitUntilAsync(() => viewModel.Attachments.Count == 1, cancellationToken);
            await WaitUntilAsync(() => viewModel.SelectedMessage?.Body == "<b>Body</b>", cancellationToken);
            await WaitUntilAsync(
                () => viewModel.ConversationThread.SelectedMessage?.BodyHtml.Contains("Body", StringComparison.Ordinal) == true,
                cancellationToken);
            viewModel.Messages.CollectionChanged += (_, args) =>
            {
                if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace)
                {
                    viewModel.SetSelectedMessages([]);
                }
            };
            var bodyRefreshes = 0;
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.SelectedMessageBodyUri))
                {
                    bodyRefreshes++;
                }
            };
            await WaitUntilAsync(() => provider.MarkedRead && viewModel.SelectedMessage?.IsRead == true, cancellationToken);

            Assert.True(provider.MarkedRead);
            Assert.True(viewModel.SelectedMessage?.IsRead);
            Assert.Equal("<b>Body</b>", viewModel.SelectedMessage?.Body);
            Assert.Contains("Body", viewModel.ConversationThread.SelectedMessage!.BodyHtml);
            Assert.Same(viewModel.Messages.Single(), viewModel.SelectedMessage);
            Assert.True(viewModel.DeleteCommand.CanExecute(null));
            Assert.Single(viewModel.Attachments);
            Assert.Equal(0, bodyRefreshes);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncUpdatesRowsWithoutReplacingTheSelectedMessageBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-sync-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox");
            var original = Message(mailbox.Id, "inbox", "Original", "<b>Stable body</b>") with
            {
                ProviderId = "message",
                ConversationId = "thread",
                IsRead = true
            };
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync("test", new MailSyncPage([original], null, false), cancellationToken);

            var updated = original with { Subject = "Updated", IsFlagged = true };
            var sent = original with
            {
                ProviderId = "sent",
                FolderId = "sentitems",
                Subject = "Sent reply",
                ReceivedAt = original.ReceivedAt.AddMinutes(1)
            };
            var provider = new BlockingSyncProvider(inbox, updated, sent);
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();
            await WaitUntilAsync(
                () => viewModel.ConversationThread.SelectedMessage?.Message.Body == original.Body,
                cancellationToken);
            var conversationMessage = viewModel.ConversationThread.SelectedMessage!;
            await WaitUntilAsync(
                () => conversationMessage.BodyHtml.Contains("Stable body", StringComparison.Ordinal),
                cancellationToken);
            var bodyRefreshes = 0;
            conversationMessage.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ConversationMessageItem.BodyHtml))
                {
                    bodyRefreshes++;
                }
            };
            var transientSelectionClears = 0;
            viewModel.Messages.CollectionChanged += (_, args) =>
            {
                if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace &&
                    args.OldItems?.OfType<MailMessage>().Any(message => message.ProviderId == "message") == true)
                {
                    viewModel.SetSelectedMessages([]);
                    transientSelectionClears += viewModel.SelectedMessage is null ? 1 : 0;
                }
            };

            viewModel.SyncCommand.Execute(null);
            viewModel.SyncCommand.Execute(null);
            await provider.Entered.Task.WaitAsync(cancellationToken);

            Assert.True(viewModel.IsSyncing);
            Assert.False(viewModel.IsBusy);
            Assert.Equal("message", viewModel.SelectedMessage?.ProviderId);

            provider.Release.TrySetResult();
            await WaitUntilAsync(() => !viewModel.IsSyncing && viewModel.SelectedMessage?.Subject == "Updated", cancellationToken);

            Assert.Equal(1, provider.SyncCalls);
            Assert.Equal(1, provider.MaxConcurrent);
            Assert.True(viewModel.SelectedMessage?.IsFlagged);
            Assert.Equal(2, viewModel.ConversationThread.SelectedThread?.Messages.Count);
            Assert.Contains(viewModel.ConversationThread.SelectedThread!.Messages, item => item.Message.ProviderId == "sent");
            Assert.Equal(0, bodyRefreshes);
            Assert.Equal(0, transientSelectionClears);

            viewModel.SearchText = "Updated";
            viewModel.SearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning && viewModel.GlobalSearchResults.Count == 1, cancellationToken);
            var searchSnapshot = viewModel.GlobalSearchResults.ToArray();
            viewModel.CloseGlobalSearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchOpen, cancellationToken);

            await WaitUntilAsync(() => viewModel.SyncCommand.CanExecute(null), cancellationToken);
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(() => provider.SyncCalls == 2 && !viewModel.IsSyncing, cancellationToken);

            Assert.False(viewModel.IsGlobalSearchOpen);
            Assert.Equal(searchSnapshot, viewModel.GlobalSearchResults);
            viewModel.ClearGlobalSearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.HasSearchText, cancellationToken);
            Assert.Equal("", viewModel.SearchText);

            var selectedAfterSync = viewModel.SelectedMessage;
            var conversationAfterSync = viewModel.ConversationThread.SelectedMessage;
            var unchangedListUpdates = 0;
            var unchangedFolderUpdates = 0;
            var unchangedFolderGroupUpdates = 0;
            viewModel.Messages.CollectionChanged += (_, _) => unchangedListUpdates++;
            viewModel.Folders.CollectionChanged += (_, _) => unchangedFolderUpdates++;
            viewModel.FolderGroups.CollectionChanged += (_, _) => unchangedFolderGroupUpdates++;
            await WaitUntilAsync(() => viewModel.SyncCommand.CanExecute(null), cancellationToken);
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(() => provider.SyncCalls == 3 && !viewModel.IsSyncing, cancellationToken);

            Assert.Equal(0, unchangedListUpdates);
            Assert.Equal(0, unchangedFolderUpdates);
            Assert.Equal(0, unchangedFolderGroupUpdates);
            Assert.Same(selectedAfterSync, viewModel.SelectedMessage);
            Assert.Same(conversationAfterSync, viewModel.ConversationThread.SelectedMessage);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SendingDuringSyncQueuesOneFollowUpSync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-send-sync-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "account", "tenant", "person@example.com", "Person",
                ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var sent = new MailFolder(mailbox.Id, "sentitems", "Sent Items", 0, 1, "sentitems");
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [sent], cancellationToken);

            var provider = new BlockingSyncProvider(
                sent,
                Message(mailbox.Id, sent.ProviderId, "Sent message", "Body"));
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();

            viewModel.SyncCommand.Execute(null);
            await provider.Entered.Task.WaitAsync(cancellationToken);
            await viewModel.QueueSendAsync(
                new ComposeSender(account, mailbox),
                "missing-local-draft",
                new DraftMessage(
                    "Sent message",
                    [new MailAddress("Recipient", "recipient@example.com")],
                    "Body",
                    false));

            provider.Release.TrySetResult();
            await WaitUntilAsync(
                () => provider.SyncCalls == 2 && !viewModel.IsSyncing,
                cancellationToken);

            Assert.Equal(2, provider.SyncCalls);
            Assert.Equal(1, provider.MaxConcurrent);
            Assert.Equal([1], provider.SyncCountsAtSend);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncsPrimaryAndSharedMailboxesInParallelAndKeepsFolderFailuresLocal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-mailbox-sync-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        var provider = new ParallelMailboxProvider();
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "account", "tenant", "person@example.com", "Person",
                ProviderCapabilities.Mail);
            var primary = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var shared = new Mailbox(
                account.AccountId, "shared@example.com", "Shared", IsShared: true);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(primary, cancellationToken);
            await store.SaveMailboxAsync(shared, cancellationToken);

            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(() => provider.EnteredCount == 2, cancellationToken);
            provider.Release.TrySetResult();
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);

            Assert.Equal(2, provider.MaxConcurrent);
            Assert.Single(await store.GetMessagesAsync(primary.Id, "sentitems", cancellationToken: cancellationToken));
            Assert.Single(await store.GetMessagesAsync(shared.Id, "inbox", cancellationToken: cancellationToken));
            Assert.Equal("Sync completed with issues", viewModel.Status);
            Assert.Contains("Broken folder", viewModel.Error);
        }
        finally
        {
            provider.Release.TrySetResult();
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SyncsCachedFoldersWhenFolderDiscoveryFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-cached-sync-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "account", "tenant", "person@example.com", "Person",
                ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox");
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);

            var provider = new DiscoveryFailureProvider(
                Message(mailbox.Id, inbox.ProviderId, "Recovered message", "Body"));
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(() => provider.SyncCalls == 1 && !viewModel.IsSyncing, cancellationToken);

            Assert.Single(await store.GetMessagesAsync(
                mailbox.Id, inbox.ProviderId, cancellationToken: cancellationToken));
            Assert.Equal("Sync completed with issues", viewModel.Status);
            Assert.Contains("Folder discovery unavailable", viewModel.Error);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task NavigatesFoldersSelectsMessagesAndRendersTheirBody()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-ui-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 1, 1, "inbox");
            var archive = new MailFolder(mailbox.Id, "archive", "Archive", 0, 1);
            var projects = new MailFolder(mailbox.Id, "projects", "Projects", 0, 1);
            var client = new MailFolder(mailbox.Id, "client", "Client", 0, 1, ParentProviderId: projects.ProviderId);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox, archive, projects, client], cancellationToken);
            await store.ApplySyncPageAsync("test", new MailSyncPage(
            [
                Message(mailbox.Id, "inbox", "Inbox message", "<b>Inbox body</b>"),
                Message(mailbox.Id, "archive", "Archive message", "<b>Archive body</b>")
            ], null, false), cancellationToken);

            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            await viewModel.InitializeAsync();
            Assert.Equal("Inbox message", viewModel.SelectedMessage?.Subject);
            var group = Assert.Single(viewModel.FolderGroups);
            var projectsNode = Assert.Single(group.Folders, node => node.Item.ProviderId == "projects");
            Assert.Equal("client", Assert.Single(projectsNode.Children).Item.ProviderId);

            var archiveItem = Assert.Single(viewModel.Folders, folder => folder.ProviderId == "archive");
            var readerGate = (SemaphoreSlim)typeof(EncryptedMailStore).GetField("_folderReadGate",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            await readerGate.WaitAsync(cancellationToken);
            Task current;
            try
            {
                var obsolete = ((AsyncCommand<MailFolderItem>)viewModel.SelectFolderCommand).ExecuteAsync(projectsNode.Item);
                current = ((AsyncCommand<MailFolderItem>)viewModel.SelectFolderCommand).ExecuteAsync(archiveItem);
                await obsolete.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                Assert.True(viewModel.IsLoadingMessages);
                Assert.False(viewModel.ShowEmptyState);
            }
            finally { readerGate.Release(); }
            await current;
            Assert.False(viewModel.IsLoadingMessages);
            await WaitUntilAsync(() => viewModel.CurrentFolderName == "Archive" && viewModel.SelectedMessage?.Body?.Contains("Archive body", StringComparison.Ordinal) == true, cancellationToken);

            Assert.Equal("Archive message", viewModel.SelectedMessage?.Subject);
            Assert.True(archiveItem.IsSelected);
            var html = Decode(viewModel.SelectedMessageBodyUri);
            Assert.Contains("<b>Archive body</b>", html);

            await ((AsyncCommand)viewModel.ShowUnifiedInboxCommand).ExecuteAsync();
            await WaitUntilAsync(() => viewModel.CurrentFolderName == "Inbox" && viewModel.SelectedMessage?.Subject == "Inbox message", cancellationToken);
            Assert.Equal("Inbox message", viewModel.SelectedMessage?.Subject);
            Assert.False(archiveItem.IsSelected);
            Assert.True(viewModel.IsUnifiedInbox);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FlagsAndDeletesTheUnreadSelectedMessageAfterMarkingItRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-actions-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 2, 2, "inbox");
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id, [inbox], cancellationToken);
            await store.ApplySyncPageAsync(
                "test",
                new MailSyncPage([
                    Message(mailbox.Id, "inbox", "Action message", "<b>Body</b>"),
                    Message(mailbox.Id, "inbox", "Second action", "<b>Second</b>")
                ], null, false),
                cancellationToken);

            var provider = new RecordingProvider
            {
                FolderResults = [inbox, new(mailbox.Id, "deleteditems", "Deleted Items", 0, 0, "deleteditems")]
            };
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();

            await ((AsyncCommand)viewModel.ToggleFlagCommand).ExecuteAsync();
            await WaitUntilAsync(() => provider.Flagged == true && viewModel.SelectedMessage?.IsFlagged == true, cancellationToken);
            await ((AsyncCommand)viewModel.TogglePinCommand).ExecuteAsync();
            await WaitUntilAsync(() => viewModel.SelectedMessage?.IsPinned == true && viewModel.PinnedMessageCount == 1, cancellationToken);
            viewModel.ShowPinnedCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.IsPinnedView && viewModel.Messages.Count == 1, cancellationToken);
            await ((AsyncCommand)viewModel.ShowUnifiedInboxCommand).ExecuteAsync();
            await WaitUntilAsync(() => viewModel.IsUnifiedInbox && viewModel.Messages.Count == 2, cancellationToken);

            provider.MoveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.DeleteCommand.Execute(null);
            await WaitUntilAsync(() => provider.MoveDestination == "deleteditems", cancellationToken);
            Assert.False(viewModel.IsMailActionRunning);
            Assert.Single(viewModel.Messages);
            Assert.Single(viewModel.BusyActions);
            Assert.Single(await store.GetMessagesAsync(mailbox.Id, "deleteditems", cancellationToken: cancellationToken));
            provider.MoveRelease.SetResult();
            await WaitUntilAsync(() => provider.MarkedRead && provider.MoveDestination == "deleteditems" && viewModel.Messages.Count == 1 && !viewModel.IsMailActionRunning, cancellationToken);
            Assert.True(viewModel.DeleteCommand.CanExecute(null));
            viewModel.DeleteCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.Messages.Count == 0 && !viewModel.IsMailActionRunning, cancellationToken);

            Assert.False(viewModel.IsBusy);
            Assert.False(viewModel.IsMailActionRunning);
            Assert.Null(viewModel.SelectedMessage);
            Assert.Empty(await store.GetMessagesAsync(mailbox.Id, "inbox", cancellationToken: cancellationToken));
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GlobalMailSearchExcludesArchivesUntilRequested()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-archive-search-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox");
            var archive = new MailFolder(mailbox.Id, "archive-id", "Archive", 0, 1, "archive");
            var onlineArchive = new MailFolder(mailbox.Id, "online-archive", "Online Archive - Person", 0, 1);
            await store.ApplySyncPageAsync("test", new MailSyncPage([
                Message(mailbox.Id, inbox.ProviderId, "Needle inbox", "Needle"),
                Message(mailbox.Id, archive.ProviderId, "Needle archive", "Needle"),
                Message(mailbox.Id, onlineArchive.ProviderId, "Needle online archive", "Needle")
            ], null, false), cancellationToken);

            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null);
            viewModel.Accounts.Add(account);
            viewModel.Mailboxes.Add(mailbox);
            foreach (var folder in new[] { inbox, archive, onlineArchive })
            {
                viewModel.Folders.Add(new MailFolderItem(folder, mailbox.DisplayName));
            }
            viewModel.SearchText = "Needle type:mail";
            viewModel.SearchCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning, cancellationToken);

            Assert.Single(viewModel.GlobalSearchResults);
            viewModel.SearchText = "Needle type:mail archives:true";
            await WaitUntilAsync(() => !viewModel.IsGlobalSearchRunning && viewModel.GlobalSearchResults.Count == 3, cancellationToken);
            Assert.Equal(3, viewModel.GlobalSearchResults.Count);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task OpensMicrosoft365WorkspaceModules()
    {
        var account = new MailAccount(
            "microsoft365", "account", "tenant", "person@example.com", "Person",
            ProviderCapabilities.Mail | ProviderCapabilities.Calendar | ProviderCapabilities.Contacts |
            ProviderCapabilities.Tasks | ProviderCapabilities.Files | ProviderCapabilities.Notes);
        var workspace = new FakeWorkspaceProvider();
        var viewModel = new MainWindowViewModel(
            null,
            "data",
            _ => { },
            _ => { },
            null,
            new RecordingProvider(),
            workspaceProvider: workspace);
        viewModel.Accounts.Add(account);

        viewModel.ShowCalendarCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ActiveModule == "Calendar" &&
                  viewModel.CalendarWorkspace?.CalendarGroups.Count == 1 &&
                  viewModel.CalendarWorkspace.DayColumns.SelectMany(day => day.Events).Count() == 1,
            TestContext.Current.CancellationToken);

        Assert.True(viewModel.IsCalendarModule);
        Assert.False(viewModel.IsMailModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var calendarWorkspace = viewModel.CalendarWorkspace;
        Assert.Same(calendarWorkspace, viewModel.ActiveWorkspace);

        var second = account with { AccountId = "second", EmailAddress = "second@example.com" };
        viewModel.Accounts.Add(second);
        viewModel.ShowCalendarCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.CalendarWorkspace?.CalendarGroups.Count == 2,
            TestContext.Current.CancellationToken);
        Assert.Same(calendarWorkspace, viewModel.CalendarWorkspace);

        viewModel.Accounts.Remove(second);
        viewModel.ShowCalendarCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.CalendarWorkspace?.CalendarGroups.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.Same(calendarWorkspace, viewModel.CalendarWorkspace);

        viewModel.ShowNotesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ActiveModule == "Notes" &&
                  viewModel.NotesWorkspace?.AccountRoots.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsNotesModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var notesWorkspace = viewModel.NotesWorkspace;
        Assert.Same(notesWorkspace, viewModel.ActiveWorkspace);

        viewModel.Accounts.Add(second);
        viewModel.ShowNotesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.NotesWorkspace?.AccountRoots.Count == 2,
            TestContext.Current.CancellationToken);
        Assert.Same(notesWorkspace, viewModel.NotesWorkspace);

        viewModel.Accounts.Remove(second);
        viewModel.ShowNotesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.NotesWorkspace?.AccountRoots.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.Same(notesWorkspace, viewModel.NotesWorkspace);

        viewModel.ShowTasksCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.ActiveModule == "To Do" &&
                  viewModel.TasksWorkspace?.AccountGroups.Count == 1 &&
                  viewModel.TasksWorkspace.VisibleTasks.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.True(viewModel.IsTasksModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var tasksWorkspace = viewModel.TasksWorkspace;
        Assert.Same(tasksWorkspace, viewModel.ActiveWorkspace);

        viewModel.Accounts.Add(second);
        viewModel.ShowTasksCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.TasksWorkspace?.AccountGroups.Count == 2,
            TestContext.Current.CancellationToken);
        Assert.Same(tasksWorkspace, viewModel.TasksWorkspace);

        viewModel.Accounts.Remove(second);
        viewModel.ShowTasksCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.TasksWorkspace?.AccountGroups.Count == 1,
            TestContext.Current.CancellationToken);
        Assert.Same(tasksWorkspace, viewModel.TasksWorkspace);
    }

    [Fact]
    public async Task IntegratesStableDriveWorkspaceAndAttachesUsingOwningAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var account = new MailAccount(
            "microsoft365", "drive-account", "tenant", "drive@example.com", "Drive",
            ProviderCapabilities.Mail | ProviderCapabilities.Files);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var workspace = new FakeWorkspaceProvider();
        var viewModel = new MainWindowViewModel(
            null,
            "data",
            _ => { },
            _ => { },
            null,
            new RecordingProvider(),
            workspaceProvider: workspace);
        viewModel.Accounts.Add(account);
        viewModel.Mailboxes.Add(mailbox);

        viewModel.ShowFilesCommand.Execute(null);
        await WaitUntilAsync(
            () => viewModel.DriveWorkspace?.CurrentItems.Count == 1,
            cancellationToken);

        Assert.True(viewModel.IsFilesModule);
        Assert.False(viewModel.IsGenericWorkspaceModule);
        var driveWorkspace = viewModel.DriveWorkspace!;
        var second = account with { AccountId = "second-drive", EmailAddress = "second@example.com" };
        viewModel.Accounts.Add(second);
        await viewModel.RefreshConnectedAccountsAsync();
        Assert.Same(driveWorkspace, viewModel.DriveWorkspace);
        Assert.Equal(2, driveWorkspace.Roots.Count);
        viewModel.Accounts.Remove(second);
        await viewModel.RefreshConnectedAccountsAsync();
        Assert.Same(driveWorkspace, viewModel.DriveWorkspace);
        Assert.Single(driveWorkspace.Roots);

        ComposeRequest? request = null;
        viewModel.ComposeRequested += value => request = value;
        driveWorkspace.SelectedItem = Assert.Single(driveWorkspace.CurrentItems);
        driveWorkspace.ChooseItemCommand.Execute(null);
        await WaitUntilAsync(() => request is not null, cancellationToken);

        Assert.Equal(account.AccountId, request!.AccountId);
        Assert.Equal(mailbox.Id, request.MailboxId);
        var attachment = Assert.Single(request.Attachments!);
        Assert.Equal("plan.txt", attachment.Name);
        Assert.Equal("plan", System.Text.Encoding.UTF8.GetString(attachment.ContentBytes));
        Assert.Equal(1, workspace.DownloadCount);

        request = null;
        var oversized = driveWorkspace.SelectedProviderItem! with
        {
            Item = driveWorkspace.SelectedProviderItem.Item with
            {
                Size = DraftAttachment.MaximumSizeBytes + 1
            }
        };
        await viewModel.AttachDriveItemAsync(oversized, cancellationToken);
        Assert.Null(request);
        Assert.Contains("150 MB", viewModel.Error);
        Assert.Equal(1, workspace.DownloadCount);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await viewModel.AttachDriveItemAsync(driveWorkspace.SelectedProviderItem, cancelled.Token);
        Assert.Null(request);
        Assert.Contains("cancelled", viewModel.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconcilesDraftsAtSyncBoundariesAndCompletesMappedSendAndDeleteOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-draft-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount(
                "microsoft365", "draft-account", "tenant", "draft@example.com", "Draft",
                ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            var local = new LocalDraft(
                "stable-local-id",
                account.AccountId,
                mailbox.Id,
                "recipient@example.com",
                "",
                "",
                "First version",
                "Body",
                [],
                DateTimeOffset.UtcNow);
            await store.SaveLocalDraftAsync(local, cancellationToken);
            var storedMessage = Message(mailbox.Id, "inbox", "Keep selected", "Body");
            await store.ApplySyncPageAsync(
                mailbox.Id,
                new MailSyncPage([storedMessage], null, false),
                cancellationToken);

            var provider = new LifecycleDraftProvider();
            var viewModel = new MainWindowViewModel(
                store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();
            await WaitUntilAsync(
                () => viewModel.Drafts.SingleOrDefault()?.ProviderDraftId is not null,
                cancellationToken);
            Assert.Empty(viewModel.Drafts.Single().Body);
            ComposeRequest? openedDraft = null;
            viewModel.ComposeRequested += request => openedDraft = request;
            viewModel.OpenDraftCommand.Execute(viewModel.Drafts.Single());
            await WaitUntilAsync(() => openedDraft is not null, cancellationToken);
            Assert.Equal("Body", openedDraft!.Body);

            var selected = Assert.Single(viewModel.Messages);
            viewModel.SelectedMessage = selected;
            await viewModel.SaveLocalDraftAsync(local with
            {
                Subject = "Second version",
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1)
            });
            Assert.Equal(0, provider.UpdateCount);

            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(
                () => !viewModel.IsSyncing && provider.UpdateCount == 1 &&
                      viewModel.Drafts.Single().Subject == "Second version",
                cancellationToken);
            Assert.NotNull(viewModel.SelectedMessage);
            Assert.Equal(selected.ProviderId, viewModel.SelectedMessage.ProviderId);

            var sender = new ComposeSender(account, mailbox);
            await viewModel.QueueSendAsync(
                sender,
                local.Id,
                new DraftMessage(
                    "Second version",
                    [new("Recipient", "recipient@example.com")],
                    "Body",
                    false));
            await WaitUntilAsync(() => !viewModel.IsSyncing && viewModel.Outbox.Count == 0, cancellationToken);
            Assert.Equal(1, provider.SendDraftCount);
            Assert.Equal(0, provider.SendCount);
            Assert.Empty(viewModel.Drafts);
            Assert.Empty(await store.GetLocalDraftsAsync(cancellationToken));

            var deleteDraft = local with
            {
                Id = "delete-once",
                Subject = "Delete me",
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(2)
            };
            await viewModel.SaveLocalDraftAsync(deleteDraft);
            await WaitUntilAsync(
                () => viewModel.SyncCommand.CanExecute(null),
                cancellationToken);
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(
                () => !viewModel.IsSyncing &&
                      viewModel.Drafts.SingleOrDefault()?.ProviderDraftId is not null,
                cancellationToken);
            await viewModel.DeleteLocalDraftAsync(deleteDraft.Id);
            await viewModel.DeleteLocalDraftAsync(deleteDraft.Id);
            Assert.Empty(viewModel.Drafts);
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);
            Assert.Equal(1, provider.DeleteCount);
            Assert.Empty(await store.GetLocalDraftsAsync(cancellationToken));

            var missingRemote = deleteDraft with
            {
                Id = "missing-remote",
                ProviderDraftId = "already-gone",
                SyncStatus = DraftSyncStatus.MissingRemote
            };
            await viewModel.SaveLocalDraftAsync(missingRemote);
            await viewModel.DeleteLocalDraftAsync(missingRemote.Id);
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);
            Assert.Equal(2, provider.DeleteCount);

            provider.DeleteAsMissing = true;
            var staleConflict = missingRemote with
            {
                Id = "stale-conflict",
                ProviderDraftId = "another-missing-remote",
                SyncStatus = DraftSyncStatus.Conflict
            };
            await viewModel.SaveLocalDraftAsync(staleConflict);
            await viewModel.DeleteLocalDraftAsync(staleConflict.Id);
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);
            Assert.Equal(3, provider.DeleteCount);
            Assert.Empty(await store.GetLocalDraftsAsync(cancellationToken));

            await viewModel.SaveLocalDraftAsync(local with
            {
                Id = "invalid-recipient",
                To = "bob",
                UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(3)
            });
            viewModel.SyncCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);
            Assert.True(string.IsNullOrWhiteSpace(viewModel.Error));
        }
        finally
        {
            await store.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AggregatesSavedAndDiscoveredPeopleWhileKeepingAccountFailuresLocal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-people-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var good = new MailAccount(
                "microsoft365", "good", "tenant", "good@example.com", "Good",
                ProviderCapabilities.Mail | ProviderCapabilities.Contacts);
            var bad = new MailAccount(
                "microsoft365", "bad", "tenant", "bad@example.com", "Bad",
                ProviderCapabilities.Mail | ProviderCapabilities.Contacts);
            var mailbox = new Mailbox(good.AccountId, good.EmailAddress, good.DisplayName);
            var badMailbox = new Mailbox(bad.AccountId, bad.EmailAddress, bad.DisplayName);
            await store.SaveAccountAsync(good, cancellationToken);
            await store.SaveAccountAsync(bad, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            await store.SaveMailboxAsync(badMailbox, cancellationToken);
            await store.SaveFoldersAsync(
                mailbox.Id,
                [new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 2, "inbox")],
                cancellationToken);
            await store.ApplySyncPageAsync(
                "people",
                new MailSyncPage(
                [
                    Message(mailbox.Id, "inbox", "Known", "Known") with
                    {
                        ProviderId = "known-message",
                        From = new MailAddress("Known Person", "known@example.com"),
                        IsRead = true
                    },
                    Message(mailbox.Id, "inbox", "New", "New") with
                    {
                        ProviderId = "new-message",
                        From = new MailAddress("New Person", "new@example.com"),
                        IsRead = true
                    }
                ],
                null,
                false),
                cancellationToken);

            var workspace = new AggregatedContactsProvider(good.AccountId, bad.AccountId);
            var viewModel = new MainWindowViewModel(
                store,
                directory,
                _ => { },
                _ => { },
                null,
                new RecordingProvider(),
                workspaceProvider: workspace);
            await viewModel.InitializeAsync();

            await ((AsyncCommand)viewModel.ShowContactsCommand).ExecuteAsync();
            await viewModel.PeopleBackgroundRefresh;
            Assert.Equal(2, viewModel.People.Count);
            Assert.True(viewModel.HasPeopleErrors);

            Assert.Equal(viewModel.People.OrderBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase), viewModel.People);
            var saved = Assert.Single(viewModel.People, static person => person.IsSaved);
            var discovered = Assert.Single(viewModel.People, static person => !person.IsSaved);
            Assert.Equal("K", saved.AvatarText);
            Assert.Equal("N", discovered.AvatarText);
            Assert.Equal("known@example.com", saved.EmailText);
            Assert.Equal("new@example.com", discovered.EmailText);
            Assert.Contains(good.EmailAddress, discovered.ProvenanceText);
            Assert.Contains(bad.EmailAddress, viewModel.PeopleErrorText);
            Assert.Contains("re-authenticate", viewModel.PeopleErrorText);
            Assert.False(viewModel.IsBusy);

            viewModel.EditContactCommand.Execute(saved);
            await WaitUntilAsync(() => viewModel.IsEditingContact, cancellationToken);
            viewModel.ContactName = "Known Updated";
            viewModel.ContactEmails = "known@example.com; second@example.com";
            await ((AsyncCommand)viewModel.SaveContactCommand).ExecuteAsync();
            Assert.True(workspace.Updated);
            Assert.Contains(viewModel.People, person => person.IsSaved && person.DisplayName == "Known Updated");

            var updated = Assert.Single(viewModel.People, static person => person.IsSaved);
            viewModel.RequestDeleteContactCommand.Execute(updated);
            await WaitUntilAsync(() => viewModel.IsConfirmingContactDelete, cancellationToken);
            Assert.True(viewModel.ConfirmDeleteContactCommand.CanExecute(null));
            await ((AsyncCommand)viewModel.ConfirmDeleteContactCommand).ExecuteAsync();
            Assert.True(workspace.Deleted);
            Assert.All(viewModel.People, person => Assert.False(person.IsSaved));

            Assert.True(viewModel.NewContactCommand.CanExecute(null));
            await ((AsyncCommand)viewModel.NewContactCommand).ExecuteAsync();
            Assert.True(viewModel.IsCreatingContact);
            viewModel.ContactName = "Created Person";
            viewModel.ContactEmails = "created@example.com";
            await ((AsyncCommand)viewModel.SaveContactCommand).ExecuteAsync();
            Assert.True(workspace.Created);
            Assert.Contains(viewModel.People, person => person.DisplayName == "Created Person");
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddsSharedMailboxToTheLiveFolderAndSettingsModels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-shared-live-{Guid.NewGuid():N}");
        var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName), cancellationToken);
            var provider = new RecordingProvider
            {
                FolderResults = [new MailFolder("account:team@example.com", "inbox", "Inbox", 1, 1, "inbox")],
                SyncRelease = new(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            await viewModel.InitializeAsync();

            var adding = viewModel.AddSharedMailboxAsync(account, "team@example.com", "Send As");
            await WaitUntilAsync(() => viewModel.FolderGroups.Any(group => group.Mailbox.Address == "team@example.com"), cancellationToken);
            provider.SyncRelease.SetResult();
            await adding;

            var shared = Assert.Single(viewModel.Mailboxes, mailbox => mailbox.Address == "team@example.com");
            Assert.True(shared.IsShared);
            Assert.True(shared.CanSendAs);
            var setting = Assert.Single(Assert.Single(viewModel.SettingsAccounts).SharedMailboxes);
            var permissionChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.SettingsAccounts))
                {
                    permissionChanged.TrySetResult();
                }
            };
            setting.SelectedPermission = "Send on behalf";
            await permissionChanged.Task.WaitAsync(cancellationToken);
            Assert.True(viewModel.Mailboxes.Single(mailbox => mailbox.Id == shared.Id).CanSendOnBehalf);
            Assert.False(viewModel.Mailboxes.Single(mailbox => mailbox.Id == shared.Id).CanSendAs);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
    private static MailMessage Message(string mailboxId, string folderId, string subject, string body) => new(
        mailboxId, subject, null, null, folderId, subject,
        new MailAddress("Sender", "sender@example.com"), [], DateTimeOffset.UtcNow,
        body, body, true, false, false, MailImportance.Normal, [], null);

    private static string Decode(Uri uri) => System.Text.Encoding.UTF8.GetString(
        Convert.FromBase64String(uri.OriginalString.Split(',')[1]));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50 && !condition(); attempt++)
        {
            await Task.Delay(20, cancellationToken);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task SendClosesComposerAndRetriesExplicitlyRejectedOutboxOnlyOnTheNextSync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-outbox-{Guid.NewGuid():N}");
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), key);
        try
        {
            await store.InitializeAsync(cancellationToken);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await store.SaveAccountAsync(account, cancellationToken);
            await store.SaveMailboxAsync(mailbox, cancellationToken);
            var provider = new RecordingProvider
            {
                SendRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                SyncRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                FolderResults = [new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox")]
            };
            var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
            viewModel.Accounts.Add(account);
            viewModel.Mailboxes.Add(mailbox);
            var composer = new ComposeWindowViewModel(
                [account], [mailbox], new ComposeRequest("to@example.com", "Queued subject", "Queued body"),
                viewModel.QueueSendAsync, viewModel.SaveLocalDraftAsync);
            composer.AddAttachment(new DraftAttachment("notes.txt", "text/plain", "content"u8.ToArray()));
            var closed = false;
            composer.Sent += (_, _) => closed = true;
            Assert.False(viewModel.HasOutbox);

            await ((AsyncCommand)composer.SendCommand).ExecuteAsync();
            Assert.True(closed);
            Assert.False(composer.IsSending);
            Assert.False(composer.HasError);
            Assert.Empty(viewModel.Drafts);
            Assert.Single(viewModel.Outbox);
            Assert.True(viewModel.HasOutbox);
            await WaitUntilAsync(() => provider.SendCalls == 1, cancellationToken);
            Assert.False(provider.SyncEntered.Task.IsCompleted);
            viewModel.ShowOutboxCommand.Execute(null);
            Assert.Equal("Busy", viewModel.CurrentFolderName);
            Assert.False(viewModel.OpenDraftCommand.CanExecute(Assert.Single(viewModel.VisibleDrafts)));

            // Send is attempted before any incoming sync, even when it is rejected.
            provider.SendRelease.SetException(new HttpRequestException("Throttled", null, System.Net.HttpStatusCode.TooManyRequests));
            await provider.SyncEntered.Task.WaitAsync(cancellationToken);
            provider.SyncRelease.SetResult();
            await WaitUntilAsync(() => !viewModel.IsSyncing, cancellationToken);
            Assert.Null(viewModel.Error);
            Assert.Single(viewModel.Outbox);
            Assert.Equal(1, provider.SendCalls);
            var queued = Assert.Single(await store.GetLocalDraftsAsync(cancellationToken));
            Assert.True(queued.IsQueued);
            Assert.Contains("Queued body", queued.Body);
            Assert.Equal("content"u8.ToArray(), Assert.Single(queued.Attachments).ContentBytes);
            await store.SaveLocalDraftAsync(queued with { Id = "accepted-before-restart", SendAccepted = true }, cancellationToken);

            // A later app session can deliver the persisted queue without reopening a composer.
            await using var reopened = new EncryptedMailStore(Path.Combine(directory, "mail.db"), key);
            await reopened.InitializeAsync(cancellationToken);
            provider.SendRelease = null;
            var restarted = new MainWindowViewModel(reopened, directory, _ => { }, _ => { }, null, provider);
            restarted.Accounts.Add(account);
            restarted.Mailboxes.Add(mailbox);
            await ((AsyncCommand)restarted.SyncCommand).ExecuteAsync();
            Assert.Equal(2, provider.SendCalls);
            Assert.Empty(await reopened.GetLocalDraftsAsync(cancellationToken));
            Assert.Empty(restarted.Outbox);
            Assert.False(restarted.HasOutbox);
            Assert.Null(restarted.Error);
        }
        finally
        {
            await store.DisposeAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeletingADraftClosesBeforeCloudDeletionAndRetriesAfterRestart()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-delete-draft-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "mail.db");
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var provider = new LifecycleDraftProvider { DeleteRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        try
        {
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await using (var store = new EncryptedMailStore(path, key))
            {
                await store.InitializeAsync(token);
                var remote = await provider.CreateDraftAsync(account, mailbox,
                    new("Delete me", [new("To", "to@example.com")], "Body", false), token);
                var draft = new LocalDraft("delete-draft", account.AccountId, mailbox.Id, "to@example.com", "", "",
                    "Delete me", "Body", [], DateTimeOffset.UtcNow, ProviderDraftId: remote.ProviderId);
                await store.SaveLocalDraftAsync(draft, token);
                var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
                viewModel.Accounts.Add(account);
                viewModel.Mailboxes.Add(mailbox);
                var composer = new ComposeWindowViewModel([account], [mailbox],
                    new ComposeRequest(draft.To, draft.Subject, draft.Body, DraftId: draft.Id),
                    viewModel.QueueSendAsync, viewModel.SaveLocalDraftAsync, viewModel.DeleteLocalDraftAsync);
                var closed = false;
                composer.Deleted += (_, _) => closed = true;
                await ((AsyncCommand)composer.DeleteCommand).ExecuteAsync();
                Assert.True(closed);
                Assert.False(composer.HasError);
                Assert.Empty(viewModel.Drafts);
                await WaitUntilAsync(() => provider.DeleteCount == 1, token);
                Assert.Single(viewModel.BusyActions);
                provider.DeleteRelease.SetException(new HttpRequestException("Offline"));
                await WaitUntilAsync(() => !viewModel.IsSyncing, token);
                Assert.Null(viewModel.Error);
                Assert.Equal("Retrying next sync", Assert.Single(viewModel.BusyActions).StatusText);
                Assert.Equal(1, provider.DeleteCount);
                await composer.FlushDraftAsync();
                Assert.Empty(await store.GetLocalDraftSummariesAsync(token));
            }
            await using (var store = new EncryptedMailStore(path, key))
            {
                await store.InitializeAsync(token);
                provider.DeleteRelease = null;
                var viewModel = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null, provider);
                viewModel.Accounts.Add(account);
                viewModel.Mailboxes.Add(mailbox);
                await ((AsyncCommand)viewModel.SyncCommand).ExecuteAsync();
                Assert.Equal(2, provider.DeleteCount);
                Assert.Empty(viewModel.BusyActions);
                Assert.Empty(await store.GetLocalDraftsAsync(token));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class RecordingProvider : IMailProvider, ISharedMailboxProvider
    {
        public bool MarkedRead { get; private set; }
        public List<string> MarkedReadIds { get; } = [];
        public bool? Flagged { get; private set; }
        public string? MoveDestination { get; private set; }
        public int GetMessageCalls { get; private set; }
        public int SearchCalls { get; private set; }
        public IReadOnlyList<MailMessage> SearchResults { get; set; } = [];
        public TaskCompletionSource? SearchRelease { get; set; }
        public TaskCompletionSource? MoveRelease { get; set; }
        public TaskCompletionSource? SyncRelease { get; set; }
        public TaskCompletionSource SyncEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? SendRelease { get; set; }
        public int SendCalls { get; private set; }
        public IReadOnlyList<MailFolder> FolderResults { get; set; } = [];
        public IReadOnlyList<MailAttachment> AttachmentResults { get; set; } = [];
        public MailAttachment? HydratedAttachmentResult { get; set; }
        public int GetAttachmentCalls { get; private set; }

        public Task<Mailbox> ValidateSharedMailboxAsync(
            MailAccount account,
            string address,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Mailbox(account.AccountId, address, address, IsShared: true));
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            Mailbox mailbox,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(FolderResults);

        public async Task<MailSyncPage> SyncFolderAsync(
            MailAccount account,
            Mailbox mailbox,
            string folderId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            SyncEntered.TrySetResult();
            if (SyncRelease is not null)
            {
                await SyncRelease.Task.WaitAsync(cancellationToken);
            }
            return new MailSyncPage([], cursor, false);
        }

        public Task MarkReadAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            bool isRead,
            CancellationToken cancellationToken = default)
        {
            MarkedRead = isRead;
            if (isRead)
            {
                MarkedReadIds.Add(messageId);
            }
            return Task.CompletedTask;
        }

        public Task<MailMessage> GetMessageAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            GetMessageCalls++;
            return Task.FromResult(Message(mailbox.Id, "inbox", "(no subject)", ""));
        }

        public async Task<IReadOnlyList<MailMessage>> SearchMessagesAsync(
            MailAccount account,
            Mailbox mailbox,
            string query,
            int limit = 250,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;
            if (SearchRelease is not null)
            {
                await SearchRelease.Task.WaitAsync(cancellationToken);
            }
            return SearchResults.Take(limit).ToArray();
        }

        public async Task MoveMessageAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            string destinationFolderId,
            CancellationToken cancellationToken = default)
        {
            MoveDestination = destinationFolderId;
            if (MoveRelease is not null)
            {
                await MoveRelease.Task.WaitAsync(cancellationToken);
            }
        }

        public Task SetFlaggedAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            bool isFlagged,
            CancellationToken cancellationToken = default)
        {
            Flagged = isFlagged;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AttachmentResults);

        public Task<MailAttachment?> GetAttachmentAsync(
            MailAccount account,
            Mailbox mailbox,
            string messageId,
            string attachmentId,
            CancellationToken cancellationToken = default)
        {
            GetAttachmentCalls++;
            return Task.FromResult(HydratedAttachmentResult ??
                AttachmentResults.FirstOrDefault(attachment => attachment.ProviderId == attachmentId));
        }

        public async Task SendAsync(
            MailAccount account,
            Mailbox mailbox,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            SendCalls++;
            if (SendRelease is not null)
            {
                await SendRelease.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class BlockingSyncProvider(MailFolder folder, params MailMessage[] messages) : IMailProvider
    {
        private int _concurrent;
        public List<int> SyncCountsAtSend { get; } = [];
        public int SyncCalls { get; private set; }
        public int MaxConcurrent { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<Mailbox> ValidateSharedMailboxAsync(
            MailAccount account,
            string address,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Mailbox(account.AccountId, address, address, IsShared: true));
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([folder]);

        public async Task<MailSyncPage> SyncFolderAsync(
            MailAccount account,
            Mailbox mailbox,
            string folderId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            var concurrent = Interlocked.Increment(ref _concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new MailSyncPage(messages, cursor, false);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public Task MarkReadAsync(MailAccount account, Mailbox mailbox, string messageId, bool isRead, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(messages[0]);
        public Task MoveMessageAsync(MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetFlaggedAsync(MailAccount account, Mailbox mailbox, string messageId, bool isFlagged, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);
        public Task SendAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default)
        {
            SyncCountsAtSend.Add(SyncCalls);
            return Task.CompletedTask;
        }
    }

    private sealed class ParallelMailboxProvider : IMailProvider
    {
        private int _concurrent;
        private int _enteredCount;
        public int EnteredCount => Volatile.Read(ref _enteredCount);
        public int MaxConcurrent { get; private set; }
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account,
            Mailbox mailbox,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>(mailbox.IsShared
                ? [new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox")]
                :
                [
                    new MailFolder(mailbox.Id, "broken", "Broken folder", 0, 1),
                    new MailFolder(mailbox.Id, "sentitems", "Sent Items", 0, 1, "sentitems")
                ]);

        public async Task<MailSyncPage> SyncFolderAsync(
            MailAccount account,
            Mailbox mailbox,
            string folderId,
            string? cursor,
            CancellationToken cancellationToken = default)
        {
            if (folderId == "broken")
            {
                throw new InvalidOperationException("Folder unavailable");
            }

            var concurrent = Interlocked.Increment(ref _concurrent);
            lock (this)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
            }
            Interlocked.Increment(ref _enteredCount);
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return new MailSyncPage(
                    [Message(mailbox.Id, folderId, $"{mailbox.DisplayName} message", "Body")],
                    cursor,
                    false);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public Task MarkReadAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isRead,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Message(mailbox.Id, "inbox", "Message", "Body"));
        public Task MoveMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFlaggedAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isFlagged,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);
        public Task SendAsync(
            MailAccount account, Mailbox mailbox, DraftMessage draft,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DiscoveryFailureProvider(MailMessage message) : IMailProvider
    {
        public int SyncCalls { get; private set; }

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<MailFolder>>(
                new HttpRequestException("Folder discovery unavailable"));

        public Task<MailSyncPage> SyncFolderAsync(
            MailAccount account, Mailbox mailbox, string folderId, string? cursor,
            CancellationToken cancellationToken = default)
        {
            SyncCalls++;
            return Task.FromResult(new MailSyncPage([message], cursor, false));
        }

        public Task MarkReadAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isRead,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) => Task.FromResult(message);
        public Task MoveMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFlaggedAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isFlagged,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);
        public Task SendAsync(
            MailAccount account, Mailbox mailbox, DraftMessage draft,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeWorkspaceProvider : IWorkspaceProvider
    {
        public int DownloadCount { get; private set; }

        public TaskCompletionSource? CalendarGate { get; init; }
        public int CalendarRequests { get; private set; }
        public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(
            MailAccount account, CancellationToken cancellationToken = default)
        {
            CalendarRequests++;
            if (CalendarGate is not null) await CalendarGate.Task.WaitAsync(cancellationToken);
            return [new("calendar", "Calendar", "#0F6CBD", true, account.AccountId)];
        }

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
            MailAccount account,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>(
                [new("event", "calendar", "Planning", from.AddHours(1), from.AddHours(2), "Room 1")]);

        public TaskCompletionSource? ContactGate { get; init; }
        public async Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default)
        {
            if (ContactGate is not null) await ContactGate.Task.WaitAsync(cancellationToken);
            return query.Contains("Planning", StringComparison.OrdinalIgnoreCase)
                ? [new("contact", "Planning Person", ["planning@example.com"], account.AccountId)] : [];
        }

        public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskInfo>>(
                [new("planning-task", "default", "Planning task", null, false, account.AccountId)]);

        public Task<IReadOnlyList<TaskListInfo>> GetTaskListsAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskListInfo>>(
                [new($"list-{account.AccountId}", "Tasks", account.AccountId, "defaultList")]);

        public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account,
            TaskListInfo list,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskInfo>>(
                [new($"task-{account.AccountId}", list.ProviderId, "Follow up", null, false, account.AccountId)]);

        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudFile>>(
                [new("planning-file", "Planning.docx", 1024, null, account.AccountId, account.ProviderId, "Documents")]);

        public Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(
            MailAccount account,
            CloudDriveItem? parent = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudDriveItem>>(
                parent is null
                    ?
                    [
                        new(
                            $"file-{account.AccountId}",
                            "plan.txt",
                            4,
                            false,
                            null,
                            null,
                            account.AccountId,
                            account.ProviderId,
                            "text/plain",
                            "Documents")
                    ]
                    : []);

        public async Task DownloadFileAsync(
            MailAccount account,
            CloudDriveItem file,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCount++;
            await destination.WriteAsync("plan"u8.ToArray(), cancellationToken);
        }

        public Task<IReadOnlyList<NoteNotebook>> GetNotebooksAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteNotebook>>(
                [new("planning-notebook", "Planning", account.AccountId, account.ProviderId)]);

        public Task<IReadOnlyList<NoteSection>> GetSectionsAsync(
            MailAccount account, NoteNotebook notebook, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteSection>>([]);

        public Task<IReadOnlyList<NoteInfo>> GetNotesAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteInfo>>(
                [new("planning-note", "Planning note", DateTimeOffset.UtcNow, null, account.AccountId, account.ProviderId)]);
    }

    private sealed class LifecycleDraftProvider : IMailProvider
    {
        private readonly List<CloudDraft> _drafts = [];
        private int _version;

        public bool SupportsCloudDrafts => true;
        public int UpdateCount { get; private set; }
        public int SendDraftCount { get; private set; }
        public int SendCount { get; private set; }
        public int DeleteCount { get; private set; }
        public bool DeleteAsMissing { get; set; }
        public TaskCompletionSource? DeleteRelease { get; set; }

        public Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(
            MailAccount account,
            Mailbox mailbox,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudDraft>>(_drafts.ToArray());

        public Task<CloudDraft> CreateDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            var created = Cloud(account, mailbox, $"server-{Guid.NewGuid():N}", draft);
            _drafts.Add(created);
            return Task.FromResult(created);
        }

        public Task<CloudDraft> UpdateDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            string draftId,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            var updated = Cloud(account, mailbox, draftId, draft);
            _drafts[_drafts.FindIndex(candidate => candidate.ProviderId == draftId)] = updated;
            return Task.FromResult(updated);
        }

        public async Task DeleteDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            string draftId,
            CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            if (DeleteRelease is not null) await DeleteRelease.Task.WaitAsync(cancellationToken);
            if (DeleteAsMissing)
            {
                throw new HttpRequestException(
                    "The specified object was not found in the store.",
                    null,
                    System.Net.HttpStatusCode.NotFound);
            }
            _drafts.RemoveAll(candidate => candidate.ProviderId == draftId);
        }

        public Task SendDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            string draftId,
            CancellationToken cancellationToken = default)
        {
            SendDraftCount++;
            _drafts.RemoveAll(candidate => candidate.ProviderId == draftId);
            return Task.CompletedTask;
        }

        public Task SendAsync(
            MailAccount account,
            Mailbox mailbox,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.CompletedTask;
        }

        private CloudDraft Cloud(
            MailAccount account,
            Mailbox mailbox,
            string id,
            DraftMessage message) =>
            new(
                id,
                account.AccountId,
                mailbox.Id,
                message,
                DateTimeOffset.UtcNow.AddMilliseconds(++_version),
                $"etag-{_version}");

        public Task<Mailbox> ValidateSharedMailboxAsync(
            MailAccount account,
            string address,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Mailbox(account.AccountId, address, address, IsShared: true));
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
            MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([]);
        public Task<MailSyncPage> SyncFolderAsync(
            MailAccount account, Mailbox mailbox, string folderId, string? cursor,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MailSyncPage([], null, false));
        public Task MarkReadAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isRead,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<MailMessage>(new NotSupportedException());
        public Task MoveMessageAsync(
            MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFlaggedAsync(
            MailAccount account, Mailbox mailbox, string messageId, bool isFlagged,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
            MailAccount account, Mailbox mailbox, string messageId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);
    }

    private sealed class AggregatedContactsProvider(string goodAccountId, string badAccountId) : IWorkspaceProvider
    {
        private ContactInfo? _contact = new(
            "saved-contact",
            "Known Person",
            ["known@example.com"],
            goodAccountId);

        public bool Updated { get; private set; }
        public bool Deleted { get; private set; }
        public bool Created { get; private set; }

        public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarInfo>>([]);

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
            MailAccount account,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);

        public Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default)
        {
            if (account.AccountId == badAccountId)
            {
                return Task.FromException<IReadOnlyList<ContactInfo>>(
                    new InvalidOperationException("Consent expired."));
            }
            IReadOnlyList<ContactInfo> contacts = _contact is null ? [] : [_contact];
            return Task.FromResult(contacts);
        }

        public Task<ContactInfo> UpdateContactAsync(
            MailAccount account,
            string contactId,
            ContactDraft draft,
            CancellationToken cancellationToken = default)
        {
            Updated = true;
            _contact = new ContactInfo(contactId, draft.DisplayName, draft.EmailAddresses, account.AccountId);
            return Task.FromResult(_contact);
        }

        public Task<ContactInfo> CreateContactAsync(
            MailAccount account,
            ContactDraft draft,
            CancellationToken cancellationToken = default)
        {
            Created = true;
            _contact = new ContactInfo("created-contact", draft.DisplayName, draft.EmailAddresses, account.AccountId);
            return Task.FromResult(_contact);
        }

        public Task DeleteContactAsync(
            MailAccount account,
            ContactInfo contact,
            CancellationToken cancellationToken = default)
        {
            Deleted = true;
            _contact = null;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TaskInfo>>([]);

        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(
            MailAccount account, string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudFile>>([]);

        public Task<IReadOnlyList<NoteInfo>> GetNotesAsync(
            MailAccount account, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoteInfo>>([]);
    }
}
