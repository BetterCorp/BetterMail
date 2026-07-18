using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BetterMail.Core;

namespace BetterMail.Microsoft365;

public sealed class Microsoft365WorkspaceProvider(
    Microsoft365AuthService authentication,
    HttpClient? httpClient = null) :
    IWorkspaceProvider
{
    internal const int DriveUploadChunkSizeBytes = 10 * 1024 * 1024;
    internal const int DriveUploadSessionThresholdBytes = 10 * 1024 * 1024;
    private const string DriveItemSelect = "id,name,size,webUrl,parentReference,folder,file";
    private const string NotePageSelect = "id,title,lastModifiedDateTime,order,level,links";
    private const string EventSelect =
        "id,calendar,subject,start,end,location,attendees,isReminderOn,reminderMinutesBeforeStart,recurrence,showAs";
    private const string TaskSelect =
        "id,title,status,body,dueDateTime,importance,isReminderOn,reminderDateTime,recurrence,categories,createdDateTime,completedDateTime";
    private static readonly string[] CalendarScopes = ["Calendars.ReadWrite"];
    private static readonly string[] ContactScopes = ["Contacts.ReadWrite"];
    private static readonly string[] TaskScopes = ["Tasks.ReadWrite"];
    private static readonly string[] FileScopes = ["Files.ReadWrite"];
    private static readonly string[] NoteScopes = ["Notes.ReadWrite"];
    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
    };

    public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(
        MailAccount account, CancellationToken cancellationToken = default) =>
        GetPagedAsync(
            account,
            "me/calendars?$select=id,name,color,canEdit&$top=100",
            CalendarScopes,
            item => new CalendarInfo(
                RequiredString(item, "id"),
                RequiredString(item, "name"),
                OptionalString(item, "color"),
                item.TryGetProperty("canEdit", out var canEdit) && canEdit.GetBoolean(),
                account.AccountId),
            cancellationToken);

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        MailAccount account, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        return await GetPagedAsync(
            account,
            CalendarViewEndpoint(null, from, to),
            CalendarScopes,
            item => MapEvent(item, null, account.AccountId),
            cancellationToken);
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
        MailAccount account,
        string calendarId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        return await GetPagedAsync(
            account,
            CalendarViewEndpoint(calendarId, from, to),
            CalendarScopes,
            item => MapEvent(item, calendarId, account.AccountId),
            cancellationToken);
    }

    public async Task<CalendarEvent> CreateEventAsync(
        MailAccount account,
        CalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Post,
            CalendarEventsEndpoint(draft.CalendarId),
            BuildEventPayload(draft),
            CalendarScopes,
            cancellationToken);
        return MapEvent(document.RootElement, draft.CalendarId, account.AccountId);
    }

    public async Task<CalendarEvent> UpdateEventAsync(
        MailAccount account,
        string eventId,
        CalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Patch,
            $"{CalendarEventsEndpoint(draft.CalendarId)}/{Uri.EscapeDataString(eventId)}",
            BuildEventPayload(draft),
            CalendarScopes,
            cancellationToken);
        return MapEvent(document.RootElement, draft.CalendarId, account.AccountId);
    }

    public Task DeleteEventAsync(
        MailAccount account,
        string calendarId,
        string eventId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId) || string.IsNullOrWhiteSpace(eventId))
        {
            throw new ArgumentException("Calendar and event identifiers are required.");
        }

        return DeleteAsync(
            account,
            $"{CalendarEventsEndpoint(calendarId)}/{Uri.EscapeDataString(eventId)}",
            CalendarScopes,
            cancellationToken);
    }

    public async Task<IReadOnlyList<ContactInfo>> SearchContactsAsync(
        MailAccount account, string query, CancellationToken cancellationToken = default)
    {
        var contacts = await GetPagedAsync(
            account,
            "me/contacts?$select=id,displayName,emailAddresses&$top=250",
            ContactScopes,
            item => MapContact(item, account.AccountId),
            cancellationToken);
        return contacts
            .Where(contact => string.IsNullOrWhiteSpace(query) ||
                              contact.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                              contact.EmailAddresses.Any(address => address.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public async Task<ContactInfo> CreateContactAsync(
        MailAccount account,
        ContactDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Post,
            ContactEndpoint(account, draft.AccountId),
            BuildContactPayload(account, draft),
            ContactScopes,
            cancellationToken);
        return MapContact(document.RootElement, account.AccountId);
    }

    public async Task<ContactInfo> UpdateContactAsync(
        MailAccount account,
        string contactId,
        ContactDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Patch,
            ContactEndpoint(account, draft.AccountId, contactId),
            BuildContactPayload(account, draft),
            ContactScopes,
            cancellationToken);
        return MapContact(document.RootElement, account.AccountId);
    }

    public Task DeleteContactAsync(
        MailAccount account,
        ContactInfo contact,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            account,
            ContactEndpoint(account, contact.AccountId, contact.ProviderId),
            ContactScopes,
            cancellationToken);

    public async Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
        MailAccount account, CancellationToken cancellationToken = default)
    {
        var tasks = new List<TaskInfo>();
        foreach (var list in await GetTaskListsAsync(account, cancellationToken))
        {
            tasks.AddRange(await GetTasksAsync(account, list, cancellationToken));
        }
        return tasks.OrderBy(static task => task.IsComplete).ThenBy(static task => task.DueAt).ToArray();
    }

    public Task<IReadOnlyList<TaskListInfo>> GetTaskListsAsync(
        MailAccount account, CancellationToken cancellationToken = default) =>
        GetPagedAsync(
            account,
            "me/todo/lists?$select=id,displayName,wellknownListName,isOwner,isShared&$top=100",
            TaskScopes,
            item => MapTaskList(item, account.AccountId),
            cancellationToken);

    public async Task<TaskListInfo> CreateTaskListAsync(
        MailAccount account,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Post,
            "me/todo/lists",
            BuildTaskListPayload(displayName),
            TaskScopes,
            cancellationToken);
        return MapTaskList(document.RootElement, account.AccountId);
    }

    public async Task<TaskListInfo> RenameTaskListAsync(
        MailAccount account,
        TaskListInfo list,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        EnsureMutableTaskList(account, list);
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Patch,
            TaskListEndpoint(account, list.AccountId, list.ProviderId),
            BuildTaskListPayload(displayName),
            TaskScopes,
            cancellationToken);
        return MapTaskList(document.RootElement, account.AccountId);
    }

    public Task DeleteTaskListAsync(
        MailAccount account,
        TaskListInfo list,
        CancellationToken cancellationToken = default)
    {
        EnsureMutableTaskList(account, list);
        return DeleteAsync(
            account,
            TaskListEndpoint(account, list.AccountId, list.ProviderId),
            TaskScopes,
            cancellationToken);
    }

    public async Task<IReadOnlyList<TaskInfo>> GetTasksAsync(
        MailAccount account,
        TaskListInfo list,
        CancellationToken cancellationToken = default)
    {
        var tasks = await GetPagedAsync(
            account,
            $"{TaskEndpoint(account, list.AccountId, list.ProviderId)}?$select={TaskSelect}&$top=250",
            TaskScopes,
            item => MapTask(item, list.ProviderId, account.AccountId),
            cancellationToken);
        return tasks.OrderBy(static task => task.IsComplete)
            .ThenBy(static task => task.DueAt is null)
            .ThenBy(static task => task.DueAt)
            .ToArray();
    }

    public async Task<TaskInfo> CreateTaskAsync(
        MailAccount account,
        TaskDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Post,
            TaskEndpoint(account, draft.AccountId, draft.ListId),
            BuildTaskPayload(account, draft),
            TaskScopes,
            cancellationToken);
        return MapTask(document.RootElement, draft.ListId, account.AccountId);
    }

    public async Task<TaskInfo> UpdateTaskAsync(
        MailAccount account,
        string taskId,
        TaskDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Patch,
            TaskEndpoint(account, draft.AccountId, draft.ListId, taskId),
            BuildTaskPayload(account, draft),
            TaskScopes,
            cancellationToken);
        return MapTask(document.RootElement, draft.ListId, account.AccountId);
    }

    public async Task<TaskInfo> SetTaskCompletedAsync(
        MailAccount account,
        TaskListInfo list,
        string taskId,
        bool isCompleted,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Patch,
            TaskEndpoint(account, list.AccountId, list.ProviderId, taskId),
            new { status = isCompleted ? "completed" : "notStarted" },
            TaskScopes,
            cancellationToken);
        return MapTask(document.RootElement, list.ProviderId, account.AccountId);
    }

    public Task DeleteTaskAsync(
        MailAccount account,
        TaskListInfo list,
        string taskId,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            account,
            TaskEndpoint(account, list.AccountId, list.ProviderId, taskId),
            TaskScopes,
            cancellationToken);

    public async Task<IReadOnlyList<CloudFile>> SearchFilesAsync(
        MailAccount account, string query, CancellationToken cancellationToken = default)
    {
        var escaped = Uri.EscapeDataString(query.Replace("'", "''", StringComparison.Ordinal));
        string? endpoint = string.IsNullOrWhiteSpace(query)
            ? $"me/drive/root/children?$select={DriveItemSelect}&$top=250"
            : $"me/drive/root/search(q='{escaped}')?$select={DriveItemSelect}&$top=250";
        var files = new List<CloudFile>();
        while (!string.IsNullOrWhiteSpace(endpoint))
        {
            using var document = await GetJsonAsync(account, endpoint, FileScopes, cancellationToken);
            files.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(item =>
            {
                var driveItem = MapDriveItem(item, account);
                return new CloudFile(
                    driveItem.ProviderId,
                    driveItem.Name,
                    driveItem.Size,
                    driveItem.WebUrl,
                    driveItem.AccountId,
                    driveItem.AccountProviderId,
                    driveItem.ParentPath);
            }));
            endpoint = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }
        return files;
    }

    public async Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(
        MailAccount account,
        CloudDriveItem? parent = null,
        CancellationToken cancellationToken = default)
    {
        var items = new List<CloudDriveItem>();
        string? endpoint = $"{DriveChildrenEndpoint(account, parent)}?$select={DriveItemSelect}&$top=200";
        while (!string.IsNullOrWhiteSpace(endpoint))
        {
            using var document = await GetJsonAsync(account, endpoint, FileScopes, cancellationToken);
            items.AddRange(document.RootElement.GetProperty("value").EnumerateArray()
                .Select(item => MapDriveItem(item, account)));
            endpoint = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }
        return items.OrderByDescending(static item => item.IsFolder)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<CloudDriveItem> CreateFolderAsync(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateDriveName(name);
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Post,
            DriveChildrenEndpoint(account, parent),
            new Dictionary<string, object?>
            {
                ["name"] = name.Trim(),
                ["folder"] = new { },
                ["@microsoft.graph.conflictBehavior"] = "rename"
            },
            FileScopes,
            cancellationToken);
        return MapDriveItem(document.RootElement, account);
    }

    public Task<CloudDriveItem> UploadFileAsync(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        Stream content,
        long contentLength,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        ValidateUpload(account, parent, name, content, contentLength);
        return RequiresUploadSession(contentLength)
            ? UploadLargeFileAsync(account, parent, name.Trim(), content, contentLength, cancellationToken)
            : UploadSmallFileAsync(account, parent, name.Trim(), content, contentLength, contentType, cancellationToken);
    }

    internal static bool RequiresUploadSession(long contentLength) =>
        contentLength > DriveUploadSessionThresholdBytes;

    public async Task DownloadFileAsync(
        MailAccount account,
        CloudDriveItem file,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        EnsureDriveOwned(account, file);
        if (file.IsFolder)
        {
            throw new ArgumentException("Folders cannot be downloaded as files.", nameof(file));
        }
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The download destination must be writable.", nameof(destination));
        }

        using var request = await CreateRequestAsync(
            account,
            HttpMethod.Get,
            $"{DriveItemEndpoint(account, file)}/content",
            FileScopes,
            cancellationToken);
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(destination, cancellationToken);
    }

    public async Task<CloudDriveItem> RenameDriveItemAsync(
        MailAccount account,
        CloudDriveItem item,
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateDriveName(name);
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Patch,
            DriveItemEndpoint(account, item),
            new { name = name.Trim() },
            FileScopes,
            cancellationToken);
        return MapDriveItem(document.RootElement, account);
    }

    public Task DeleteDriveItemAsync(
        MailAccount account,
        CloudDriveItem item,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            account,
            DriveItemEndpoint(account, item),
            FileScopes,
            cancellationToken);

    public async Task<IReadOnlyList<NoteInfo>> GetNotesAsync(
        MailAccount account, CancellationToken cancellationToken = default)
    {
        var notes = new List<NoteInfo>();
        foreach (var notebook in await GetNotebooksAsync(account, cancellationToken))
        {
            foreach (var section in await GetSectionsAsync(account, notebook, cancellationToken))
            {
                notes.AddRange((await GetPagesAsync(account, section, cancellationToken)).Select(page =>
                    new NoteInfo(
                        page.ProviderId,
                        page.Title,
                        page.ModifiedAt,
                        page.WebUrl,
                        page.AccountId,
                        page.AccountProviderId,
                        page.SectionProviderId)));
            }
        }
        return notes.OrderByDescending(note => note.ModifiedAt).ToArray();
    }

    public Task<IReadOnlyList<NoteNotebook>> GetNotebooksAsync(
        MailAccount account, CancellationToken cancellationToken = default) =>
        GetPagedAsync(
            account,
            "me/onenote/notebooks?$select=id,displayName,links&$top=100",
            NoteScopes,
            item => MapNotebook(item, account),
            cancellationToken);

    public Task<IReadOnlyList<NoteSection>> GetSectionsAsync(
        MailAccount account,
        NoteNotebook notebook,
        CancellationToken cancellationToken = default) =>
        GetPagedAsync(
            account,
            $"{NotebookSectionsEndpoint(account, notebook)}?$select=id,displayName,links&$top=100",
            NoteScopes,
            item => MapSection(item, notebook.ProviderId, account),
            cancellationToken);

    public Task<IReadOnlyList<NotePage>> GetPagesAsync(
        MailAccount account,
        NoteSection section,
        CancellationToken cancellationToken = default) =>
        GetPagedAsync(
            account,
            $"{SectionPagesEndpoint(account, section)}?$select={NotePageSelect}&$orderby=order&$top=100&pagelevel=true",
            NoteScopes,
            item => MapPage(item, section.ProviderId, account),
            cancellationToken);

    public async Task<NotePageContent> GetPageContentAsync(
        MailAccount account,
        NotePage page,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            account,
            HttpMethod.Get,
            $"{PageEndpoint(account, page)}/content?includeIDs=true",
            NoteScopes,
            cancellationToken);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return new NotePageContent(
            page.ProviderId,
            page.SectionProviderId,
            page.AccountId,
            page.AccountProviderId,
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    public async Task<NotePage> CreatePageAsync(
        MailAccount account,
        NotePageDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var request = await CreateRequestAsync(
            account,
            HttpMethod.Post,
            SectionPagesEndpoint(account, draft),
            NoteScopes,
            cancellationToken);
        request.Content = new StringContent(
            BuildNotePageHtml(account, draft), Encoding.UTF8, "text/html");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken);
        return MapPage(document.RootElement, draft.SectionProviderId, account);
    }

    public Task UpdatePageAsync(
        MailAccount account,
        NotePage page,
        IReadOnlyList<NotePagePatch> changes,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(
            account,
            HttpMethod.Patch,
            $"{PageEndpoint(account, page)}/content",
            BuildNotePatchPayload(account, page, changes),
            NoteScopes,
            cancellationToken);

    public Task DeletePageAsync(
        MailAccount account,
        NotePage page,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(account, PageEndpoint(account, page), NoteScopes, cancellationToken);

    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(
        MailAccount account,
        string endpoint,
        IEnumerable<string> scopes,
        Func<JsonElement, T> map,
        CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? next = endpoint;
        while (next is not null)
        {
            using var document = await GetJsonAsync(account, next, scopes, cancellationToken);
            items.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(map));
            next = NextPageEndpoint(document.RootElement);
        }
        return items;
    }

    internal static string? NextPageEndpoint(JsonElement response) =>
        OptionalString(response, "@odata.nextLink");

    private async Task<JsonDocument> GetJsonAsync(
        MailAccount account,
        string endpoint,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        using var response = await Microsoft365RequestScheduler.Shared.SendAsync(
            account,
            endpoint,
            async (_, token) =>
            {
                using var request = await CreateRequestAsync(account, HttpMethod.Get, endpoint, scopes, token);
                return await _httpClient.SendAsync(request, token);
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<JsonDocument> SendJsonForResponseAsync(
        MailAccount account,
        HttpMethod method,
        string endpoint,
        object body,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        using var response = await Microsoft365RequestScheduler.Shared.SendAsync(
            account,
            endpoint,
            async (_, token) =>
            {
                using var request = await CreateRequestAsync(account, method, endpoint, scopes, token);
                request.Content = JsonContent.Create(body);
                return await _httpClient.SendAsync(request, token);
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task SendJsonAsync(
        MailAccount account,
        HttpMethod method,
        string endpoint,
        object body,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        using var response = await Microsoft365RequestScheduler.Shared.SendAsync(
            account,
            endpoint,
            async (_, token) =>
            {
                using var request = await CreateRequestAsync(account, method, endpoint, scopes, token);
                request.Content = JsonContent.Create(body);
                return await _httpClient.SendAsync(request, token);
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task DeleteAsync(
        MailAccount account,
        string endpoint,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        using var response = await Microsoft365RequestScheduler.Shared.SendAsync(
            account,
            endpoint,
            async (_, token) =>
            {
                using var request = await CreateRequestAsync(account, HttpMethod.Delete, endpoint, scopes, token);
                return await _httpClient.SendAsync(request, token);
            },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        MailAccount account,
        HttpMethod method,
        string endpoint,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await authentication.GetAccessTokenAsync(account.AccountId, scopes, cancellationToken));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Prefer", string.Concat("outlook.timezone=", (char)34, "UTC", (char)34));
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                await ReadErrorAsync(response, cancellationToken), null, response.StatusCode);
        }
    }

    internal static string ContactEndpoint(
        MailAccount account, string? ownerAccountId, string? contactId = null)
    {
        EnsureOwnedBy(account, ownerAccountId);
        if (contactId is not null && string.IsNullOrWhiteSpace(contactId))
        {
            throw new ArgumentException("A contact identifier is required.", nameof(contactId));
        }

        return contactId is null
            ? "me/contacts"
            : $"me/contacts/{Uri.EscapeDataString(contactId)}";
    }

    internal static object BuildContactPayload(MailAccount account, ContactDraft draft)
    {
        _ = ContactEndpoint(account, draft.AccountId);
        if (string.IsNullOrWhiteSpace(draft.DisplayName) && draft.EmailAddresses.Count == 0)
        {
            throw new ArgumentException("A contact needs a name or email address.", nameof(draft));
        }
        if (draft.EmailAddresses.Any(address => !System.Net.Mail.MailAddress.TryCreate(address, out _)))
        {
            throw new ArgumentException("Every contact email address must be valid.", nameof(draft));
        }

        return new
        {
            displayName = draft.DisplayName.Trim(),
            emailAddresses = draft.EmailAddresses
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(address => new { name = draft.DisplayName.Trim(), address })
        };
    }

    internal static ContactInfo MapContact(JsonElement item, string accountId) => new(
        RequiredString(item, "id"),
        OptionalString(item, "displayName") ?? "(no name)",
        item.TryGetProperty("emailAddresses", out var addresses)
            ? addresses.EnumerateArray().Select(address => OptionalString(address, "address") ?? "")
                .Where(static address => address.Length > 0).ToArray()
            : [],
        accountId);

    internal static string TaskEndpoint(
        MailAccount account,
        string? ownerAccountId,
        string listId,
        string? taskId = null)
    {
        var listEndpoint = TaskListEndpoint(account, ownerAccountId, listId);
        if (taskId is not null && string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("A task identifier is required.", nameof(taskId));
        }

        var endpoint = $"{listEndpoint}/tasks";
        return taskId is null
            ? endpoint
            : $"{endpoint}/{Uri.EscapeDataString(taskId)}";
    }

    internal static string TaskListEndpoint(
        MailAccount account,
        string? ownerAccountId,
        string listId)
    {
        EnsureOwnedBy(account, ownerAccountId);
        if (string.IsNullOrWhiteSpace(listId))
        {
            throw new ArgumentException("A task list must be selected.", nameof(listId));
        }
        return $"me/todo/lists/{Uri.EscapeDataString(listId)}";
    }

    internal static object BuildTaskListPayload(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("A task list name is required.", nameof(displayName));
        }
        return new { displayName = displayName.Trim() };
    }

    internal static void EnsureMutableTaskList(MailAccount account, TaskListInfo list)
    {
        _ = TaskListEndpoint(account, list.AccountId, list.ProviderId);
        if (!list.IsOwner)
        {
            throw new InvalidOperationException("Only the task-list owner can rename or delete this list.");
        }
        if (!string.IsNullOrWhiteSpace(list.WellKnownName) &&
            !string.Equals(list.WellKnownName, "none", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Built-in Microsoft To Do lists cannot be renamed or deleted.");
        }
    }

    internal static object BuildTaskPayload(MailAccount account, TaskDraft draft)
    {
        _ = TaskEndpoint(account, draft.AccountId, draft.ListId);
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new ArgumentException("A task title is required.", nameof(draft));
        }

        var payload = new Dictionary<string, object?>
        {
            ["title"] = draft.Title.Trim(),
            ["dueDateTime"] = draft.DueAt is null ? null : GraphDate(draft.DueAt.Value)
        };
        if (draft.Notes is not null)
        {
            payload["body"] = new
            {
                content = draft.Notes,
                contentType = EnumValue(draft.NotesContentType)
            };
        }
        if (draft.Importance is { } importance)
        {
            payload["importance"] = EnumValue(importance);
        }
        if (draft.ReminderAt is { } reminderAt)
        {
            if (draft.IsReminderOn == false)
            {
                throw new ArgumentException(
                    "A task cannot have a reminder time while reminders are disabled.", nameof(draft));
            }
            payload["isReminderOn"] = draft.IsReminderOn ?? true;
            payload["reminderDateTime"] = GraphDate(reminderAt);
        }
        else if (draft.IsReminderOn == true)
        {
            throw new ArgumentException("An enabled task reminder needs a date and time.", nameof(draft));
        }
        else if (draft.IsReminderOn == false)
        {
            payload["isReminderOn"] = false;
        }
        if (draft.Recurrence is { } recurrence)
        {
            if (draft.ClearRecurrence)
            {
                throw new ArgumentException(
                    "A task recurrence cannot be supplied and cleared together.", nameof(draft));
            }
            ValidateRecurrence(recurrence);
            payload["recurrence"] = BuildRecurrence(recurrence);
        }
        else if (draft.ClearRecurrence)
        {
            payload["recurrence"] = null;
        }
        if (draft.Categories is not null)
        {
            if (draft.Categories.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Task categories cannot be empty.", nameof(draft));
            }
            payload["categories"] = draft.Categories
                .Select(static category => category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        if (draft.Status is { } status)
        {
            payload["status"] = EnumValue(status);
        }
        if (draft.CreatedAt is { } createdAt)
        {
            payload["createdDateTime"] = createdAt.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
        }
        if (draft.CompletedAt is { } completedAt)
        {
            if (draft.Status is not null and not TodoTaskStatus.Completed)
            {
                throw new ArgumentException(
                    "A completed timestamp cannot be paired with a non-completed task status.",
                    nameof(draft));
            }
            payload["completedDateTime"] = GraphDate(completedAt);
        }
        return payload;
    }

    internal static TaskInfo MapTask(JsonElement item, string listId, string accountId)
    {
        var status = ParseTaskStatus(OptionalString(item, "status"));
        var body = item.TryGetProperty("body", out var bodyElement) &&
                   bodyElement.ValueKind == JsonValueKind.Object
            ? bodyElement
            : default;
        return new TaskInfo(
            RequiredString(item, "id"),
            listId,
            OptionalString(item, "title") ?? "(no title)",
            item.TryGetProperty("dueDateTime", out var due) && due.ValueKind == JsonValueKind.Object
                ? ReadGraphDate(due)
                : null,
            status == TodoTaskStatus.Completed,
            accountId,
            body.ValueKind == JsonValueKind.Object ? OptionalString(body, "content") : null,
            body.ValueKind == JsonValueKind.Object &&
            string.Equals(OptionalString(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase)
                ? TaskBodyContentType.Html
                : TaskBodyContentType.Text,
            ParseTaskImportance(OptionalString(item, "importance")),
            item.TryGetProperty("isReminderOn", out var reminderOn) && reminderOn.GetBoolean(),
            item.TryGetProperty("reminderDateTime", out var reminder) &&
            reminder.ValueKind == JsonValueKind.Object
                ? ReadGraphDate(reminder)
                : null,
            item.TryGetProperty("recurrence", out var recurrence) &&
            recurrence.ValueKind == JsonValueKind.Object
                ? ParseRecurrence(recurrence)
                : null,
            item.TryGetProperty("categories", out var categories)
                ? categories.EnumerateArray()
                    .Select(static category => category.GetString() ?? "")
                    .Where(static category => category.Length > 0)
                    .ToArray()
                : [],
            ParseTimestamp(OptionalString(item, "createdDateTime")),
            item.TryGetProperty("completedDateTime", out var completed) &&
            completed.ValueKind == JsonValueKind.Object
                ? ReadGraphDate(completed)
                : null,
            status);
    }

    internal static TaskListInfo MapTaskList(JsonElement item, string accountId) => new(
        RequiredString(item, "id"),
        OptionalString(item, "displayName") ?? "(unnamed list)",
        accountId,
        OptionalString(item, "wellknownListName"),
        !item.TryGetProperty("isOwner", out var isOwner) || isOwner.GetBoolean(),
        item.TryGetProperty("isShared", out var isShared) && isShared.GetBoolean());

    private static TaskImportance ParseTaskImportance(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => TaskImportance.Low,
        "high" => TaskImportance.High,
        _ => TaskImportance.Normal
    };

    private static TodoTaskStatus ParseTaskStatus(string? value) => value?.ToLowerInvariant() switch
    {
        "inprogress" => TodoTaskStatus.InProgress,
        "completed" => TodoTaskStatus.Completed,
        "waitingonothers" => TodoTaskStatus.WaitingOnOthers,
        "deferred" => TodoTaskStatus.Deferred,
        _ => TodoTaskStatus.NotStarted
    };

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var timestamp)
                ? timestamp
                : null;

    private static void EnsureOwnedBy(MailAccount account, string? ownerAccountId)
    {
        if (string.IsNullOrWhiteSpace(ownerAccountId) || ownerAccountId != account.AccountId)
        {
            throw new InvalidOperationException("The selected item does not belong to this account.");
        }
    }

    internal static string NotebookSectionsEndpoint(
        MailAccount account, NoteNotebook notebook)
    {
        EnsureNoteOwned(account, notebook.AccountId, notebook.AccountProviderId);
        return $"me/onenote/notebooks/{RequiredNoteId(notebook.ProviderId)}/sections";
    }

    internal static string SectionPagesEndpoint(
        MailAccount account, NoteSection section)
    {
        EnsureNoteOwned(account, section.AccountId, section.AccountProviderId);
        return $"me/onenote/sections/{RequiredNoteId(section.ProviderId)}/pages";
    }

    internal static string SectionPagesEndpoint(
        MailAccount account, NotePageDraft draft)
    {
        EnsureNoteOwned(account, draft.AccountId, draft.AccountProviderId);
        return $"me/onenote/sections/{RequiredNoteId(draft.SectionProviderId)}/pages";
    }

    internal static string PageEndpoint(MailAccount account, NotePage page)
    {
        EnsureNoteOwned(account, page.AccountId, page.AccountProviderId);
        return $"me/onenote/pages/{RequiredNoteId(page.ProviderId)}";
    }

    internal static NoteNotebook MapNotebook(JsonElement item, MailAccount account) => new(
        RequiredString(item, "id"),
        OptionalString(item, "displayName") ?? "(untitled notebook)",
        account.AccountId,
        account.ProviderId,
        ReadOneNoteWebUrl(item));

    internal static NoteSection MapSection(
        JsonElement item, string notebookProviderId, MailAccount account) => new(
        RequiredString(item, "id"),
        notebookProviderId,
        OptionalString(item, "displayName") ?? "(untitled section)",
        account.AccountId,
        account.ProviderId,
        ReadOneNoteWebUrl(item));

    internal static NotePage MapPage(
        JsonElement item, string sectionProviderId, MailAccount account) => new(
        RequiredString(item, "id"),
        sectionProviderId,
        OptionalString(item, "title") ?? "(untitled)",
        DateTimeOffset.TryParse(
            OptionalString(item, "lastModifiedDateTime"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var modified)
                ? modified
                : DateTimeOffset.MinValue,
        item.TryGetProperty("order", out var order) ? order.GetInt32() : 0,
        item.TryGetProperty("level", out var level) ? level.GetInt32() : 0,
        account.AccountId,
        account.ProviderId,
        ReadOneNoteWebUrl(item));

    internal static string BuildNotePageHtml(MailAccount account, NotePageDraft draft)
    {
        _ = SectionPagesEndpoint(account, draft);
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            throw new ArgumentException("A note page title is required.", nameof(draft));
        }
        if (draft.HtmlBody is null)
        {
            throw new ArgumentException("Note page content is required.", nameof(draft));
        }

        return "<!DOCTYPE html><html><head><title>" +
               WebUtility.HtmlEncode(draft.Title.Trim()) +
               "</title></head><body>" +
               draft.HtmlBody +
               "</body></html>";
    }

    internal static IReadOnlyList<IReadOnlyDictionary<string, object>> BuildNotePatchPayload(
        MailAccount account,
        NotePage page,
        IReadOnlyList<NotePagePatch> changes)
    {
        _ = PageEndpoint(account, page);
        if (changes is null || changes.Count == 0)
        {
            throw new ArgumentException("At least one note page change is required.", nameof(changes));
        }

        return changes.Select(change =>
        {
            ValidateNotePatch(change);
            var command = new Dictionary<string, object>
            {
                ["target"] = change.Target,
                ["action"] = EnumValue(change.Action)
            };
            if (change.HtmlContent is not null)
            {
                command["content"] = change.HtmlContent;
            }
            if (change.Position is { } position)
            {
                command["position"] = EnumValue(position);
            }
            return (IReadOnlyDictionary<string, object>)command;
        }).ToArray();
    }

    private static void ValidateNotePatch(NotePagePatch change)
    {
        if (!IsValidNoteTarget(change.Target))
        {
            throw new ArgumentException(
                "A note target must be 'body', 'title', a '#data-id', or a generated element ID.",
                nameof(change));
        }
        if (change.HtmlContent is null)
        {
            throw new ArgumentException("This note change requires HTML content.", nameof(change));
        }
        if (change.Action is not (NotePatchAction.Insert or NotePatchAction.Append) &&
            change.Position is not null)
        {
            throw new ArgumentException(
                "Only inserted or appended note content accepts a position.", nameof(change));
        }
    }

    private static bool IsValidNoteTarget(string? target)
    {
        if (target is "body" or "title")
        {
            return true;
        }
        return target is { Length: > 1 } &&
               target.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character));
    }

    private static string RequiredNoteId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            throw new ArgumentException("A OneNote item identifier is required.", nameof(providerId));
        }
        return Uri.EscapeDataString(providerId);
    }

    private static void EnsureNoteOwned(
        MailAccount account, string? accountId, string? accountProviderId)
    {
        if (accountId != account.AccountId || accountProviderId != account.ProviderId)
        {
            throw new InvalidOperationException(
                "The selected OneNote item does not belong to this account and provider.");
        }
    }

    private static Uri? ReadOneNoteWebUrl(JsonElement item)
    {
        if (item.TryGetProperty("links", out var links) &&
            links.ValueKind == JsonValueKind.Object &&
            links.TryGetProperty("oneNoteWebUrl", out var link) &&
            link.ValueKind == JsonValueKind.Object &&
            Uri.TryCreate(OptionalString(link, "href"), UriKind.Absolute, out var webUrl))
        {
            return webUrl;
        }
        return null;
    }

    internal static string DriveChildrenEndpoint(MailAccount account, CloudDriveItem? parent)
    {
        if (parent is null)
        {
            return "me/drive/root/children";
        }

        EnsureDriveOwned(account, parent);
        if (!parent.IsFolder)
        {
            throw new ArgumentException("Only folders can contain drive items.", nameof(parent));
        }
        return $"{DriveItemEndpoint(account, parent)}/children";
    }

    internal static string DriveItemEndpoint(MailAccount account, CloudDriveItem item)
    {
        EnsureDriveOwned(account, item);
        if (string.IsNullOrWhiteSpace(item.ProviderId))
        {
            throw new ArgumentException("A drive item identifier is required.", nameof(item));
        }
        return $"me/drive/items/{Uri.EscapeDataString(item.ProviderId)}";
    }

    internal static string DriveUploadEndpoint(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        bool createSession)
    {
        ValidateDriveName(name);
        if (parent is not null)
        {
            _ = DriveChildrenEndpoint(account, parent);
        }
        var parentPath = parent is null
            ? "me/drive/root"
            : $"me/drive/items/{Uri.EscapeDataString(parent.ProviderId)}";
        return $"{parentPath}:/{Uri.EscapeDataString(name.Trim())}:/{(createSession ? "createUploadSession" : "content")}";
    }

    internal static CloudDriveItem MapDriveItem(JsonElement item, MailAccount account)
    {
        var parent = item.TryGetProperty("parentReference", out var parentReference)
            ? parentReference
            : default;
        var file = item.TryGetProperty("file", out var fileFacet) ? fileFacet : default;
        return new CloudDriveItem(
            RequiredString(item, "id"),
            RequiredString(item, "name"),
            item.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            item.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.Object,
            parent.ValueKind == JsonValueKind.Object ? OptionalString(parent, "id") : null,
            Uri.TryCreate(OptionalString(item, "webUrl"), UriKind.Absolute, out var webUrl) ? webUrl : null,
            account.AccountId,
            account.ProviderId,
            file.ValueKind == JsonValueKind.Object ? OptionalString(file, "mimeType") : null,
            parent.ValueKind == JsonValueKind.Object ? OptionalString(parent, "path") : null);
    }

    private static void EnsureDriveOwned(MailAccount account, CloudDriveItem item)
    {
        if (item.AccountId != account.AccountId || item.AccountProviderId != account.ProviderId)
        {
            throw new InvalidOperationException("The selected drive item does not belong to this account and provider.");
        }
    }

    internal static void ValidateDriveName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.Any(character => character < ' ' ||
                                  character is '"' or '*' or '<' or '>' or '?' or ':' or '/' or '\\' or '|'))
        {
            throw new ArgumentException("Enter a valid OneDrive file or folder name.", nameof(name));
        }
    }

    private static void ValidateUpload(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        Stream content,
        long contentLength)
    {
        ValidateDriveName(name);
        if (parent is not null)
        {
            _ = DriveChildrenEndpoint(account, parent);
        }
        if (!content.CanRead)
        {
            throw new ArgumentException("The upload stream must be readable.", nameof(content));
        }
        if (contentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }
    }

    private async Task<CloudDriveItem> UploadSmallFileAsync(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        Stream content,
        long contentLength,
        string? contentType,
        CancellationToken cancellationToken)
    {
        using var request = await CreateRequestAsync(
            account,
            HttpMethod.Put,
            DriveUploadEndpoint(account, parent, name, createSession: false),
            FileScopes,
            cancellationToken);
        request.Content = new StreamContent(new NonDisposingStream(content));
        request.Content.Headers.ContentLength = contentLength;
        request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            ? mediaType
            : new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            responseStream, cancellationToken: cancellationToken);
        return MapDriveItem(document.RootElement, account);
    }

    private async Task<CloudDriveItem> UploadLargeFileAsync(
        MailAccount account,
        CloudDriveItem? parent,
        string name,
        Stream content,
        long contentLength,
        CancellationToken cancellationToken)
    {
        using var session = await SendJsonForResponseAsync(
            account,
            HttpMethod.Post,
            DriveUploadEndpoint(account, parent, name, createSession: true),
            new Dictionary<string, object?>
            {
                ["item"] = new Dictionary<string, object?>
                {
                    ["@microsoft.graph.conflictBehavior"] = "rename",
                    ["name"] = name
                }
            },
            FileScopes,
            cancellationToken);
        var uploadUrl = RequiredString(session.RootElement, "uploadUrl");
        var buffer = new byte[DriveUploadChunkSizeBytes];
        CloudDriveItem? completed = null;
        long offset = 0;
        while (offset < contentLength)
        {
            var expected = (int)Math.Min(buffer.Length, contentLength - offset);
            var count = await ReadChunkAsync(content, buffer, expected, cancellationToken);
            if (count != expected)
            {
                throw new EndOfStreamException("The upload stream ended before its declared length.");
            }

            using var response = await SendUploadChunkWithRetryAsync(
                uploadUrl, buffer, count, offset, contentLength, cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            offset += count;
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(
                    responseStream, cancellationToken: cancellationToken);
                completed = MapDriveItem(document.RootElement, account);
            }
        }

        return completed ??
               throw new HttpRequestException("OneDrive accepted all upload chunks but did not complete the file.");
    }

    internal static async Task<int> ReadChunkAsync(
        Stream source,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var current = await source.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (current == 0)
            {
                break;
            }
            read += current;
        }
        return read;
    }

    private async Task<HttpResponseMessage> SendUploadChunkWithRetryAsync(
        string uploadUrl,
        byte[] buffer,
        int count,
        long offset,
        long totalLength,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                request.Content = new StreamContent(
                    new MemoryStream(buffer, 0, count, writable: false, publiclyVisible: true));
                request.Content.Headers.ContentLength = count;
                request.Content.Headers.ContentRange =
                    new ContentRangeHeaderValue(offset, offset + count - 1, totalLength);
                var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (attempt < 3 && IsTransient(response.StatusCode))
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }
                return response;
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    internal static string CalendarEventsEndpoint(string calendarId)
    {
        if (string.IsNullOrWhiteSpace(calendarId))
        {
            throw new ArgumentException("A calendar must be selected.", nameof(calendarId));
        }

        return $"me/calendars/{Uri.EscapeDataString(calendarId)}/events";
    }

    internal static string CalendarViewEndpoint(
        string? calendarId, DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from)
        {
            throw new ArgumentException("The calendar range end must be after its start.");
        }

        var path = calendarId is null
            ? "me/calendarView"
            : $"me/calendars/{Uri.EscapeDataString(
                string.IsNullOrWhiteSpace(calendarId)
                    ? throw new ArgumentException("A calendar must be selected.", nameof(calendarId))
                    : calendarId)}/calendarView";
        return $"{path}?startDateTime={Uri.EscapeDataString(from.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}" +
               $"&endDateTime={Uri.EscapeDataString(to.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}" +
               $"&$select={EventSelect}&$orderby=start/dateTime&$top=250";
    }

    internal static object BuildEventPayload(CalendarEventDraft draft)
    {
        ValidateDraft(draft);
        return new
        {
            subject = draft.Subject,
            start = GraphDate(draft.StartsAt),
            end = GraphDate(draft.EndsAt),
            location = new { displayName = draft.Location ?? "" },
            attendees = (draft.Attendees ?? []).Select(attendee => new
            {
                emailAddress = new { name = attendee.Address.Name, address = attendee.Address.Address },
                type = EnumValue(attendee.Type)
            }),
            isReminderOn = draft.IsReminderOn,
            reminderMinutesBeforeStart = draft.ReminderMinutesBeforeStart,
            recurrence = draft.Recurrence is null ? null : BuildRecurrence(draft.Recurrence)
        };
    }

    internal static CalendarEvent MapEvent(
        JsonElement item, string? fallbackCalendarId, string? accountId = null)
    {
        var attendees = item.TryGetProperty("attendees", out var attendeeItems)
            ? attendeeItems.EnumerateArray().Select(attendee =>
            {
                var address = attendee.GetProperty("emailAddress");
                return new CalendarAttendee(
                    new MailAddress(OptionalString(address, "name") ?? "", OptionalString(address, "address") ?? ""),
                    ParseAttendeeType(OptionalString(attendee, "type")));
            }).ToArray()
            : [];
        return new CalendarEvent(
            RequiredString(item, "id"),
            item.TryGetProperty("calendar", out var calendar)
                ? OptionalString(calendar, "id") ?? fallbackCalendarId ?? ""
                : fallbackCalendarId ?? "",
            OptionalString(item, "subject") ?? "(no title)",
            ReadGraphDate(item.GetProperty("start")),
            ReadGraphDate(item.GetProperty("end")),
            item.TryGetProperty("location", out var location) ? OptionalString(location, "displayName") : null,
            attendees,
            item.TryGetProperty("isReminderOn", out var reminderOn) && reminderOn.GetBoolean(),
            item.TryGetProperty("reminderMinutesBeforeStart", out var reminderMinutes) ? reminderMinutes.GetInt32() : 0,
            item.TryGetProperty("recurrence", out var recurrence) && recurrence.ValueKind == JsonValueKind.Object
                ? ParseRecurrence(recurrence)
                : null,
            accountId,
            OptionalString(item, "showAs")?.ToLowerInvariant() switch
            {
                "free" => CalendarAvailability.Free,
                "workingelsewhere" => CalendarAvailability.WorkingElsewhere,
                "tentative" => CalendarAvailability.Tentative,
                "busy" => CalendarAvailability.Busy,
                "oof" => CalendarAvailability.OutOfOffice,
                _ => CalendarAvailability.Unknown
            });
    }

    private static void ValidateDraft(CalendarEventDraft draft)
    {
        _ = CalendarEventsEndpoint(draft.CalendarId);
        if (draft.EndsAt <= draft.StartsAt)
        {
            throw new ArgumentException("The event end must be after its start.", nameof(draft));
        }
        if (draft.ReminderMinutesBeforeStart < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(draft), "Reminder minutes cannot be negative.");
        }
        if ((draft.Attendees ?? []).Any(attendee =>
                !System.Net.Mail.MailAddress.TryCreate(attendee.Address.Address, out _)))
        {
            throw new ArgumentException("Every attendee must have a valid email address.", nameof(draft));
        }
        if (draft.Recurrence is { } recurrence)
        {
            ValidateRecurrence(recurrence);
            if (recurrence.StartDate != DateOnly.FromDateTime(draft.StartsAt.UtcDateTime))
            {
                throw new ArgumentException("The recurrence start date must match the event start date.", nameof(draft));
            }
        }
    }

    private static void ValidateRecurrence(CalendarRecurrence recurrence)
    {
        if (recurrence.Interval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recurrence), "Recurrence interval must be positive.");
        }
        if (recurrence.RangeType == CalendarRecurrenceRangeType.EndDate &&
            (recurrence.EndDate is null || recurrence.EndDate < recurrence.StartDate))
        {
            throw new ArgumentException("An end-date recurrence needs an end date on or after its start.", nameof(recurrence));
        }
        if (recurrence.RangeType == CalendarRecurrenceRangeType.Numbered &&
            recurrence.NumberOfOccurrences is not > 0)
        {
            throw new ArgumentException("A numbered recurrence needs a positive occurrence count.", nameof(recurrence));
        }
        if (recurrence.DayOfMonth is < 1 or > 31 || recurrence.Month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(recurrence), "Recurrence day and month values are out of range.");
        }

        var hasDays = recurrence.DaysOfWeek is { Count: > 0 };
        switch (recurrence.PatternType)
        {
            case CalendarRecurrencePatternType.Weekly when !hasDays || recurrence.FirstDayOfWeek is null:
                throw new ArgumentException("Weekly recurrence needs days and a first day of the week.", nameof(recurrence));
            case CalendarRecurrencePatternType.AbsoluteMonthly when recurrence.DayOfMonth is null:
                throw new ArgumentException("Absolute monthly recurrence needs a day of the month.", nameof(recurrence));
            case CalendarRecurrencePatternType.RelativeMonthly when !hasDays:
                throw new ArgumentException("Relative monthly recurrence needs at least one day.", nameof(recurrence));
            case CalendarRecurrencePatternType.AbsoluteYearly when recurrence.DayOfMonth is null || recurrence.Month is null:
                throw new ArgumentException("Absolute yearly recurrence needs a day and month.", nameof(recurrence));
            case CalendarRecurrencePatternType.RelativeYearly when !hasDays || recurrence.Month is null:
                throw new ArgumentException("Relative yearly recurrence needs days and a month.", nameof(recurrence));
        }
    }

    private static object BuildRecurrence(CalendarRecurrence recurrence)
    {
        var pattern = new Dictionary<string, object?>
        {
            ["type"] = EnumValue(recurrence.PatternType),
            ["interval"] = recurrence.Interval
        };
        if (recurrence.DaysOfWeek is { Count: > 0 })
        {
            pattern["daysOfWeek"] = recurrence.DaysOfWeek.Select(EnumValue);
        }
        if (recurrence.FirstDayOfWeek is { } firstDay)
        {
            pattern["firstDayOfWeek"] = EnumValue(firstDay);
        }
        if (recurrence.DayOfMonth is { } day)
        {
            pattern["dayOfMonth"] = day;
        }
        if (recurrence.Index is { } index)
        {
            pattern["index"] = EnumValue(index);
        }
        if (recurrence.Month is { } month)
        {
            pattern["month"] = month;
        }

        var range = new Dictionary<string, object?>
        {
            ["type"] = EnumValue(recurrence.RangeType),
            ["startDate"] = recurrence.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["recurrenceTimeZone"] = "UTC"
        };
        if (recurrence.RangeType == CalendarRecurrenceRangeType.EndDate)
        {
            range["endDate"] = recurrence.EndDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (recurrence.RangeType == CalendarRecurrenceRangeType.Numbered)
        {
            range["numberOfOccurrences"] = recurrence.NumberOfOccurrences;
        }
        return new { pattern, range };
    }

    private static CalendarRecurrence ParseRecurrence(JsonElement recurrence)
    {
        var pattern = recurrence.GetProperty("pattern");
        var range = recurrence.GetProperty("range");
        return new CalendarRecurrence(
            ParsePatternType(RequiredString(pattern, "type")),
            pattern.GetProperty("interval").GetInt32(),
            DateOnly.Parse(RequiredString(range, "startDate"), CultureInfo.InvariantCulture),
            ParseRangeType(RequiredString(range, "type")),
            DateOnly.TryParse(OptionalString(range, "endDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate)
                ? endDate
                : null,
            range.TryGetProperty("numberOfOccurrences", out var occurrences) && occurrences.GetInt32() > 0
                ? occurrences.GetInt32()
                : null,
            pattern.TryGetProperty("daysOfWeek", out var days)
                ? days.EnumerateArray().Select(day => ParseDay(day.GetString() ?? "")).ToArray()
                : null,
            pattern.TryGetProperty("dayOfMonth", out var dayOfMonth) && dayOfMonth.GetInt32() > 0
                ? dayOfMonth.GetInt32()
                : null,
            pattern.TryGetProperty("index", out var index)
                ? ParseIndex(index.GetString())
                : null,
            pattern.TryGetProperty("month", out var month) && month.GetInt32() > 0
                ? month.GetInt32()
                : null,
            pattern.TryGetProperty("firstDayOfWeek", out var firstDay)
                ? ParseDay(firstDay.GetString() ?? "")
                : null);
    }

    private static object GraphDate(DateTimeOffset value) => new
    {
        dateTime = value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        timeZone = "UTC"
    };

    private static DateTimeOffset ReadGraphDate(JsonElement wrapper) =>
        DateTimeOffset.TryParse(
            OptionalString(wrapper, "dateTime"),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var value)
            ? value
            : DateTimeOffset.MinValue;

    private static string EnumValue<T>(T value) where T : struct, Enum =>
        char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];

    private static CalendarAttendeeType ParseAttendeeType(string? value) => value?.ToLowerInvariant() switch
    {
        "optional" => CalendarAttendeeType.Optional,
        "resource" => CalendarAttendeeType.Resource,
        _ => CalendarAttendeeType.Required
    };

    private static CalendarRecurrencePatternType ParsePatternType(string value) =>
        Enum.Parse<CalendarRecurrencePatternType>(value, ignoreCase: true);

    private static CalendarRecurrenceRangeType ParseRangeType(string value) =>
        Enum.Parse<CalendarRecurrenceRangeType>(value, ignoreCase: true);

    private static CalendarRecurrenceIndex? ParseIndex(string? value) =>
        Enum.TryParse<CalendarRecurrenceIndex>(value, ignoreCase: true, out var index) ? index : null;

    private static DayOfWeek ParseDay(string value) =>
        Enum.Parse<DayOfWeek>(value, ignoreCase: true);

    internal static async Task<string> ReadErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var fallback = $"{(int)response.StatusCode} {response.ReasonPhrase ?? "Microsoft Graph request failed."}";
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = OptionalString(error, "message");
                var code = OptionalString(error, "code");
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return string.IsNullOrWhiteSpace(code) ? message : $"{message} ({code})";
                }
            }
        }
        catch (JsonException)
        {
            // Preserve the HTTP status when a proxy returns non-JSON content.
        }
        return fallback;
    }

    private static string RequiredString(JsonElement element, string property) =>
        OptionalString(element, property) ??
        throw new JsonException($"Microsoft Graph omitted required property '{property}'.");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString()
            : null;

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
