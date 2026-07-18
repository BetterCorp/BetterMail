using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class SyncEngineTests
{
    [Fact]
    public async Task ConsumesEveryPageAndPersistsTheFinalCursor()
    {
        var provider = new FakeProvider(
            new MailSyncPage([Message("one")], "next-page", true),
            new MailSyncPage([Message("two")], "delta-cursor", false));
        var store = new FakeStore();
        var engine = new SyncEngine(provider, store);
        var account = Account();
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);

        var folder = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 2, "inbox");
        var changed = await engine.SyncFolderAsync(account, mailbox, folder, TestContext.Current.CancellationToken);

        Assert.Equal(2, changed);
        Assert.Equal([null, "next-page"], provider.Cursors);
        Assert.Equal("delta-cursor", store.Cursor);
        Assert.Equal(2, store.Messages.Count);
    }

    [Fact]
    public async Task UsesHistoryScopedCursorAndPassesTheInitialCutoff()
    {
        var provider = new FakeProvider(new MailSyncPage([], "delta-cursor", false));
        var store = new FakeStore();
        var account = Account();
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);
        var folder = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox");

        await new SyncEngine(provider, store).SyncFolderAsync(
            account, mailbox, folder, 90, TestContext.Current.CancellationToken);

        Assert.EndsWith(":history:90", store.CursorId);
        Assert.InRange(provider.ReceivedSince!.Value, DateTimeOffset.UtcNow.AddDays(-91), DateTimeOffset.UtcNow.AddDays(-89));
    }

    [Fact]
    public async Task VersionsTheFullHistoryCursorToRepairPreviouslyTruncatedSyncs()
    {
        var provider = new FakeProvider(new MailSyncPage([], "delta-cursor", false));
        var store = new FakeStore();
        var account = Account();
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);

        await new SyncEngine(provider, store).SyncFolderAsync(
            account,
            mailbox,
            new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 1, "inbox"),
            TestContext.Current.CancellationToken);

        Assert.EndsWith(":history:all-v2", store.CursorId);
    }

    private static MailAccount Account() => new(
        "fake", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);

    private static MailMessage Message(string id) => new(
        "account:person@example.com", id, null, null, "inbox", id,
        new MailAddress("Sender", "sender@example.com"), [], DateTimeOffset.UtcNow,
        id, id, false, false, false, MailImportance.Normal, [], null);

    private sealed class FakeProvider(params MailSyncPage[] pages) : IMailProvider
    {
        private readonly Queue<MailSyncPage> _pages = new(pages);
        public List<string?> Cursors { get; } = [];
        public DateTimeOffset? ReceivedSince { get; private set; }

        public Task<MailSyncPage> SyncFolderAsync(MailAccount account, Mailbox mailbox, string folderId, string? cursor, CancellationToken cancellationToken = default)
        {
            Cursors.Add(cursor);
            return Task.FromResult(_pages.Dequeue());
        }

        public Task<MailSyncPage> SyncFolderAsync(MailAccount account, Mailbox mailbox, string folderId, string? cursor, DateTimeOffset? receivedSince, CancellationToken cancellationToken = default)
        {
            ReceivedSince = receivedSince;
            return SyncFolderAsync(account, mailbox, folderId, cursor, cancellationToken);
        }

        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailFolder>>([]);
        public Task MarkReadAsync(MailAccount account, Mailbox mailbox, string messageId, bool isRead, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Message(messageId));
        public Task MoveMessageAsync(MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task SetFlaggedAsync(MailAccount account, Mailbox mailbox, string messageId, bool isFlagged, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([]);
        public Task SendAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeStore : IMailStore
    {
        public string? Cursor { get; private set; }
        public string? CursorId { get; private set; }
        public List<MailMessage> Messages { get; } = [];

        public Task ApplySyncPageAsync(string mailboxId, MailSyncPage page, CancellationToken cancellationToken = default)
        {
            CursorId = mailboxId;
            Messages.AddRange(page.Messages);
            Cursor = page.NextCursor;
            return Task.CompletedTask;
        }

        public Task<string?> GetSyncCursorAsync(string mailboxId, CancellationToken cancellationToken = default)
        {
            CursorId = mailboxId;
            return Task.FromResult(Cursor);
        }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveAccountAsync(MailAccount account, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailAccount>>([]);
        public Task DeleteAccountAsync(string providerId, string accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveMailboxAsync(Mailbox mailbox, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Mailbox>> GetMailboxesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Mailbox>>([]);
        public Task SaveFoldersAsync(string mailboxId, IReadOnlyList<MailFolder> folders, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(string? mailboxId = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailFolder>>([]);
        public Task<IReadOnlyList<MailMessage>> GetMessagesAsync(string? mailboxId = null, string? folderId = null, int limit = 5000, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailMessage>>(Messages);
        public Task<IReadOnlyList<MailMessage>> SearchAsync(string query, int limit = 5000, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailMessage>>(Messages);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
