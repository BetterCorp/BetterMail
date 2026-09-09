using System.Net;
using System.Security.Cryptography;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class OutboxServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgementIsHeldAcrossRestartAndCanBeReturnedToDrafts(bool cloudDraft)
    {
        await WithStore(async (store, reopen, account, mailbox) =>
        {
            var provider = new Provider { SupportsCloudDrafts = cloudDraft, SendError = new HttpRequestException("Lost acknowledgement") };
            await Queue(store, account, mailbox);
            await new OutboxService(provider, store).ProcessAsync(account, mailbox, "draft");
            var action = Assert.Single(await store.GetMailActionsAsync());
            Assert.True(action.NeedsSendReview);
            Assert.False(action.CanCancel);
            Assert.False(await store.CancelMailActionAsync(action.Id));
            await using var restarted = reopen();
            await restarted.InitializeAsync();
            provider.SendError = null;
            await new OutboxService(provider, restarted).ProcessAsync(account, mailbox, "draft");
            Assert.Equal(1, provider.Sends);
            Assert.True(await new OutboxService(provider, restarted).ReturnToDraftAsync(account, mailbox, action.Id));
            var draft = Assert.Single(await restarted.GetLocalDraftsAsync());
            Assert.False(draft.IsQueued);
            Assert.Null(draft.ProviderDraftId);
            Assert.Equal("attachment"u8.ToArray(), Assert.Single(draft.Attachments).ContentBytes);
            await new OutboxService(provider, restarted).ProcessAsync(account, mailbox, "draft");
            Assert.Equal(1, provider.Sends);
        });
    }

    [Fact]
    public async Task ReturningUnconfirmedSendPreservesExistingCloudDraftWithoutDuplication()
    {
        await WithStore(async (store, _, account, mailbox) =>
        {
            await Queue(store, account, mailbox);
            var provider = new Provider { SupportsCloudDrafts = true, KeepRemoteDraft = true, SendError = new HttpRequestException("Offline") };
            var service = new OutboxService(provider, store);
            await service.ProcessAsync(account, mailbox, "draft");
            Assert.True(await service.ReturnToDraftAsync(account, mailbox, "send:draft"));
            Assert.Equal("remote", Assert.Single(await store.GetLocalDraftsAsync()).ProviderDraftId);
            await new DraftSynchronizationService(provider, store).SynchronizeAsync(account, mailbox);
            Assert.Equal(1, provider.Creates);
            Assert.Single(await store.GetLocalDraftsAsync());
        });
    }

    [Fact]
    public async Task CrashAfterRecordingAttemptCannotResend()
    {
        await WithStore(async (store, reopen, account, mailbox) =>
        {
            await Queue(store, account, mailbox);
            await store.StartMailActionAsync("send:draft");
            await store.MarkSendAttemptedAsync("draft");
            await using var restarted = reopen();
            await restarted.InitializeAsync();
            var provider = new Provider();
            await new OutboxService(provider, restarted).ProcessAsync(account, mailbox, "draft");
            Assert.Equal(0, provider.Sends);
            Assert.True(Assert.Single(await restarted.GetMailActionsAsync()).NeedsSendReview);
        });
    }

    [Fact]
    public async Task PositiveProviderEvidenceResolvesUnconfirmedSendWithoutResending()
    {
        await WithStore(async (store, _, account, mailbox) =>
        {
            await Queue(store, account, mailbox);
            var provider = new Provider { SupportsCloudDrafts = true, SendError = new HttpRequestException("Lost response") };
            var service = new OutboxService(provider, store);
            await service.ProcessAsync(account, mailbox, "draft");
            provider.SentConfirmed = true;
            await service.ProcessAsync(account, mailbox, "draft");
            Assert.Equal(1, provider.Sends);
            Assert.Empty(await store.GetMailActionsAsync());
            Assert.Empty(await store.GetLocalDraftsAsync());
        });
    }

    [Fact]
    public async Task ExplicitRejectionCanRetryAndAcceptedSendIsCleanedUp()
    {
        await WithStore(async (store, _, account, mailbox) =>
        {
            await Queue(store, account, mailbox);
            var provider = new Provider { SendError = new HttpRequestException("Throttled", null, HttpStatusCode.TooManyRequests) };
            var service = new OutboxService(provider, store);
            await service.ProcessAsync(account, mailbox, "draft");
            Assert.False(Assert.Single(await store.GetMailActionsAsync()).SendAttempted);
            provider.SendError = null;
            await service.ProcessAsync(account, mailbox, "draft");
            Assert.Equal(2, provider.Sends);
            Assert.Empty(await store.GetLocalDraftsAsync());
        });
    }

    [Fact]
    public async Task SyncHealthPersistsAndIsRemovedWithAccount()
    {
        await WithStore(async (store, reopen, account, mailbox) =>
        {
            await store.SaveAccountAsync(account);
            var success = DateTimeOffset.UtcNow;
            await store.SaveSyncHealthAsync(new(account.AccountId, mailbox.Id, mailbox.Address, success, "Offline"));
            await using var restarted = reopen();
            await restarted.InitializeAsync();
            Assert.Equal(success, Assert.Single(await restarted.GetSyncHealthAsync()).LastSuccess);
            await restarted.DeleteAccountAsync(account.ProviderId, account.AccountId);
            Assert.Empty(await restarted.GetSyncHealthAsync());
        });
    }

    private static Task Queue(EncryptedMailStore store, MailAccount account, Mailbox mailbox) =>
        store.SaveLocalDraftAsync(new("draft", account.AccountId, mailbox.Id, "to@example.test", "", "",
            "Subject", "Body", [new("file.txt", "text/plain", "attachment"u8.ToArray())], DateTimeOffset.UtcNow, IsQueued: true));

    private static async Task WithStore(Func<EncryptedMailStore, Func<EncryptedMailStore>, MailAccount, Mailbox, Task> run)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-outbox-" + Guid.NewGuid().ToString("N"));
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        EncryptedMailStore Open() => new(Path.Combine(directory, "mail.db"), key);
        try
        {
            await using var store = Open();
            await store.InitializeAsync();
            var account = new MailAccount("test", "account", "tenant", "me@example.test", "Me", ProviderCapabilities.Mail);
            await run(store, Open, account, new(account.AccountId, account.EmailAddress, "Me"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Provider : IMailProvider
    {
        public bool SupportsCloudDrafts { get; init; }
        public Exception? SendError { get; set; }
        public bool SentConfirmed { get; set; }
        public int Sends { get; private set; }
        public int Creates { get; private set; }
        public bool KeepRemoteDraft { get; init; }
        private CloudDraft? _remote;
        public Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CloudDraft>>(KeepRemoteDraft && _remote is not null ? [_remote] : []);
        public Task<bool> IsDraftSentAsync(MailAccount account, Mailbox mailbox, string draftId, CancellationToken cancellationToken = default) => Task.FromResult(SentConfirmed);
        public Task<CloudDraft> CreateDraftAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default)
        {
            Creates++;
            _remote = new CloudDraft("remote", account.AccountId, mailbox.Id, draft, DateTimeOffset.UtcNow);
            return Task.FromResult(_remote);
        }
        public Task SendDraftAsync(MailAccount account, Mailbox mailbox, string draftId, CancellationToken cancellationToken = default) => Send();
        public Task SendAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default) => Send();
        private Task Send() { Sends++; return SendError is { } error ? Task.FromException(error) : Task.CompletedTask; }
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailFolder>>([]);
        public Task<MailSyncPage> SyncFolderAsync(MailAccount account, Mailbox mailbox, string folderId, string? cursor, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MarkReadAsync(MailAccount account, Mailbox mailbox, string messageId, bool isRead, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MailMessage> GetMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveMessageAsync(MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetFlaggedAsync(MailAccount account, Mailbox mailbox, string messageId, bool isFlagged, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
