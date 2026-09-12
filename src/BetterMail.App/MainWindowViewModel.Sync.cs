using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    public ObservableCollection<SyncStep> SyncSteps { get; } = [];
    public SyncStep WorkspaceSyncStep { get; } = new("Workspace cache") { Detail = "Not started" };

    public SyncStep StorageSyncStep { get; } = new("Storage maintenance") { Detail = "Not started" };

    private async Task RunSyncStepAsync(SyncStep step, Func<Task> action)
    {
        step.Running = true;
        step.Progress = 0;
        step.Indeterminate = true;
        step.Detail = "In progress";
        try { await action(); step.Detail = "Complete"; step.Progress = 100; step.Indeterminate = false; }
        catch { step.Detail = "Failed"; throw; }
        finally { step.Running = false; }
    }

    public ObservableCollection<MailboxSyncHealth> SyncHealth { get; } = [];

    private async Task RefreshSyncHealthAsync()
    {
        if (_store is null) return;
        CollectionUpdates.Reconcile(SyncHealth,
            (await _store.GetSyncHealthAsync()).Where(item => Mailboxes.Any(mailbox => mailbox.Id == item.MailboxId)),
            static item => item.MailboxId);
    }

    private async Task SyncMailboxAsync(IMailProvider provider, EncryptedMailStore store, SyncEngine engine,
        MailAccount account, Mailbox mailbox, ConcurrentQueue<string> failures)
    {
        var ownFailures = new ConcurrentQueue<string>();
        var step = new SyncStep(mailbox.Address) { Running = true, Detail = "Discovering folders" };
        SyncSteps.Add(step);
        try { await SyncMailboxCoreAsync(provider, store, engine, account, mailbox, ownFailures, step); }
        catch (Exception error) { ownFailures.Enqueue($"{mailbox.Address}: {error.Message}"); }
        step.Running = false;
        step.Detail = ownFailures.IsEmpty ? "Complete" : "Completed with issues";
        foreach (var failure in ownFailures) failures.Enqueue(failure);
        var previous = (await store.GetSyncHealthAsync()).FirstOrDefault(item => item.MailboxId == mailbox.Id);
        await store.SaveSyncHealthAsync(new(account.AccountId, mailbox.Id, mailbox.Address,
            ownFailures.IsEmpty ? DateTimeOffset.UtcNow : previous?.LastSuccess,
            ownFailures.IsEmpty ? null : string.Join(Environment.NewLine, ownFailures)));
    }

    private async Task SyncAsync()
    {
        var provider = _provider;
        var store = _store;
        if (IsBusy || provider is null || store is null || Accounts.Count == 0)
        {
            return;
        }
        Interlocked.Exchange(ref _syncPending, 1);
        if (Interlocked.CompareExchange(ref _syncRunning, 1, 0) != 0)
        {
            return;
        }

        StopContactPhotoSync();
        IsSyncing = true;
        Status = "Syncing mail...";
        Error = null;
        var animation = AnimateSyncIconAsync();
        var mailFailures = new ConcurrentQueue<string>();
        SyncSteps.Clear();
        var sends = new SyncStep("Send queued mail");
        var actions = new SyncStep("Process Busy actions");
        var mail = new SyncStep("Sync mailboxes");
        var view = new SyncStep("Refresh mail view");
        var health = new SyncStep("Update sync health");
        var drafts = new SyncStep("Reconcile drafts");
        foreach (var step in new[] { sends, actions, mail, view, health, drafts }) SyncSteps.Add(step);
        try
        {
            do
            {
                Interlocked.Exchange(ref _syncPending, 0);
                // User-requested work takes priority over background mailbox refreshes.
                // Repeat this for pending passes so sends queued during sync go first next time.
                await RunSyncStepAsync(sends, ProcessOutboxAsync);
                if (BusyActions.Any(action => action.Kind == MailActionKind.Send && (action.NeedsSendReview || action.Error is not null)))
                    sends.Detail = "Needs attention — review Busy";
                await RunSyncStepAsync(actions, ProcessMailActionsAsync);
                if (BusyActions.Any(action => action.Kind != MailActionKind.Send && action.Error is not null))
                    actions.Detail = "Needs attention — review Busy";
                var engine = new SyncEngine(provider, store);
                var mailboxes = Mailboxes.ToArray();
                await RunSyncStepAsync(mail, async () =>
                {
                    var work = (from account in Accounts.ToArray()
                                join mailbox in mailboxes on account.AccountId equals mailbox.AccountId
                                select (account, mailbox)).ToArray();
                    var completed = 0;
                    mail.Indeterminate = false;
                    mail.Progress = 0;
                    await Task.WhenAll(work.Select(async item =>
                    {
                        await SyncMailboxAsync(provider, store, engine, item.account, item.mailbox, mailFailures);
                        mail.Progress = 100d * ++completed / work.Length;
                        mail.Detail = $"{completed} of {work.Length} mailboxes complete";
                    }));
                });

                if (!mailFailures.IsEmpty) mail.Detail = "Completed with issues";
                try
                {
                    view.Running = true;
                    view.Detail = "Updating cached rows";
                    await LoadFoldersAsync();
                    if (IsMailModule && !IsGlobalSearchOpen && !IsSearchResultsView)
                    {
                        await LoadMessagesAsync(showLoading: false);
                    }
                    if (IsMailModule && SelectedMessage is { } selected)
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
                    mailFailures.Enqueue(exception.Message);
                    view.Detail = "Failed: " + exception.Message;
                }
                finally { view.Running = false; }
            }
            while (Volatile.Read(ref _syncPending) != 0);
            view.Running = false;
            if (!view.Detail.StartsWith("Failed:", StringComparison.Ordinal)) view.Detail = "Complete";
            view.Progress = 100;
            view.Indeterminate = false;

            await RunSyncStepAsync(health, RefreshSyncHealthAsync);
            IReadOnlyList<string> draftIssues = [];
            await RunSyncStepAsync(drafts, async () => { draftIssues = await ReconcileAllDraftsAsync(); });
            if (draftIssues.Count > 0) drafts.Detail = $"{draftIssues.Count} issue(s) — review draft sync issues";
            _ = RefreshWorkspaceCacheAsync();
            if (!mailFailures.IsEmpty)
            {
                Error = string.Join(Environment.NewLine, mailFailures.Distinct(StringComparer.Ordinal));
                Status = "Sync completed with issues";
            }
            else
            {
                Status = BusyActions.Any(action => action.NeedsSendReview || action.Error is not null)
                    ? "Mail synced — review actions in Busy" : "Up to date";
            }
        }
        finally
        {
            foreach (var step in SyncSteps.Where(step => step.Running)) { step.Running = false; step.Detail = "Stopped"; }
            Interlocked.Exchange(ref _syncRunning, 0);
            IsSyncing = false;
            await animation;
            _ = RunStorageMaintenanceAsync();
            if (Volatile.Read(ref _syncPending) != 0)
            {
                _ = SyncAsync();
            }
            else StartContactPhotoSync();
        }
    }

    private CancellationTokenSource? _contactPhotoSyncCancellation;

    private void StopContactPhotoSync() => _contactPhotoSyncCancellation?.Cancel();

    private void StartContactPhotoSync()
    {
        if ((!ContactImagesEnabled && !MailSenderImagesEnabled) || _store is null || IsBusy ||
            Volatile.Read(ref _syncRunning) != 0 || Volatile.Read(ref _workspaceSyncRunning) != 0 ||
            _contactPhotoSyncCancellation is not null) return;
        var store = _store;
        var owners = ContactOwners.Select(owner => owner.CacheId).Distinct().ToArray();
        var source = _contactPhotoSyncCancellation = new CancellationTokenSource();
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await Task.Run(async () =>
                {
                    foreach (var owner in owners)
                    {
                        var contacts = await store.GetWorkspaceItemsAsync<ContactInfo>("contact", owner, "all", source.Token);
                        await BackgroundImages.PrefetchAsync(contacts.SelectMany(contact => contact.EmailAddresses)
                            .Where(email => !string.IsNullOrWhiteSpace(email)).Select(BackgroundImages.Contact), source.Token);
                    }
                }, source.Token);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
            catch (Exception) { /* Photo enrichment is optional and must not change mail sync status. */ }
            finally
            {
                if (ReferenceEquals(_contactPhotoSyncCancellation, source)) _contactPhotoSyncCancellation = null;
                source.Dispose();
            }
        }
    }

    private async Task RunStorageMaintenanceAsync()
    {
        if (_store is null || Interlocked.CompareExchange(ref _storageMaintenanceRunning, 1, 0) != 0)
        {
            return;
        }
        StorageSyncStep.Running = true;
        StorageSyncStep.Detail = "Cleaning cached data";
        try
        {
            var batches = 0;
            while (await _store.RunMaintenanceBatchAsync())
            {
                StorageSyncStep.Detail = $"{++batches} cleanup batches complete";
                await Task.Delay(50);
            }
            _recipientDirectoryTask = null;
            if (WindowsSessionLock.IsLocked())
            {
                using var vacuumCancellation = new CancellationTokenSource();
                var unlockWatcher = CancelVacuumWhenUnlockedAsync(vacuumCancellation);
                try
                {
                    StorageSyncStep.Detail = "Optimizing database while session is locked";
                    await _store.VacuumIfUsefulAsync(vacuumCancellation.Token);
                }
                catch (OperationCanceledException) when (!WindowsSessionLock.IsLocked())
                {
                    // The user returned; interactive database work takes priority.
                }
                finally
                {
                    vacuumCancellation.Cancel();
                    await unlockWatcher;
                }
            }
            StorageSyncStep.Detail = "Complete";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            StorageSyncStep.Detail = $"Paused: {exception.Message}";
            Error = $"Storage optimization paused: {exception.Message}";
        }
        finally
        {
            StorageSyncStep.Running = false;
            Interlocked.Exchange(ref _storageMaintenanceRunning, 0);
        }
    }

    private static async Task CancelVacuumWhenUnlockedAsync(CancellationTokenSource cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && WindowsSessionLock.IsLocked())
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
            }
            if (!cancellation.IsCancellationRequested)
            {
                cancellation.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
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
            await RefreshNextCalendarEventAsync();
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

        StopContactPhotoSync();
        _lastWorkspaceSyncAt = DateTimeOffset.UtcNow;
        var accounts = Accounts.ToArray();
        WorkspaceSyncStep.Running = true;
        var issues = new ConcurrentQueue<string>();
        IProgress<string> progress = new Progress<string>(detail => { if (WorkspaceSyncStep.Running) WorkspaceSyncStep.Detail = detail; });
        try
        {
            await Task.Run(async () =>
            {
                foreach (var account in accounts)
                {
                    progress.Report($"{account.EmailAddress} · People");
                    await RefreshContactsAsync(account);
                    progress.Report($"{account.EmailAddress} · Calendar");
                    await RefreshCalendarsAsync(account);
                    progress.Report($"{account.EmailAddress} · To Do");
                    await RefreshTasksAsync(account);
                    progress.Report($"{account.EmailAddress} · Notes");
                    await RefreshNotesAsync(account);
                    progress.Report($"{account.EmailAddress} · Cache cleanup");
                    await _store.GarbageCollectWorkspaceAsync(account.AccountId);
                }
            });
            await RefreshNextCalendarEventAsync();
            WorkspaceSyncStep.Detail = issues.IsEmpty ? "Complete" : "Completed with issues: " + string.Join("; ", issues);
        }
        catch (Exception error) { WorkspaceSyncStep.Detail = "Failed: " + error.Message; }
        finally
        {
            WorkspaceSyncStep.Running = false;
            Interlocked.Exchange(ref _workspaceSyncRunning, 0);
            StartContactPhotoSync();
        }

        async Task RefreshContactsAsync(MailAccount account)
        {
            if (!account.Capabilities.HasFlag(ProviderCapabilities.Contacts))
            {
                return;
            }
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
                issues.Enqueue($"{account.EmailAddress} · People: {WorkspaceErrors.Describe(exception)}");
            }
        }

        async Task RefreshCalendarsAsync(MailAccount account)
        {
            if (!account.Capabilities.HasFlag(ProviderCapabilities.Calendar))
            {
                return;
            }
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
                issues.Enqueue($"{account.EmailAddress} · Calendar: {WorkspaceErrors.Describe(exception)}");
            }
        }

        async Task RefreshTasksAsync(MailAccount account)
        {
            if (!account.Capabilities.HasFlag(ProviderCapabilities.Tasks))
            {
                return;
            }
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
                issues.Enqueue($"{account.EmailAddress} · To Do: {WorkspaceErrors.Describe(exception)}");
            }
        }

        async Task RefreshNotesAsync(MailAccount account)
        {
            if (!account.Capabilities.HasFlag(ProviderCapabilities.Notes))
            {
                return;
            }
            try
            {
                var notebooks = await _workspaceProvider.GetNotebooksAsync(account);
                await _store.ReplaceWorkspaceItemsAsync(
                    "note-notebook", account.AccountId, "all", notebooks,
                    static item => item.ProviderId,
                    static item => item.Name);
                var notes = new List<NoteInfo>();
                var complete = true;
                foreach (var notebook in notebooks)
                {
                    try
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
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        complete = false;
                        issues.Enqueue($"{account.EmailAddress} · Notes · {notebook.Name}: {WorkspaceErrors.Describe(exception)}");
                    }
                }
                if (complete) await _store.ReplaceWorkspaceItemsAsync(
                    "note", account.AccountId, "all", notes,
                    static item => item.ProviderId,
                    static item => item.Title);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                issues.Enqueue($"{account.EmailAddress} · Notes: {WorkspaceErrors.Describe(exception)}");
            }
        }
    }

    private async Task SyncMailboxCoreAsync(
        IMailProvider provider,
        EncryptedMailStore store,
        SyncEngine engine,
        MailAccount account,
        Mailbox mailbox,
        ConcurrentQueue<string> failures, SyncStep step)
    {
        IReadOnlyList<MailFolder> folders;
        try
        {
            folders = await Task.Run(() => provider.GetFoldersAsync(account, mailbox));
            await store.SaveFoldersAsync(mailbox.Id, folders);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Enqueue($"{mailbox.Address}: {exception.Message}");
            folders = await store.GetFoldersAsync(mailbox.Id);
            if (folders.Count == 0)
            {
                return;
            }
        }

        foreach (var folder in folders
                     .Where(static folder => folder.TotalCount > 0)
                     .OrderBy(static folder => folder.WellKnownName switch
                     {
                         "inbox" => 0,
                         "sentitems" => 1,
                         _ => 2
                     }))
        {
            try
            {
                step.Detail = folder.DisplayName;
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
                            await GetInboxSnapshotAsync(mailbox.Id, folder.ProviderId));
                    }
                }
                var historyDays = MailSyncHistoryDays;
                await Task.Run(() => engine.SyncFolderAsync(account, mailbox, folder, historyDays));
                if (notificationContext is not null)
                {
                    var synced = await GetInboxSnapshotAsync(mailbox.Id, folder.ProviderId);
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Enqueue($"{mailbox.Address} / {folder.DisplayName}: {exception.Message}");
            }
        }
        if (MailSyncHistoryDays > 0)
        {
            try
            {
                await store.PruneMessagesBeforeAsync(
                    mailbox.Id,
                    DateTimeOffset.UtcNow.AddDays(-MailSyncHistoryDays));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Enqueue($"{mailbox.Address}: {exception.Message}");
            }
        }
    }

    private async Task<IReadOnlyList<MailMessage>> GetInboxSnapshotAsync(string mailboxId, string folderId) =>
        (await _store!.GetMessagesPageAsync([new(mailboxId, folderId)], pageSize: 500)).Messages;

}
