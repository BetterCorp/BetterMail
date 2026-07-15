namespace BetterMail.Core;

public interface IAccountProvider
{
    string ProviderId { get; }
    ProviderCapabilities Capabilities { get; }
    Task<MailAccount> SignInAsync(CancellationToken cancellationToken = default);
    Task SignOutAsync(string accountId, CancellationToken cancellationToken = default);
}

public interface IMailProvider
{
    bool SupportsCloudDrafts => false;

    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
        MailAccount account,
        Mailbox mailbox,
        CancellationToken cancellationToken = default);

    Task<MailSyncPage> SyncFolderAsync(
        MailAccount account,
        Mailbox mailbox,
        string folderId,
        string? cursor,
        CancellationToken cancellationToken = default);

    Task<MailSyncPage> SyncFolderAsync(
        MailAccount account,
        Mailbox mailbox,
        string folderId,
        string? cursor,
        DateTimeOffset? receivedSince,
        CancellationToken cancellationToken = default) =>
        SyncFolderAsync(account, mailbox, folderId, cursor, cancellationToken);

    Task MarkReadAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        bool isRead,
        CancellationToken cancellationToken = default);

    Task<MailMessage> GetMessageAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailHeader>> GetMessageHeadersAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<MailHeader>>(
            new NotSupportedException("This provider does not expose internet message headers."));

    Task MoveMessageAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        string destinationFolderId,
        CancellationToken cancellationToken = default);

    Task SetFlaggedAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        bool isFlagged,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default);

    Task SendAsync(
        MailAccount account,
        Mailbox mailbox,
        DraftMessage draft,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(
        MailAccount account,
        Mailbox mailbox,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<CloudDraft>>(
            new NotSupportedException("This provider does not support cloud drafts."));

    Task<CloudDraft> GetDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDraft>(
            new NotSupportedException("This provider does not support cloud drafts."));

    Task<CloudDraft> CreateDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        DraftMessage draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDraft>(
            new NotSupportedException("This provider does not support creating cloud drafts."));

    Task<CloudDraft> UpdateDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        DraftMessage draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDraft>(
            new NotSupportedException("This provider does not support updating cloud drafts."));

    Task DeleteDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new NotSupportedException("This provider does not support deleting cloud drafts."));

    Task SendDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new NotSupportedException("This provider does not support sending cloud drafts."));
}

public interface ISharedMailboxProvider
{
    Task<Mailbox> ValidateSharedMailboxAsync(
        MailAccount account,
        string address,
        CancellationToken cancellationToken = default);
}

public interface ICalendarProvider
{
    Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(MailAccount account, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(MailAccount account, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        MailAccount account,
        string calendarId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default) =>
        GetEventsAsync(account, from, to, cancellationToken);

    Task<CalendarEvent> CreateEventAsync(
        MailAccount account,
        CalendarEventDraft draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CalendarEvent>(new NotSupportedException("This provider does not support creating calendar events."));

    Task<CalendarEvent> UpdateEventAsync(
        MailAccount account,
        string eventId,
        CalendarEventDraft draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CalendarEvent>(new NotSupportedException("This provider does not support updating calendar events."));

    Task DeleteEventAsync(
        MailAccount account,
        string calendarId,
        string eventId,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This provider does not support deleting calendar events."));
}

public interface IContactsProvider
{
    Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(MailAccount account, string query, CancellationToken cancellationToken = default);

    Task<ContactInfo> CreateContactAsync(
        MailAccount account,
        ContactDraft draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ContactInfo>(new NotSupportedException("This provider does not support creating contacts."));

    Task<ContactInfo> UpdateContactAsync(
        MailAccount account,
        string contactId,
        ContactDraft draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ContactInfo>(new NotSupportedException("This provider does not support updating contacts."));

    Task DeleteContactAsync(
        MailAccount account,
        ContactInfo contact,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This provider does not support deleting contacts."));
}

public interface ITasksProvider
{
    Task<IReadOnlyList<TaskInfo>> GetTasksAsync(MailAccount account, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TaskListInfo>> GetTaskListsAsync(
        MailAccount account,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<TaskListInfo>>(
            new NotSupportedException("This provider does not support task-list selection."));

    Task<TaskListInfo> CreateTaskListAsync(
        MailAccount account,
        string displayName,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TaskListInfo>(
            new NotSupportedException("This provider does not support creating task lists."));

    Task<TaskListInfo> RenameTaskListAsync(
        MailAccount account,
        TaskListInfo list,
        string displayName,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TaskListInfo>(
            new NotSupportedException("This provider does not support renaming task lists."));

    Task DeleteTaskListAsync(
        MailAccount account,
        TaskListInfo list,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new NotSupportedException("This provider does not support deleting task lists."));

    Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
        MailAccount account,
        TaskListInfo list,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<TaskInfo>>(
            new NotSupportedException("This provider does not support task-list selection."));

    Task<TaskInfo> CreateTaskAsync(
        MailAccount account,
        TaskDraft draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TaskInfo>(new NotSupportedException("This provider does not support creating tasks."));

    Task<TaskInfo> UpdateTaskAsync(
        MailAccount account,
        string taskId,
        TaskDraft draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TaskInfo>(new NotSupportedException("This provider does not support updating tasks."));

    Task<TaskInfo> SetTaskCompletedAsync(
        MailAccount account,
        TaskListInfo list,
        string taskId,
        bool isCompleted,
        CancellationToken cancellationToken = default) =>
        Task.FromException<TaskInfo>(new NotSupportedException("This provider does not support completing tasks."));

    Task DeleteTaskAsync(
        MailAccount account,
        TaskListInfo list,
        string taskId,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This provider does not support deleting tasks."));
}

public interface IFilesProvider
{
    Task<IReadOnlyList<CloudFile>> SearchFilesAsync(MailAccount account, string query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(
        MailAccount account,
        CloudDriveItem? parent = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<CloudDriveItem>>(
            new NotSupportedException("This provider does not support folder navigation."));

    Task<CloudDriveItem> CreateFolderAsync(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDriveItem>(
            new NotSupportedException("This provider does not support creating folders."));

    Task<CloudDriveItem> UploadFileAsync(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        Stream content,
        long contentLength,
        string? contentType = null,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDriveItem>(
            new NotSupportedException("This provider does not support uploading files."));

    Task DownloadFileAsync(
        MailAccount account,
        CloudDriveItem file,
        Stream destination,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This provider does not support downloading files."));

    Task<CloudDriveItem> RenameDriveItemAsync(
        MailAccount account,
        CloudDriveItem item,
        string name,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CloudDriveItem>(
            new NotSupportedException("This provider does not support renaming drive items."));

    Task DeleteDriveItemAsync(
        MailAccount account,
        CloudDriveItem item,
        CancellationToken cancellationToken = default) =>
        Task.FromException(new NotSupportedException("This provider does not support deleting drive items."));
}

public interface INotesProvider
{
    Task<IReadOnlyList<NoteInfo>> GetNotesAsync(MailAccount account, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NoteNotebook>> GetNotebooksAsync(
        MailAccount account,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<NoteNotebook>>(
            new NotSupportedException("This provider does not support notebook navigation."));

    Task<IReadOnlyList<NoteSection>> GetSectionsAsync(
        MailAccount account,
        NoteNotebook notebook,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<NoteSection>>(
            new NotSupportedException("This provider does not support notebook sections."));

    Task<IReadOnlyList<NotePage>> GetPagesAsync(
        MailAccount account,
        NoteSection section,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<NotePage>>(
            new NotSupportedException("This provider does not support note pages."));

    Task<NotePageContent> GetPageContentAsync(
        MailAccount account,
        NotePage page,
        CancellationToken cancellationToken = default) =>
        Task.FromException<NotePageContent>(
            new NotSupportedException("This provider does not support reading note page content."));

    Task<NotePage> CreatePageAsync(
        MailAccount account,
        NotePageDraft draft,
        CancellationToken cancellationToken = default) =>
        Task.FromException<NotePage>(
            new NotSupportedException("This provider does not support creating note pages."));

    Task UpdatePageAsync(
        MailAccount account,
        NotePage page,
        IReadOnlyList<NotePagePatch> changes,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new NotSupportedException("This provider does not support updating note pages."));

    Task DeletePageAsync(
        MailAccount account,
        NotePage page,
        CancellationToken cancellationToken = default) =>
        Task.FromException(
            new NotSupportedException("This provider does not support deleting note pages."));
}

public interface IWorkspaceProvider :
    ICalendarProvider,
    IContactsProvider,
    ITasksProvider,
    IFilesProvider,
    INotesProvider;

public interface IMailStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAccountAsync(MailAccount account, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task DeleteAccountAsync(string providerId, string accountId, CancellationToken cancellationToken = default);
    Task SaveMailboxAsync(Mailbox mailbox, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Mailbox>> GetMailboxesAsync(CancellationToken cancellationToken = default);
    Task SaveFoldersAsync(string mailboxId, IReadOnlyList<MailFolder> folders, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailFolder>> GetFoldersAsync(string? mailboxId = null, CancellationToken cancellationToken = default);
    Task ApplySyncPageAsync(string cursorId, MailSyncPage page, CancellationToken cancellationToken = default);
    Task<string?> GetSyncCursorAsync(string cursorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MailMessage>> GetMessagesAsync(string? mailboxId = null, string? folderId = null, int limit = 5000, CancellationToken cancellationToken = default);
    async Task<MailStoreCounts> GetMessageCountsAsync(string? mailboxId = null, CancellationToken cancellationToken = default)
    {
        var messages = await GetMessagesAsync(mailboxId, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(messages.Count, messages.Count(static message => !message.IsRead), messages.Count(static message => message.IsFlagged));
    }
    async Task<IReadOnlyList<MailMessage>> GetThreadMessagesAsync(
        string threadId,
        CancellationToken cancellationToken = default) =>
        (await GetMessagesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(message => ConversationThread.ThreadIdentity(message) == threadId)
            .ToArray();
    Task<IReadOnlyList<MailMessage>> SearchAsync(string query, int limit = 5000, CancellationToken cancellationToken = default);
}

public interface IDraftStore
{
    Task SaveLocalDraftAsync(LocalDraft draft, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalDraft>> GetLocalDraftsAsync(CancellationToken cancellationToken = default);
    Task DeleteLocalDraftAsync(string id, CancellationToken cancellationToken = default);
    Task UpdateLocalDraftSyncMetadataAsync(
        string id,
        string providerDraftId,
        DateTimeOffset syncedLocalUpdatedAt,
        DateTimeOffset providerUpdatedAt,
        string? providerETag,
        CancellationToken cancellationToken = default);
}
