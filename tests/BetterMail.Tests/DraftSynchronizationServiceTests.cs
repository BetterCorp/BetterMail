using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class DraftSynchronizationServiceTests
{
    private static readonly MailAccount Account = new(
        "microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);
    private static readonly Mailbox Mailbox = new(Account.AccountId, Account.EmailAddress, Account.DisplayName);

    [Fact]
    public async Task CreatesRemoteDraftsAndKeepsEveryLocalDraftWhenOneFails()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new DraftStore(
        [
            Local("keep-id", "Working", now),
            Local("fail-id", "Fail", now)
        ]);
        var provider = new DraftProvider { FailingSubject = "Fail" };
        var service = new DraftSynchronizationService(provider, store);

        var result = await service.SynchronizeAsync(Account, Mailbox, TestContext.Current.CancellationToken);

        Assert.Contains(result.Items, item =>
            item.LocalDraftId == "keep-id" && item.Status == DraftSyncStatus.CreatedRemote);
        Assert.Contains(result.Items, item =>
            item.LocalDraftId == "fail-id" && item.Status == DraftSyncStatus.Failed);
        Assert.Equal(2, store.Drafts.Count);
        Assert.Equal("server-Working", store.Drafts.Single(draft => draft.Id == "keep-id").ProviderDraftId);
        Assert.Null(store.Drafts.Single(draft => draft.Id == "fail-id").ProviderDraftId);
    }

    [Fact]
    public async Task PreservesConflictsAndImportsOnlySupportedUnmappedDrafts()
    {
        var baseline = DateTimeOffset.UtcNow.AddMinutes(-10);
        var local = Local("conflict", "Local edit", baseline.AddMinutes(2)) with
        {
            ProviderDraftId = "mapped",
            SyncedLocalUpdatedAt = baseline,
            ProviderUpdatedAt = baseline,
            ProviderETag = "old"
        };
        var missing = Local("missing", "Keep me", baseline) with
        {
            ProviderDraftId = "gone",
            SyncedLocalUpdatedAt = baseline,
            ProviderUpdatedAt = baseline
        };
        var provider = new DraftProvider
        {
            RemoteDrafts =
            [
                Cloud("mapped", "Server edit", baseline.AddMinutes(3), etag: "new"),
                Cloud("new", "Imported", baseline.AddMinutes(4)),
                Cloud("unsupported", "Outlook item", baseline.AddMinutes(5), unsupported: true)
            ]
        };
        var store = new DraftStore([local, missing]);
        var service = new DraftSynchronizationService(provider, store);

        var result = await service.SynchronizeAsync(Account, Mailbox, TestContext.Current.CancellationToken);

        Assert.Contains(result.Items, item =>
            item.LocalDraftId == "conflict" && item.Status == DraftSyncStatus.Conflict);
        Assert.Contains(result.Items, item =>
            item.LocalDraftId == "missing" && item.Status == DraftSyncStatus.MissingRemote);
        Assert.Contains(result.Items, item =>
            item.ProviderDraftId == "new" && item.Status == DraftSyncStatus.ImportedRemote);
        Assert.Contains(result.Items, item =>
            item.ProviderDraftId == "unsupported" && item.Status == DraftSyncStatus.UnsupportedAttachment);
        Assert.Equal("Local edit", store.Drafts.Single(draft => draft.Id == "conflict").Subject);
        Assert.Contains(store.Drafts, draft => draft.Id == "missing");
        Assert.Contains(store.Drafts, draft => draft.ProviderDraftId == "new" && draft.Subject == "Imported");
        Assert.DoesNotContain(store.Drafts, draft => draft.ProviderDraftId == "unsupported");
        Assert.Equal(0, provider.UpdateCount);
    }

    private static LocalDraft Local(string id, string subject, DateTimeOffset updatedAt) => new(
        id,
        Account.AccountId,
        Mailbox.Id,
        "recipient@example.com",
        "",
        "",
        subject,
        "Body",
        [],
        updatedAt);

    private static CloudDraft Cloud(
        string id,
        string subject,
        DateTimeOffset updatedAt,
        string? etag = null,
        bool unsupported = false) => new(
        id,
        Account.AccountId,
        Mailbox.Id,
        new DraftMessage(subject, [new("Recipient", "recipient@example.com")], "Body", false),
        updatedAt,
        etag,
        unsupported);

    private sealed class DraftStore(IEnumerable<LocalDraft> drafts) : IDraftStore
    {
        public List<LocalDraft> Drafts { get; } = [.. drafts];

        public Task SaveLocalDraftAsync(LocalDraft draft, CancellationToken cancellationToken = default)
        {
            var index = Drafts.FindIndex(candidate => candidate.Id == draft.Id);
            if (index < 0)
            {
                Drafts.Add(draft);
            }
            else
            {
                Drafts[index] = draft;
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LocalDraft>> GetLocalDraftsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalDraft>>(Drafts.ToArray());

        public Task DeleteLocalDraftAsync(string id, CancellationToken cancellationToken = default)
        {
            Drafts.RemoveAll(draft => draft.Id == id);
            return Task.CompletedTask;
        }

        public Task UpdateLocalDraftSyncMetadataAsync(
            string id,
            string providerDraftId,
            DateTimeOffset syncedLocalUpdatedAt,
            DateTimeOffset providerUpdatedAt,
            string? providerETag,
            CancellationToken cancellationToken = default)
        {
            var index = Drafts.FindIndex(draft => draft.Id == id);
            Drafts[index] = Drafts[index] with
            {
                ProviderDraftId = providerDraftId,
                SyncedLocalUpdatedAt = syncedLocalUpdatedAt,
                ProviderUpdatedAt = providerUpdatedAt,
                ProviderETag = providerETag
            };
            return Task.CompletedTask;
        }
    }

    private sealed class DraftProvider : IMailProvider
    {
        public string? FailingSubject { get; init; }
        public IReadOnlyList<CloudDraft> RemoteDrafts { get; init; } = [];
        public int UpdateCount { get; private set; }

        public Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(
            MailAccount account,
            Mailbox mailbox,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RemoteDrafts);

        public Task<CloudDraft> CreateDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            DraftMessage draft,
            CancellationToken cancellationToken = default) =>
            draft.Subject == FailingSubject
                ? Task.FromException<CloudDraft>(new InvalidOperationException("Graph unavailable"))
                : Task.FromResult(new CloudDraft(
                    $"server-{draft.Subject}",
                    account.AccountId,
                    mailbox.Id,
                    draft,
                    DateTimeOffset.UtcNow,
                    "created"));

        public Task<CloudDraft> UpdateDraftAsync(
            MailAccount account,
            Mailbox mailbox,
            string draftId,
            DraftMessage draft,
            CancellationToken cancellationToken = default)
        {
            UpdateCount++;
            return Task.FromResult(new CloudDraft(
                draftId, account.AccountId, mailbox.Id, draft, DateTimeOffset.UtcNow, "updated"));
        }

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
        public Task SendAsync(
            MailAccount account, Mailbox mailbox, DraftMessage draft,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
