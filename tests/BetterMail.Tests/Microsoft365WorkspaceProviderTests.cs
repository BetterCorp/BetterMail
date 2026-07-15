using System.Text.Json;
using System.Net;
using BetterMail.Core;
using BetterMail.Microsoft365;

namespace BetterMail.Tests;

public sealed class Microsoft365WorkspaceProviderTests
{
    private static readonly MailAccount Account = new(
        Microsoft365AuthService.Id,
        "account-id",
        "tenant-id",
        "user@example.com",
        "User",
        ProviderCapabilities.Contacts | ProviderCapabilities.Tasks);

    [Fact]
    public void BuildsCalendarSelectedMeetingPayload()
    {
        var draft = new CalendarEventDraft(
            "calendar/id",
            "Planning",
            new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero),
            "Room 1",
            [new(new MailAddress("Adele", "adele@example.com"), CalendarAttendeeType.Optional)],
            IsReminderOn: true,
            ReminderMinutesBeforeStart: 30,
            Recurrence: new(
                CalendarRecurrencePatternType.Weekly,
                1,
                new DateOnly(2026, 7, 20),
                CalendarRecurrenceRangeType.Numbered,
                NumberOfOccurrences: 6,
                DaysOfWeek: [DayOfWeek.Monday],
                FirstDayOfWeek: DayOfWeek.Monday));

        var payload = JsonSerializer.SerializeToElement(
            Microsoft365WorkspaceProvider.BuildEventPayload(draft));

        Assert.Equal("Planning", payload.GetProperty("subject").GetString());
        Assert.Equal("UTC", payload.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal("optional", payload.GetProperty("attendees")[0].GetProperty("type").GetString());
        Assert.Equal(30, payload.GetProperty("reminderMinutesBeforeStart").GetInt32());
        Assert.Equal("weekly", payload.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString());
        Assert.Equal("monday", payload.GetProperty("recurrence").GetProperty("pattern").GetProperty("daysOfWeek")[0].GetString());
        Assert.Equal(6, payload.GetProperty("recurrence").GetProperty("range").GetProperty("numberOfOccurrences").GetInt32());
        Assert.Equal("me/calendars/calendar%2Fid/events",
            Microsoft365WorkspaceProvider.CalendarEventsEndpoint(draft.CalendarId));
    }

    [Fact]
    public void MapsMeetingReminderAndRecurrence()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id":"event-id",
              "subject":"Planning",
              "start":{"dateTime":"2026-07-20T08:00:00","timeZone":"UTC"},
              "end":{"dateTime":"2026-07-20T09:00:00","timeZone":"UTC"},
              "location":{"displayName":"Room 1"},
              "attendees":[{"emailAddress":{"name":"Adele","address":"adele@example.com"},"type":"required"}],
              "isReminderOn":true,
              "reminderMinutesBeforeStart":15,
              "showAs":"oof",
              "recurrence":{
                "pattern":{"type":"absoluteMonthly","interval":1,"dayOfMonth":20},
                "range":{"type":"endDate","startDate":"2026-07-20","endDate":"2026-12-20","recurrenceTimeZone":"UTC"}
              }
            }
            """);

        var calendarEvent = Microsoft365WorkspaceProvider.MapEvent(document.RootElement, "calendar-id", "account-id");

        Assert.Equal("calendar-id", calendarEvent.CalendarId);
        Assert.Equal("account-id", calendarEvent.AccountId);
        Assert.Equal(TimeSpan.Zero, calendarEvent.StartsAt.Offset);
        Assert.Equal("adele@example.com", Assert.Single(calendarEvent.Attendees!).Address.Address);
        Assert.True(calendarEvent.IsReminderOn);
        Assert.Equal(15, calendarEvent.ReminderMinutesBeforeStart);
        Assert.Equal(CalendarAvailability.OutOfOffice, calendarEvent.Availability);
        Assert.Equal(CalendarRecurrencePatternType.AbsoluteMonthly, calendarEvent.Recurrence!.PatternType);
        Assert.Equal(new DateOnly(2026, 12, 20), calendarEvent.Recurrence.EndDate);
    }

    [Fact]
    public void ValidatesCalendarRangeAndRecurrence()
    {
        var start = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero);
        var invalid = new CalendarEventDraft(
            "calendar",
            "Planning",
            start,
            start.AddHours(1),
            Recurrence: new(
                CalendarRecurrencePatternType.Weekly,
                1,
                DateOnly.FromDateTime(start.UtcDateTime),
                DaysOfWeek: [DayOfWeek.Monday]));

        Assert.Throws<ArgumentException>(() => Microsoft365WorkspaceProvider.BuildEventPayload(invalid));
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.CalendarViewEndpoint("calendar", start, start));
    }

    [Fact]
    public void BuildsAccountOwnedContactPayload()
    {
        var draft = new ContactDraft(
            Account.AccountId,
            "Adele Vance",
            ["adele@example.com", "ADELE@example.com"]);

        var payload = JsonSerializer.SerializeToElement(
            Microsoft365WorkspaceProvider.BuildContactPayload(Account, draft));

        Assert.Equal("Adele Vance", payload.GetProperty("displayName").GetString());
        Assert.Single(payload.GetProperty("emailAddresses").EnumerateArray());
        Assert.Equal("me/contacts/contact%2Fid",
            Microsoft365WorkspaceProvider.ContactEndpoint(Account, Account.AccountId, "contact/id"));
        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.BuildContactPayload(
                Account, draft with { AccountId = "another-account" }));
    }

    [Fact]
    public void MapsContactToItsAccount()
    {
        using var document = JsonDocument.Parse(
            """{"id":"contact-id","displayName":"Adele Vance","emailAddresses":[{"address":"adele@example.com"}]}""");

        var contact = Microsoft365WorkspaceProvider.MapContact(document.RootElement, Account.AccountId);

        Assert.Equal(Account.AccountId, contact.AccountId);
        Assert.Equal("adele@example.com", Assert.Single(contact.EmailAddresses));
    }

    [Fact]
    public void BuildsAndMapsAccountOwnedTask()
    {
        var due = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);
        var draft = new TaskDraft(Account.AccountId, "list/id", "File report", due);

        var payload = JsonSerializer.SerializeToElement(
            Microsoft365WorkspaceProvider.BuildTaskPayload(Account, draft));
        using var response = JsonDocument.Parse(
            """{"id":"task-id","title":"File report","status":"completed","dueDateTime":{"dateTime":"2026-07-21T10:00:00","timeZone":"UTC"}}""");
        var task = Microsoft365WorkspaceProvider.MapTask(
            response.RootElement, draft.ListId, Account.AccountId);

        Assert.Equal("UTC", payload.GetProperty("dueDateTime").GetProperty("timeZone").GetString());
        Assert.Equal("me/todo/lists/list%2Fid/tasks/task%2Fid",
            Microsoft365WorkspaceProvider.TaskEndpoint(
                Account, Account.AccountId, "list/id", "task/id"));
        Assert.True(task.IsComplete);
        Assert.Equal(Account.AccountId, task.AccountId);
        Assert.Equal(TimeSpan.Zero, task.DueAt!.Value.Offset);
    }

    [Fact]
    public void MapsTaskListAndRejectsCrossAccountSelection()
    {
        using var document = JsonDocument.Parse(
            """{"id":"list-id","displayName":"Tasks","wellknownListName":"defaultList"}""");

        var list = Microsoft365WorkspaceProvider.MapTaskList(document.RootElement, Account.AccountId);

        Assert.Equal(Account.AccountId, list.AccountId);
        Assert.Equal("defaultList", list.WellKnownName);
        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.TaskEndpoint(
                Account, "another-account", list.ProviderId));
    }

    [Fact]
    public void BuildsAndMapsRichTaskFields()
    {
        var recurrence = new CalendarRecurrence(
            CalendarRecurrencePatternType.Weekly,
            2,
            new DateOnly(2026, 7, 20),
            CalendarRecurrenceRangeType.Numbered,
            NumberOfOccurrences: 5,
            DaysOfWeek: [DayOfWeek.Monday],
            FirstDayOfWeek: DayOfWeek.Monday);
        var draft = new TaskDraft(
            Account.AccountId,
            "list-id",
            "Prepare report",
            new DateTimeOffset(2026, 7, 20, 14, 0, 0, TimeSpan.Zero),
            "<p>Use final numbers</p>",
            TaskBodyContentType.Html,
            TaskImportance.High,
            IsReminderOn: true,
            ReminderAt: new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero),
            Recurrence: recurrence,
            Categories: ["Finance", "finance"],
            Status: TodoTaskStatus.InProgress,
            CreatedAt: new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero));

        var payload = JsonSerializer.SerializeToElement(
            Microsoft365WorkspaceProvider.BuildTaskPayload(Account, draft));
        using var response = JsonDocument.Parse(
            """
            {
              "id":"task-id",
              "title":"Prepare report",
              "status":"waitingOnOthers",
              "body":{"content":"<p>Use final numbers</p>","contentType":"html"},
              "dueDateTime":{"dateTime":"2026-07-20T14:00:00","timeZone":"UTC"},
              "importance":"high",
              "isReminderOn":true,
              "reminderDateTime":{"dateTime":"2026-07-20T12:00:00","timeZone":"UTC"},
              "recurrence":{
                "pattern":{"type":"weekly","interval":2,"daysOfWeek":["monday"],"firstDayOfWeek":"monday"},
                "range":{"type":"numbered","startDate":"2026-07-20","numberOfOccurrences":5}
              },
              "categories":["Finance"],
              "createdDateTime":"2026-07-14T08:00:00Z",
              "completedDateTime":null
            }
            """);
        var task = Microsoft365WorkspaceProvider.MapTask(
            response.RootElement, "list-id", Account.AccountId);

        Assert.Equal("html", payload.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("high", payload.GetProperty("importance").GetString());
        Assert.True(payload.GetProperty("isReminderOn").GetBoolean());
        Assert.Equal("inProgress", payload.GetProperty("status").GetString());
        Assert.StartsWith("2026-07-14T08:00:00", payload.GetProperty("createdDateTime").GetString());
        Assert.Single(payload.GetProperty("categories").EnumerateArray());
        Assert.Equal("weekly", payload.GetProperty("recurrence").GetProperty("pattern").GetProperty("type").GetString());
        Assert.Equal(TaskBodyContentType.Html, task.NotesContentType);
        Assert.Equal(TaskImportance.High, task.Importance);
        Assert.Equal(TodoTaskStatus.WaitingOnOthers, task.Status);
        Assert.True(task.IsReminderOn);
        Assert.Equal(recurrence.PatternType, task.Recurrence!.PatternType);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 8, 0, 0, TimeSpan.Zero), task.CreatedAt);
        Assert.Null(task.CompletedAt);
    }

    [Fact]
    public void ValidatesTaskReminderRecurrenceAndCategories()
    {
        var draft = new TaskDraft(
            Account.AccountId,
            "list-id",
            "Task",
            IsReminderOn: true);
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.BuildTaskPayload(Account, draft));
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.BuildTaskPayload(
                Account, draft with
                {
                    IsReminderOn = null,
                    Recurrence = new(
                        CalendarRecurrencePatternType.Daily,
                        1,
                        new DateOnly(2026, 7, 20)),
                    ClearRecurrence = true
                }));
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.BuildTaskPayload(
                Account, draft with { IsReminderOn = null, Categories = [""] }));
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.BuildTaskPayload(
                Account, draft with
                {
                    IsReminderOn = null,
                    Status = TodoTaskStatus.Deferred,
                    CompletedAt = DateTimeOffset.UtcNow
                }));
    }

    [Fact]
    public void BuildsOwnedTaskListMutationsAndPreservesPagingLink()
    {
        using var customJson = JsonDocument.Parse(
            """{"id":"list/id","displayName":"Projects","wellknownListName":"none","isOwner":true,"isShared":true}""");
        var custom = Microsoft365WorkspaceProvider.MapTaskList(
            customJson.RootElement, Account.AccountId);
        var payload = JsonSerializer.SerializeToElement(
            Microsoft365WorkspaceProvider.BuildTaskListPayload(" Projects "));
        using var page = JsonDocument.Parse(
            """{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/me/todo/lists?$skiptoken=next"}""");

        Assert.Equal("Projects", payload.GetProperty("displayName").GetString());
        Assert.Equal(
            "me/todo/lists/list%2Fid",
            Microsoft365WorkspaceProvider.TaskListEndpoint(
                Account, custom.AccountId, custom.ProviderId));
        Microsoft365WorkspaceProvider.EnsureMutableTaskList(Account, custom);
        Assert.True(custom.IsShared);
        Assert.Equal(
            "https://graph.microsoft.com/v1.0/me/todo/lists?$skiptoken=next",
            Microsoft365WorkspaceProvider.NextPageEndpoint(page.RootElement));

        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.EnsureMutableTaskList(
                Account, custom with { WellKnownName = "defaultList" }));
        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.EnsureMutableTaskList(
                Account, custom with { IsOwner = false }));
        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.EnsureMutableTaskList(
                Account, custom with { AccountId = "another-account" }));
    }

    [Fact]
    public async Task PreservesActionableGraphErrorDetails()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""")
        };

        var message = await Microsoft365WorkspaceProvider.ReadErrorAsync(
            response, TestContext.Current.CancellationToken);

        Assert.Equal("Access is denied. (ErrorAccessDenied)", message);
    }

    [Fact]
    public void MapsHierarchicalDriveItemOwnership()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id":"folder-id",
              "name":"Projects",
              "size":0,
              "webUrl":"https://example.sharepoint.com/projects",
              "folder":{"childCount":2},
              "parentReference":{"id":"root-id","path":"/drive/root:"}
            }
            """);

        var item = Microsoft365WorkspaceProvider.MapDriveItem(document.RootElement, Account);

        Assert.True(item.IsFolder);
        Assert.Equal(Account.AccountId, item.AccountId);
        Assert.Equal(Account.ProviderId, item.AccountProviderId);
        Assert.Equal("root-id", item.ParentProviderId);
        Assert.Equal("/drive/root:/Projects", item.Path);
        Assert.Equal(
            "me/drive/items/folder-id/children",
            Microsoft365WorkspaceProvider.DriveChildrenEndpoint(Account, item));
    }

    [Fact]
    public void BuildsOwnedDriveTransferEndpoints()
    {
        var folder = new CloudDriveItem(
            "folder/id",
            "Projects",
            0,
            IsFolder: true,
            ParentProviderId: "root",
            WebUrl: null,
            Account.AccountId,
            Account.ProviderId);

        Assert.Equal(
            "me/drive/items/folder%2Fid:/report%20final.pdf:/content",
            Microsoft365WorkspaceProvider.DriveUploadEndpoint(
                Account, folder, "report final.pdf", createSession: false));
        Assert.Equal(
            "me/drive/root:/report.pdf:/createUploadSession",
            Microsoft365WorkspaceProvider.DriveUploadEndpoint(
                Account, null, "report.pdf", createSession: true));
        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.DriveChildrenEndpoint(
                Account, folder with { AccountId = "another-account" }));
    }

    [Fact]
    public async Task ReadsOneReusableUploadChunkAcrossPartialReads()
    {
        await using var source = new PartialReadStream([1, 2, 3, 4, 5], 2);
        var buffer = new byte[5];

        var count = await Microsoft365WorkspaceProvider.ReadChunkAsync(
            source, buffer, buffer.Length, TestContext.Current.CancellationToken);

        Assert.Equal(5, count);
        Assert.Equal([1, 2, 3, 4, 5], buffer);
        Assert.Equal(0, Microsoft365WorkspaceProvider.DriveUploadChunkSizeBytes % (320 * 1024));
        Assert.False(Microsoft365WorkspaceProvider.RequiresUploadSession(10 * 1024 * 1024));
        Assert.True(Microsoft365WorkspaceProvider.RequiresUploadSession(10 * 1024 * 1024 + 1));
    }

    [Fact]
    public void RejectsInvalidDriveNamesAndFileAsParent()
    {
        var file = new CloudDriveItem(
            "file-id",
            "report.pdf",
            10,
            IsFolder: false,
            ParentProviderId: "root",
            WebUrl: null,
            Account.AccountId,
            Account.ProviderId);

        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.ValidateDriveName("../report.pdf"));
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.DriveChildrenEndpoint(Account, file));
    }

    [Fact]
    public void MapsHierarchicalOneNoteOwnership()
    {
        using var notebookJson = JsonDocument.Parse(
            """{"id":"notebook/id","displayName":"Work","links":{"oneNoteWebUrl":{"href":"https://onenote.example/work"}}}""");
        using var sectionJson = JsonDocument.Parse(
            """{"id":"section/id","displayName":"Projects"}""");
        using var pageJson = JsonDocument.Parse(
            """{"id":"page/id","title":"Roadmap","lastModifiedDateTime":"2026-07-14T08:00:00Z","order":4,"level":1}""");

        var notebook = Microsoft365WorkspaceProvider.MapNotebook(notebookJson.RootElement, Account);
        var section = Microsoft365WorkspaceProvider.MapSection(
            sectionJson.RootElement, notebook.ProviderId, Account);
        var page = Microsoft365WorkspaceProvider.MapPage(
            pageJson.RootElement, section.ProviderId, Account);

        Assert.Equal(Account.AccountId, notebook.AccountId);
        Assert.Equal(Account.ProviderId, section.AccountProviderId);
        Assert.Equal(notebook.ProviderId, section.NotebookProviderId);
        Assert.Equal(section.ProviderId, page.SectionProviderId);
        Assert.Equal(4, page.Order);
        Assert.Equal("https://onenote.example/work", notebook.WebUrl!.AbsoluteUri);
    }

    [Fact]
    public void BuildsAccountOwnedOneNoteEndpoints()
    {
        var notebook = new NoteNotebook(
            "notebook/id", "Work", Account.AccountId, Account.ProviderId);
        var section = new NoteSection(
            "section/id", notebook.ProviderId, "Projects", Account.AccountId, Account.ProviderId);
        var page = new NotePage(
            "page/id", section.ProviderId, "Roadmap", DateTimeOffset.UtcNow, 0, 0,
            Account.AccountId, Account.ProviderId);

        Assert.Equal(
            "me/onenote/notebooks/notebook%2Fid/sections",
            Microsoft365WorkspaceProvider.NotebookSectionsEndpoint(Account, notebook));
        Assert.Equal(
            "me/onenote/sections/section%2Fid/pages",
            Microsoft365WorkspaceProvider.SectionPagesEndpoint(Account, section));
        Assert.Equal(
            "me/onenote/pages/page%2Fid",
            Microsoft365WorkspaceProvider.PageEndpoint(Account, page));
        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.PageEndpoint(
                Account, page with { AccountId = "another-account" }));
    }

    [Fact]
    public void BuildsSafeOneNoteCreateDocument()
    {
        var draft = new NotePageDraft(
            Account.AccountId,
            Account.ProviderId,
            "section-id",
            """Quarterly <review> & "plan" """,
            "<p><strong>Owned rich text</strong></p>");

        var html = Microsoft365WorkspaceProvider.BuildNotePageHtml(Account, draft);

        Assert.Contains(
            "<title>Quarterly &lt;review&gt; &amp; &quot;plan&quot;</title>",
            html);
        Assert.Contains("<body><p><strong>Owned rich text</strong></p></body>", html);
        Assert.Throws<InvalidOperationException>(() =>
            Microsoft365WorkspaceProvider.BuildNotePageHtml(
                Account, draft with { AccountProviderId = "google-workspace" }));
    }

    [Fact]
    public void BuildsStructuredOneNotePatchAndRejectsMalformedCommands()
    {
        var page = new NotePage(
            "page-id", "section-id", "Roadmap", DateTimeOffset.UtcNow, 0, 0,
            Account.AccountId, Account.ProviderId);
        var commands = Microsoft365WorkspaceProvider.BuildNotePatchPayload(
            Account,
            page,
            [
                new("#div:{33f8}{1}", NotePatchAction.Replace, "<p>Updated</p>"),
                new("body", NotePatchAction.Append, "<p>Next</p>")
            ]);
        var payload = JsonSerializer.SerializeToElement(commands);

        Assert.Equal("replace", payload[0].GetProperty("action").GetString());
        Assert.Equal("<p>Updated</p>", payload[0].GetProperty("content").GetString());
        Assert.Equal("append", payload[1].GetProperty("action").GetString());
        var generatedIdCommand = Microsoft365WorkspaceProvider.BuildNotePatchPayload(
            Account,
            page,
            [new("p:{33f8}{40}", NotePatchAction.Insert, "<p>After</p>")]);
        Assert.Equal(
            "p:{33f8}{40}",
            JsonSerializer.SerializeToElement(generatedIdCommand)[0].GetProperty("target").GetString());
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.BuildNotePatchPayload(
                Account, page, [new("title", NotePatchAction.Replace, "Title", NotePatchPosition.Before)]));
        Assert.Throws<ArgumentException>(() =>
            Microsoft365WorkspaceProvider.BuildNotePatchPayload(
                Account, page, [new("bad target", NotePatchAction.Replace, "Title")]));
    }

    private sealed class PartialReadStream(byte[] bytes, int maxRead) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, maxRead)], cancellationToken);
    }
}
