using System.Text;
using BetterMail.Core;
using Microsoft.Data.Sqlite;

namespace BetterMail.Tests;

public sealed class EncryptedMailStoreTests
{
    [Fact]
    public async Task MigratesLegacyMessagesAndPersistsCcRecipients()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-cc-migration-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "mail.db");
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(directory);
        SQLitePCL.Batteries_V2.Init();
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA key = '{key}';
                CREATE TABLE messages(
                    rowid INTEGER PRIMARY KEY,
                    mailbox_id TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    conversation_id TEXT,
                    internet_message_id TEXT,
                    folder_id TEXT NOT NULL,
                    subject TEXT NOT NULL,
                    from_name TEXT NOT NULL,
                    from_address TEXT NOT NULL,
                    recipients_json TEXT NOT NULL,
                    received_at TEXT NOT NULL,
                    preview TEXT NOT NULL,
                    body TEXT,
                    is_html INTEGER NOT NULL,
                    is_read INTEGER NOT NULL,
                    has_attachments INTEGER NOT NULL,
                    importance INTEGER NOT NULL,
                    categories_json TEXT NOT NULL,
                    etag TEXT,
                    is_flagged INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(mailbox_id, provider_id)
                );
                CREATE VIRTUAL TABLE message_search USING fts5(
                    subject, from_name, from_address, preview, body,
                    content='messages', content_rowid='rowid'
                );
                CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
                    INSERT INTO message_search(rowid, subject, from_name, from_address, preview, body)
                    VALUES (new.rowid, new.subject, new.from_name, new.from_address, new.preview, new.body);
                END;
                CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
                    INSERT INTO message_search(message_search, rowid, subject, from_name, from_address, preview, body)
                    VALUES ('delete', old.rowid, old.subject, old.from_name, old.from_address, old.preview, old.body);
                END;
                CREATE TRIGGER messages_au AFTER UPDATE ON messages BEGIN
                    INSERT INTO message_search(message_search, rowid, subject, from_name, from_address, preview, body)
                    VALUES ('delete', old.rowid, old.subject, old.from_name, old.from_address, old.preview, old.body);
                    INSERT INTO message_search(rowid, subject, from_name, from_address, preview, body)
                    VALUES (new.rowid, new.subject, new.from_name, new.from_address, new.preview, new.body);
                END;
                INSERT INTO messages(
                    mailbox_id, provider_id, folder_id, subject, from_name, from_address,
                    recipients_json, received_at, preview, body, is_html, is_read,
                    has_attachments, importance, categories_json, is_flagged)
                VALUES(
                    'account:person@example.com', 'legacy', 'inbox', 'Legacy', 'Sender',
                    'sender@example.com', '[]', '2026-07-14T12:00:00.0000000+00:00',
                    'Body', 'Body', 0, 1, 0, 1, '[]', 0);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        SqliteConnection.ClearAllPools();

        await using (var store = new EncryptedMailStore(path, key))
        {
            await store.InitializeAsync(cancellationToken);
            var legacy = Assert.Single(await store.GetMessagesAsync(cancellationToken: cancellationToken));
            Assert.Empty(legacy.Cc ?? []);

            var cc = new MailAddress("Cc Person", "cc@example.com");
            await store.ApplySyncPageAsync(
                "cursor",
                new MailSyncPage([legacy with { Cc = [cc] }], null, false),
                cancellationToken);
            var hydrated = Assert.Single(await store.GetMessagesAsync(cancellationToken: cancellationToken));
            Assert.Equal(cc, Assert.Single(hydrated.Cc ?? []));

            await store.ApplySyncPageAsync(
                "cursor",
                new MailSyncPage([hydrated with { Cc = null, IsRead = false }], null, false),
                cancellationToken);
            var reconciled = Assert.Single(await store.GetMessagesAsync(cancellationToken: cancellationToken));
            Assert.Equal(cc, Assert.Single(reconciled.Cc ?? []));
            Assert.False(reconciled.IsRead);
        }

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task StoresSearchesAndDeletesEncryptedMessages()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "mail.db");
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var mailbox = new Mailbox("account", "person@example.com", "Person");

        await using (var store = new EncryptedMailStore(path, key))
        {
            await store.InitializeAsync(cancellationToken);
            await store.SaveAccountAsync(new MailAccount(
                "microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail),
                cancellationToken);
            await store.SaveMailboxAsync(mailbox with { IsShared = true, CanSendAs = true }, cancellationToken);
            var savedMailbox = Assert.Single(await store.GetMailboxesAsync(cancellationToken));
            Assert.True(savedMailbox.IsShared);
            Assert.True(savedMailbox.CanSendAs);
            var folder = new MailFolder(mailbox.Id, "inbox-id", "Inbox", 1, 2, "inbox");
            await store.SaveFoldersAsync(mailbox.Id, [folder], cancellationToken);
            Assert.Equal(folder, Assert.Single(await store.GetFoldersAsync(mailbox.Id, cancellationToken)));
            await store.ApplySyncPageAsync(mailbox.Id, new MailSyncPage(
            [
                Message(mailbox.Id, "one", "Quarterly walrus report", "The unique narwhal figures are attached."),
                Message(mailbox.Id, "two", "Ordinary message", "Nothing special here.")
            ], "cursor-1", false), cancellationToken);

            Assert.Equal("one", (await store.GetMessageAsync(mailbox.Id, "one", cancellationToken))?.ProviderId);
            Assert.Null(await store.GetMessageAsync(mailbox.Id, "missing", cancellationToken));

            var results = await store.SearchAsync("narwhal", cancellationToken: cancellationToken);
            Assert.Single(results);
            Assert.Equal("one", results[0].ProviderId);
            Assert.True(results[0].IsFlagged);
            var discovered = Assert.Single(await store.GetDiscoveredPeopleAsync("sender", cancellationToken: cancellationToken));
            Assert.Equal("sender@example.com", discovered.EmailAddress);
            Assert.Equal("Sender", discovered.DisplayName);
            Assert.Equal(2, discovered.Frequency);
            Assert.Equal(mailbox.Id, Assert.Single(discovered.MailboxIds));
            Assert.Equal("cursor-1", await store.GetSyncCursorAsync(mailbox.Id, cancellationToken));
            Assert.True((await store.GetSyncStateAsync(mailbox.Id, cancellationToken)).IsComplete);
            Assert.Equal(new MailStoreCounts(2, 2, 1), await store.GetMessageCountsAsync(mailbox.Id, cancellationToken));
            var draft = new LocalDraft(
                "draft-one",
                "account",
                mailbox.Id,
                "recipient@example.com",
                "",
                "",
                "Unsent walrus proposal",
                "Confidential draft body",
                [new DraftAttachment("notes.txt", "text/plain", "draft attachment"u8.ToArray())],
                DateTimeOffset.UtcNow);
            await store.SaveLocalDraftAsync(draft, cancellationToken);
            var savedDraft = Assert.Single(await store.GetLocalDraftsAsync(cancellationToken));
            Assert.Equal(draft.Subject, savedDraft.Subject);
            Assert.Equal("notes.txt", Assert.Single(savedDraft.Attachments).Name);
            var providerUpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
            await store.UpdateLocalDraftSyncMetadataAsync(
                draft.Id,
                "server-draft",
                draft.UpdatedAt,
                providerUpdatedAt,
                "etag-1",
                cancellationToken);
            await store.SaveLocalDraftAsync(
                draft with { Subject = "Locally edited", UpdatedAt = providerUpdatedAt.AddSeconds(1) },
                cancellationToken);
            savedDraft = Assert.Single(await store.GetLocalDraftsAsync(cancellationToken));
            Assert.Equal("Locally edited", savedDraft.Subject);
            Assert.Equal("server-draft", savedDraft.ProviderDraftId);
            Assert.Equal(draft.UpdatedAt, savedDraft.SyncedLocalUpdatedAt);
            Assert.Equal(providerUpdatedAt, savedDraft.ProviderUpdatedAt);
            Assert.Equal("etag-1", savedDraft.ProviderETag);
            await store.SaveLocalDraftAsync(
                draft with
                {
                    MailboxId = $"{mailbox.Id}:other-sender",
                    Subject = "Changed sender",
                    UpdatedAt = providerUpdatedAt.AddSeconds(2)
                },
                cancellationToken);
            savedDraft = Assert.Single(await store.GetLocalDraftsAsync(cancellationToken));
            Assert.Null(savedDraft.ProviderDraftId);
            Assert.Null(savedDraft.SyncedLocalUpdatedAt);
            Assert.Null(savedDraft.ProviderUpdatedAt);
            Assert.Null(savedDraft.ProviderETag);

            await store.ApplySyncPageAsync(mailbox.Id, new MailSyncPage(
            [
                Message(mailbox.Id, "one", "", "") with { IsDeleted = true }
            ], "cursor-2", false), cancellationToken);
            Assert.Single(await store.GetMessagesAsync(cancellationToken: cancellationToken));
            Assert.Equal(new MailStoreCounts(1, 1, 0), await store.GetMessageCountsAsync(mailbox.Id, cancellationToken));

            await store.DeleteAccountAsync("microsoft365", "account", cancellationToken);
            Assert.Empty(await store.GetAccountsAsync(cancellationToken));
            Assert.Empty(await store.GetMailboxesAsync(cancellationToken));
            Assert.Empty(await store.GetFoldersAsync(cancellationToken: cancellationToken));
            Assert.Empty(await store.GetMessagesAsync(cancellationToken: cancellationToken));
            Assert.Equal(new MailStoreCounts(0, 0, 0), await store.GetMessageCountsAsync(cancellationToken: cancellationToken));
            Assert.Empty(await store.GetLocalDraftsAsync(cancellationToken));
            Assert.Null(await store.GetSyncCursorAsync(mailbox.Id, cancellationToken));
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        Assert.DoesNotContain("Quarterly walrus report", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("Unsent walrus proposal", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("SQLite format 3", Encoding.ASCII.GetString(bytes));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task RemovingFolderSnapshotDeletesOrphanedMessagesAndSearchRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-folder-gc-{Guid.NewGuid():N}");
        var mailbox = new Mailbox("account", "person@example.com", "Person");
        await using (var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))))
        {
            await store.InitializeAsync(cancellationToken);
            await store.SaveFoldersAsync(mailbox.Id,
            [
                new(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox"),
                new(mailbox.Id, "removed", "Old folder", 0, 1)
            ], cancellationToken);
            await store.ApplySyncPageAsync("cursor", new MailSyncPage(
            [
                Message(mailbox.Id, "keep", "Keep this", "Still present") with { FolderId = "inbox" },
                Message(mailbox.Id, "orphan", "Vanishing walrus", "Removed folder") with { FolderId = "removed" }
            ], null, false), cancellationToken);

            await store.SaveFoldersAsync(mailbox.Id,
                [new(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox")], cancellationToken);

            Assert.NotNull(await store.GetMessageAsync(mailbox.Id, "keep", cancellationToken));
            Assert.Null(await store.GetMessageAsync(mailbox.Id, "orphan", cancellationToken));
            Assert.Empty(await store.SearchAsync("Vanishing walrus", cancellationToken: cancellationToken));
        }
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task ReconcilesAndSearchesEncryptedWorkspaceSnapshots()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"bettermail-workspace-cache-{Guid.NewGuid():N}");
        await using (var store = new EncryptedMailStore(
            Path.Combine(directory, "mail.db"),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))))
        {
            await store.InitializeAsync(cancellationToken);
            var accountId = "account";
            var contact = new ContactInfo("contact", "Adele Vance", ["adele@example.com"], accountId);
            await store.ReplaceWorkspaceItemsAsync(
                "contact", accountId, "all", [contact],
                static item => item.ProviderId,
                static item => $"{item.DisplayName} {string.Join(' ', item.EmailAddresses)}",
                cancellationToken);
            var cachedContact = Assert.Single(await store.SearchWorkspaceItemsAsync<ContactInfo>(
                "contact", "Adele", accountId: accountId, cancellationToken: cancellationToken));
            Assert.Equal(contact.ProviderId, cachedContact.ProviderId);
            Assert.Equal(contact.DisplayName, cachedContact.DisplayName);
            Assert.Equal(contact.EmailAddresses, cachedContact.EmailAddresses);

            await store.ReplaceWorkspaceItemsAsync<ContactInfo>(
                "contact", accountId, "all", [],
                static item => item.ProviderId,
                static item => item.DisplayName,
                cancellationToken);
            Assert.Empty(await store.SearchWorkspaceItemsAsync<ContactInfo>(
                "contact", "Adele", accountId: accountId, cancellationToken: cancellationToken));

            var firstRange = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
            var secondRange = firstRange.AddMonths(1);
            var july = new CalendarEvent(
                "july", "calendar", "July planning", firstRange.AddDays(2), firstRange.AddDays(2).AddHours(1), null,
                AccountId: accountId);
            var august = new CalendarEvent(
                "august", "calendar", "August planning", secondRange.AddDays(2), secondRange.AddDays(2).AddHours(1), null,
                AccountId: accountId);
            await store.ReplaceCalendarEventsAsync(
                accountId, "calendar", firstRange, secondRange, [july], cancellationToken);
            await store.ReplaceCalendarEventsAsync(
                accountId, "calendar", secondRange, secondRange.AddMonths(1), [august], cancellationToken);
            await store.ReplaceCalendarEventsAsync(
                accountId, "calendar", firstRange, secondRange, [], cancellationToken);

            Assert.Empty(await store.GetCalendarEventsAsync(
                accountId, "calendar", firstRange, secondRange, cancellationToken));
            Assert.Equal(august, Assert.Single(await store.GetCalendarEventsAsync(
                accountId, "calendar", secondRange, secondRange.AddMonths(1), cancellationToken)));
            await store.GarbageCollectWorkspaceAsync(accountId, cancellationToken);
            Assert.Single(await store.GetCalendarEventsAsync(
                accountId, "calendar", secondRange, secondRange.AddMonths(1), cancellationToken));
            await store.ReplaceWorkspaceItemsAsync<CalendarInfo>(
                "calendar", accountId, "all", [],
                static item => item.ProviderId,
                static item => item.Name,
                cancellationToken);
            await store.GarbageCollectWorkspaceAsync(accountId, cancellationToken);
            Assert.Empty(await store.GetCalendarEventsAsync(
                accountId, "calendar", secondRange, secondRange.AddMonths(1), cancellationToken));

            var oldFile = new CloudFile(
                "old-file", "old-contract.pdf", 10, null, accountId, "microsoft365", "/drive/root:");
            var currentFile = new CloudFile(
                "current-file", "current-contract.pdf", 10, null, accountId, "microsoft365", "/drive/root:");
            await store.ReplaceDriveDirectoryFilesAsync(
                accountId, "/drive/root:", [oldFile, currentFile], cancellationToken);
            await store.ReplaceDriveDirectoryFilesAsync(
                accountId, "/drive/root:", [currentFile], cancellationToken);
            Assert.Empty(await store.SearchWorkspaceItemsAsync<CloudFile>(
                "drive-file", "old-contract", accountId: accountId, cancellationToken: cancellationToken));
            Assert.Equal("current-file", Assert.Single(await store.SearchWorkspaceItemsAsync<CloudFile>(
                "drive-file", "current-contract", accountId: accountId,
                cancellationToken: cancellationToken)).ProviderId);
        }
        Directory.Delete(directory, recursive: true);
    }

    private static MailMessage Message(string mailboxId, string id, string subject, string body) => new(
        mailboxId,
        id,
        "conversation",
        $"<{id}@example.com>",
        "inbox",
        subject,
        new MailAddress("Sender", "sender@example.com"),
        [new MailAddress("Person", "person@example.com")],
        DateTimeOffset.UtcNow,
        body,
        body,
        false,
        false,
        false,
        MailImportance.Normal,
        [],
        "etag",
        IsFlagged: id == "one");
}
