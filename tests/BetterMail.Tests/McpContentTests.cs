using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;
using ModelContextProtocol;

namespace BetterMail.Tests;

public sealed class McpContentTests
{
    [Fact]
    public async Task WorkspaceAccessIsIndependentAndContentOperationsUseScopedAccounts()
    {
        await WithTools(async (store, tools, fake, account, mailbox, setSettings) =>
        {
            await Assert.ThrowsAsync<McpException>(() => tools.ListCalendars("microsoft365:account"));
            Assert.Empty(fake.Calls);
            setSettings(new(Enabled: true, AllowWrites: false, WorkspaceAccountIds: ["microsoft365:account"]));
            Assert.Contains("calendar", JsonSerializer.Serialize(await tools.ListCalendars("microsoft365:account")));
            await Assert.ThrowsAsync<McpException>(() => tools.CreateContact("microsoft365:account", "Person", []));
            await Assert.ThrowsAsync<McpException>(() => tools.ListTaskLists("microsoft365:other"));
            setSettings(new(Enabled: true, AllowWrites: true, WorkspaceAccountIds: ["microsoft365:account"]));
            var start = DateTimeOffset.UtcNow;
            var draft = new CalendarEventDraft("calendar", "Review", start, start.AddHours(1), Body: "Details");
            await tools.CreateEvent("microsoft365:account", draft);
            await Assert.ThrowsAsync<McpException>(() => tools.CreateEvent("microsoft365:account", draft with { Attendees = [new(new("Guest", "guest@example.com"))] }));
            await Assert.ThrowsAsync<McpException>(() => tools.UpdateEvent("microsoft365:account", "event", draft));
            await tools.CreateContact("microsoft365:account", "Ada", ["ada@example.com"], new(CompanyName: "Studio"));
            await tools.UpdateContact("microsoft365:account", "contact", "Ada Updated", []);
            await tools.DeleteContact("microsoft365:account", "contact");
            await tools.ListTaskLists("microsoft365:account");
            await tools.ListTasks("microsoft365:account", "list");
            await tools.CreateTaskList("microsoft365:account", "Work");
            await tools.RenameTaskList("microsoft365:account", "list", "Renamed");
            await tools.CreateTask("microsoft365:account", new("untrusted-account", "list", "Task"));
            await tools.UpdateTask("microsoft365:account", "task", new("untrusted-account", "list", "Edited"));
            await tools.SetTaskCompleted("microsoft365:account", "list", "task", true);
            await tools.DeleteTask("microsoft365:account", "list", "task");
            await tools.DeleteTaskList("microsoft365:account", "list");
            await tools.ListNotebooks("microsoft365:account");
            await tools.ListNoteSections("microsoft365:account", "notebook");
            await tools.ListNotePages("microsoft365:account", "notebook", "section");
            await tools.ReadNotePage("microsoft365:account", "notebook", "section", "page");
            await tools.CreateNotePage("microsoft365:account", "notebook", "section", "Title", "<p>Details</p>");
            await Assert.ThrowsAsync<McpException>(() => tools.UpdateNotePage("microsoft365:account", "notebook", "section", "page", fake.Modified.AddSeconds(-1), [new("body", NotePatchAction.Append, "Text")]));
            await tools.UpdateNotePage("microsoft365:account", "notebook", "section", "page", fake.Modified, [new("body", NotePatchAction.Append, "Text")]);
            await tools.DeleteNotePage("microsoft365:account", "notebook", "section", "page", fake.Modified);
            Assert.All(fake.Calls, call => Assert.Equal(account.AccountId, ((MailAccount)call.Args[0]!).AccountId));
            Assert.All(fake.Calls.SelectMany(call => call.Args).OfType<TaskDraft>(), task => Assert.Equal(account.AccountId, task.AccountId));
            setSettings(new(Enabled: true, AllowWrites: true, AllowSending: true, WorkspaceAccountIds: ["microsoft365:account"]));
            await tools.UpdateEvent("microsoft365:account", "event", draft);
            await tools.DeleteEvent("microsoft365:account", "calendar", "event");
            Assert.Contains("create_response_draft", JsonSerializer.Serialize(tools.GetActionGuide("mail")));
            Assert.Contains("create_note_page", JsonSerializer.Serialize(tools.GetActionGuide("notes")));
        });
    }

    [Fact]
    public async Task ResponseDraftUsesProviderThreadAndExactRecipientsThenAcceptsAttachment()
    {
        await WithTools(async (store, tools, fake, account, mailbox, setSettings) =>
        {
            setSettings(new(Enabled: true, AllowWrites: true, MailboxIds: [mailbox.Id]));
            var message = new MailMessage(mailbox.Id, "source", "thread", "<source@example.com>", "inbox", "Source subject",
                new("Sender", "sender@example.com"), [], DateTimeOffset.UtcNow, "Snippet", "Full source content", false, true, false, MailImportance.Normal, [], null);
            await store.ApplySyncPageAsync("seed", new([message], null, false));
            var result = JsonSerializer.SerializeToElement(await tools.CreateResponseDraft(mailbox.Id, "source", MailResponseKind.Reply,
                "accounts@example.com", "Please review"));
            var id = result.GetProperty("Id").GetString()!;
            var saved = (await store.GetLocalDraftAsync(id))!;
            Assert.Equal("remote-response", saved.ProviderDraftId);
            Assert.Equal("accounts@example.com", saved.To);
            Assert.Equal("", saved.Cc); Assert.Equal("", saved.Bcc);
            Assert.Contains("Full source content", saved.Body);
            Assert.Equal(BetterMail.Core.ConversationThread.ThreadIdentity(message), saved.ConversationIdentity);
            Assert.False(saved.IsQueued);
            Assert.Single(fake.Calls, call => call.Name == "CreateResponseDraftAsync");
            var bytes = new byte[] { 1, 2, 3 };
            var upload = await tools.BeginAttachmentUpload(mailbox.Id, id, saved.UpdatedAt, "proof.zip", "application/zip", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
            await tools.UploadAttachmentChunk(mailbox.Id, upload.Id, 0, Convert.ToBase64String(bytes));
            await tools.CompleteAttachmentUpload(mailbox.Id, upload.Id);
            saved = (await store.GetLocalDraftAsync(id))!;
            Assert.Equal("remote-response", saved.ProviderDraftId);
            Assert.Equal(bytes, Assert.Single(saved.Attachments).ContentBytes);
            Assert.False(saved.IsQueued);
            await tools.SetMailState(mailbox.Id, "source", isRead: false, isFlagged: true, isPinned: true);
            Assert.Single(await store.GetMailActionsAsync());
            Assert.True((await store.GetMessageAsync(mailbox.Id, "source"))!.IsPinned);
        });
    }

    private static async Task WithTools(Func<EncryptedMailStore, McpMailTools, ContentProvider, MailAccount, Mailbox, Action<McpConfiguration>, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-mcp-content-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync();
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail | ProviderCapabilities.Calendar | ProviderCapabilities.Contacts | ProviderCapabilities.Tasks | ProviderCapabilities.Notes);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
            await store.SaveAccountAsync(account); await store.SaveMailboxAsync(mailbox);
            var workspace = DispatchProxy.Create<IWorkspaceProvider, ContentProvider>();
            var fake = (ContentProvider)(object)workspace;
            var mail = DispatchProxy.Create<IMailProvider, ContentProvider>();
            ((ContentProvider)(object)mail).Calls = fake.Calls;
            var settings = new McpConfiguration(Enabled: true, MailboxIds: [mailbox.Id], DriveAccountIds: ["microsoft365:account"]);
            var tools = new McpMailTools(store, () => settings, () => Task.CompletedTask, (_, _, _) => throw new InvalidOperationException("Must not send"),
                filesProvider: () => workspace, mailProvider: () => mail);
            await test(store, tools, fake, account, mailbox, value => settings = value);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    public class ContentProvider : DispatchProxy
    {
        public List<(string Name, object?[] Args)> Calls = [];
        public DateTimeOffset Modified { get; } = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            args ??= [];
            Calls.Add((method!.Name, args));
            var account = (MailAccount)args[0]!;
            var now = Modified;
            var list = new TaskListInfo("list", "Work", account.AccountId);
            var page = new NotePage("page", "section", "Page", now, 0, 0, account.AccountId, account.ProviderId);
            object? result = method.Name switch
            {
                "GetCalendarsAsync" => new CalendarInfo[] { new("calendar", "Calendar", null, true, account.AccountId) },
                "CreateEventAsync" or "UpdateEventAsync" => new CalendarEvent("event", "calendar", "Event", now, now.AddHours(1), null, AccountId: account.AccountId),
                "SearchContactsAsync" or "SearchSharedContactsAsync" => new ContactInfo[] { new("contact", "Ada", ["ada@example.com"], account.AccountId) },
                "CreateContactAsync" or "UpdateContactAsync" => new ContactInfo("contact", "Ada", [], account.AccountId),
                "GetTaskListsAsync" => new TaskListInfo[] { list },
                "CreateTaskListAsync" or "RenameTaskListAsync" => list,
                "GetTasksAsync" => new TaskInfo[] { new("task", "list", "Task", null, false, account.AccountId) },
                "CreateTaskAsync" or "UpdateTaskAsync" or "SetTaskCompletedAsync" => new TaskInfo("task", "list", "Task", null, true, account.AccountId),
                "GetNotebooksAsync" => new NoteNotebook[] { new("notebook", "Notes", account.AccountId, account.ProviderId) },
                "GetSectionsAsync" => new NoteSection[] { new("section", "notebook", "Section", account.AccountId, account.ProviderId) },
                "GetPagesAsync" => new NotePage[] { page },
                "GetPageContentAsync" => new NotePageContent("page", "section", account.AccountId, account.ProviderId, "<p>Notes</p>"),
                "CreatePageAsync" => page,
                "CreateResponseDraftAsync" => new CloudDraft("remote-response", account.AccountId, ((Mailbox)args[1]!).Id,
                    ((DraftMessage)args[4]!) with { Cc = [new("Unexpected", "other@example.com")] }, now, ConversationId: "thread"),
                _ when method.ReturnType == typeof(Task) => null,
                _ => throw new InvalidOperationException("Unexpected provider call: " + method.Name)
            };
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [result]);
        }
    }
}
