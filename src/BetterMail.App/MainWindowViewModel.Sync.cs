using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
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
        try { await SyncMailboxCoreAsync(provider, store, engine, account, mailbox, ownFailures); }
        catch (Exception error) { ownFailures.Enqueue($"{mailbox.Address}: {error.Message}"); }
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

        IsSyncing = true;
        Status = "Syncing mail...";
        Error = null;
        var animation = AnimateSyncIconAsync();
        var mailFailures = new ConcurrentQueue<string>();
        try
        {
            do
            {
                Interlocked.Exchange(ref _syncPending, 0);
                // User-requested work takes priority over background mailbox refreshes.
                // Repeat this for pending passes so sends queued during sync go first next time.
                await ProcessOutboxAsync();
                await ProcessMailActionsAsync();
                var engine = new SyncEngine(provider, store);
                var mailboxes = Mailboxes.ToArray();
                await Task.WhenAll(
                    from account in Accounts.ToArray()
                    join mailbox in mailboxes on account.AccountId equals mailbox.AccountId
                    select SyncMailboxAsync(provider, store, engine, account, mailbox, mailFailures));

                try
                {
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
                    mailFailures.Enqueue(exception.Message);
                }
            }
            while (Volatile.Read(ref _syncPending) != 0);

            await RefreshSyncHealthAsync();
            await ReconcileAllDraftsAsync();
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
            Interlocked.Exchange(ref _syncRunning, 0);
            IsSyncing = false;
            await animation;
            _ = RunStorageMaintenanceAsync();
            if (Volatile.Read(ref _syncPending) != 0)
            {
                _ = SyncAsync();
            }
        }
    }


    private async Task RunStorageMaintenanceAsync()
    {
        if (_store is null || Interlocked.CompareExchange(ref _storageMaintenanceRunning, 1, 0) != 0)
        {
            return;
        }
        try
        {
            while (await _store.RunMaintenanceBatchAsync())
            {
                await Task.Delay(50);
            }
            _recipientDirectoryTask = null;
            if (WindowsSessionLock.IsLocked())
            {
                using var vacuumCancellation = new CancellationTokenSource();
                var unlockWatcher = CancelVacuumWhenUnlockedAsync(vacuumCancellation);
                try
                {
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
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"Storage optimization paused: {exception.Message}";
        }
        finally
        {
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
            await RefreshNextCalendarEventAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _workspaceSyncRunning, 0);
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

    private async Task SyncMailboxCoreAsync(
        IMailProvider provider,
        EncryptedMailStore store,
        SyncEngine engine,
        MailAccount account,
        Mailbox mailbox,
        ConcurrentQueue<string> failures)
    {
        IReadOnlyList<MailFolder> folders;
        try
        {
            folders = await provider.GetFoldersAsync(account, mailbox);
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
                await engine.SyncFolderAsync(account, mailbox, folder, MailSyncHistoryDays);
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
