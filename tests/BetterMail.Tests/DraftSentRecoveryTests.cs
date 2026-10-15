using System.Net;
using System.Text.Json;
using BetterMail.Core;
using BetterMail.Microsoft365;

namespace BetterMail.Tests;

public sealed class DraftSentRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SentDraftWithChangedOutlookIdIsRemovedWithoutChangingRemoteMail(bool shared)
    {
        await WithDraft(async (store, account, mailbox, draft, token) =>
        {
            var handler = new SentHandler();
            using var http = Client(handler);
            IMailProvider provider = new MailProviderRouter([(account.ProviderId, Provider(http))]);

            var result = await new DraftSynchronizationService(provider, store).SynchronizeAsync(account, mailbox, token);

            Assert.Equal(DraftSyncStatus.RemovedSent, Assert.Single(result.Items).Status);
            Assert.Empty(await store.GetLocalDraftsAsync(token));
            Assert.Empty(await store.GetMailActionsAsync(token));
            Assert.True((await store.GetMailActionAsync("delete:" + draft.Id, token))!.Accepted);
            Assert.Equal(4, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.StartsWith(
                shared ? "/v1.0/users/shared%2Bops%40example.test/" : "/v1.0/me/", request.Uri.AbsolutePath));
            Assert.Single(handler.Requests, request => request.ImmutableIds);
            Assert.All(handler.Requests, request => Assert.True(request.Method == HttpMethod.Get ||
                request.Method == HttpMethod.Post && request.Uri.AbsolutePath.EndsWith("/translateExchangeIds")));
        }, shared);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("still-draft")]
    [InlineData("other-folder")]
    [InlineData("no-translation")]
    [InlineData("wrong-source")]
    [InlineData("duplicate")]
    [InlineData("empty-target")]
    public async Task UnconfirmedDraftIsKeptWithItsContents(string mode)
    {
        await WithDraft(async (store, account, mailbox, draft, token) =>
        {
            using var http = Client(new SentHandler { Mode = mode });
            var result = await new DraftSynchronizationService(Provider(http), store)
                .SynchronizeAsync(account, mailbox, token);

            Assert.Equal(DraftSyncStatus.MissingRemote, Assert.Single(result.Items).Status);
            var remaining = Assert.Single(await store.GetLocalDraftsAsync(token));
            Assert.Equal(draft.Body, remaining.Body);
            Assert.Equal(draft.ProviderDraftId, remaining.ProviderDraftId);
            Assert.Equal("attachment"u8.ToArray(), Assert.Single(remaining.Attachments).ContentBytes);
            Assert.Empty(await store.GetMailActionsAsync(token));
        });
    }

    [Fact]
    public async Task LookupFailurePreservesDraftAndReportsTheActualFailure()
    {
        await WithDraft(async (store, account, mailbox, draft, token) =>
        {
            using var http = Client(new SentHandler { Mode = "forbidden" });
            var result = await new DraftSynchronizationService(Provider(http), store)
                .SynchronizeAsync(account, mailbox, token);

            Assert.Equal(DraftSyncStatus.Failed, Assert.Single(result.Items).Status);
            Assert.Equal(draft.Body, Assert.Single(await store.GetLocalDraftsAsync(token)).Body);
        });
    }

    [Fact]
    public async Task EditDuringSentLookupPreventsRemoval()
    {
        await WithDraft(async (store, account, mailbox, draft, token) =>
        {
            using var http = Client(new SentHandler
            {
                BeforeTranslatedLookup = () => store.SaveLocalDraftAsync(
                    draft with { Body = "New unsynced work", UpdatedAt = draft.UpdatedAt.AddMinutes(1) }, token)
            });
            var result = await new DraftSynchronizationService(Provider(http), store)
                .SynchronizeAsync(account, mailbox, token);

            Assert.Equal(DraftSyncStatus.MissingRemote, Assert.Single(result.Items).Status);
            Assert.Equal("New unsynced work", Assert.Single(await store.GetLocalDraftsAsync(token)).Body);
        });
    }

    [Fact]
    public async Task OriginalIdStillResolvesWithoutTranslation()
    {
        var account = Account;
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Author");
        var handler = new SentHandler { Mode = "original" };
        using var http = Client(handler);

        Assert.True(await Provider(http).IsDraftSentAsync(account, mailbox, "old-id", TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    private static MailAccount Account => new("microsoft365", "account", "tenant", "author@example.test", "Author", ProviderCapabilities.Mail);
    private static HttpClient Client(SentHandler handler) => new(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    private static Microsoft365MailProvider Provider(HttpClient client) => new((_, _) => Task.FromResult("fictional-token"), client);

    private static async Task WithDraft(
        Func<EncryptedMailStore, MailAccount, Mailbox, LocalDraft, CancellationToken, Task> test, bool shared = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-draft-sent-" + Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('A', 64));
            await store.InitializeAsync(token);
            var account = Account;
            var mailbox = new Mailbox(account.AccountId, shared ? "shared+ops@example.test" : account.EmailAddress, "Author", IsShared: shared);
            var baseline = DateTimeOffset.UtcNow.AddHours(-1);
            var draft = new LocalDraft("draft", account.AccountId, mailbox.Id, "recipient@example.test", "", "",
                "Subject", "Original body", [new("file.txt", "text/plain", "attachment"u8.ToArray())], baseline,
                ProviderDraftId: "old-id", SyncedLocalUpdatedAt: baseline, SyncStatus: DraftSyncStatus.MissingRemote);
            await store.SaveLocalDraftAsync(draft, token);
            await test(store, account, mailbox, draft, token);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class SentHandler : HttpMessageHandler
    {
        public string Mode { get; init; } = "sent";
        public Func<Task>? BeforeTranslatedLookup { get; init; }
        public List<(HttpMethod Method, Uri Uri, bool ImmutableIds)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var immutable = request.Headers.TryGetValues("Prefer", out var preferences) &&
                preferences.Contains("IdType=\"ImmutableId\"");
            // The draft listing is separate from the exact-identity recovery requests.
            if (uri.AbsolutePath.EndsWith("/mailFolders/drafts/messages")) return Json("{\"value\":[]}");
            Requests.Add((request.Method, uri, immutable));
            if (uri.AbsolutePath.EndsWith("/messages/old-id"))
                return Mode == "original" ? Json("{\"isDraft\":false,\"parentFolderId\":\"sent\"}") : new(HttpStatusCode.NotFound);
            if (uri.AbsolutePath.EndsWith("/translateExchangeIds"))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("old-id", Assert.Single(body.RootElement.GetProperty("inputIds").EnumerateArray()).GetString());
                Assert.Equal("restId", body.RootElement.GetProperty("sourceIdType").GetString());
                Assert.Equal("restImmutableEntryId", body.RootElement.GetProperty("targetIdType").GetString());
                return Mode switch
                {
                    "forbidden" => new(HttpStatusCode.Forbidden),
                    "no-translation" => Json("{\"value\":[]}"),
                    "wrong-source" => Json("{\"value\":[{\"sourceId\":\"unrelated\",\"targetId\":\"stable-id\"}]}"),
                    "empty-target" => Json("{\"value\":[{\"sourceId\":\"old-id\",\"targetId\":\"\"}]}"),
                    "duplicate" => Json("{\"value\":[{\"sourceId\":\"old-id\",\"targetId\":\"stable-id\"},{\"sourceId\":\"old-id\",\"targetId\":\"other-id\"}]}"),
                    _ => Json("{\"value\":[{\"sourceId\":\"old-id\",\"targetId\":\"stable-id\"}]}")
                };
            }
            if (uri.AbsolutePath.EndsWith("/messages/stable-id"))
            {
                Assert.True(immutable);
                Assert.Contains("outlook.body-content-type=html", request.Headers.GetValues("Prefer"));
                if (BeforeTranslatedLookup is not null) await BeforeTranslatedLookup();
                return Mode switch
                {
                    "missing" => new(HttpStatusCode.NotFound),
                    "still-draft" => Json("{\"isDraft\":true,\"parentFolderId\":\"sent\"}"),
                    "other-folder" => Json("{\"isDraft\":false,\"parentFolderId\":\"inbox\"}"),
                    _ => Json("{\"isDraft\":false,\"parentFolderId\":\"sent\"}")
                };
            }
            if (uri.AbsolutePath.EndsWith("/mailFolders/sentitems")) return Json("{\"id\":\"sent\"}");
            throw new InvalidOperationException("Unexpected request: " + request.Method + " " + uri);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
}
