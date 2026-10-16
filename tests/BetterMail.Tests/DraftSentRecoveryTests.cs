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
    public async Task CapturedStableIdRemovesSentDraftAfterRestartWithoutChangingRemoteMail(bool shared)
    {
        await WithDraft(async (store, account, mailbox, draft, path, token) =>
        {
            var handler = new SentHandler { DraftPresent = true, UpdatedAt = draft.UpdatedAt };
            using var http = Client(handler);
            IMailProvider provider = new MailProviderRouter([(account.ProviderId, Provider(http))]);
            var service = new DraftSynchronizationService(provider, store);
            Assert.Equal(DraftSyncStatus.Unchanged, Assert.Single((await service.SynchronizeAsync(account, mailbox, token)).Items).Status);
            var captured = Assert.Single(await store.GetLocalDraftSummariesAsync(token));
            Assert.Equal("old-id", captured.ProviderDraftId);
            Assert.Equal("stable-id", captured.ProviderImmutableId);

            handler.DraftPresent = false;
            handler.Requests.Clear();
            await using var restarted = new EncryptedMailStore(path, new string('A', 64));
            await restarted.InitializeAsync(token);
            var result = await new DraftSynchronizationService(provider, restarted).SynchronizeAsync(account, mailbox, token);

            Assert.Equal(DraftSyncStatus.RemovedSent, Assert.Single(result.Items).Status);
            Assert.Empty(await restarted.GetLocalDraftsAsync(token));
            Assert.Empty(await restarted.GetMailActionsAsync(token));
            Assert.True((await restarted.GetMailActionAsync("delete:" + draft.Id, token))!.Accepted);
            Assert.Equal(3, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.StartsWith(
                shared ? "/v1.0/users/shared%2Bops%40example.test/" : "/v1.0/me/", request.Uri.AbsolutePath));
            Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        }, shared, stable: false);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("still-draft")]
    [InlineData("other-folder")]
    public async Task UnconfirmedDraftIsKeptWithItsContents(string mode)
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            using var http = Client(new SentHandler { Mode = mode });
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.MissingRemote, Assert.Single(result.Items).Status);
            var remaining = Assert.Single(await store.GetLocalDraftsAsync(token));
            Assert.Equal(draft.Body, remaining.Body);
            Assert.Equal(draft.ProviderDraftId, remaining.ProviderDraftId);
            Assert.Equal("attachment"u8.ToArray(), Assert.Single(remaining.Attachments).ContentBytes);
            Assert.Empty(await store.GetMailActionsAsync(token));
        });
    }

    [Fact]
    public async Task ConflictedLegacyDraftCapturesIdentityWithoutAdvancingSynchronizationBaseline()
    {
        await WithDraft(async (store, account, mailbox, draft, path, token) =>
        {
            var edited = draft with { Body = "Unsynced work", UpdatedAt = draft.UpdatedAt.AddMinutes(2), ProviderETag = "original-version" };
            await store.SaveLocalDraftAsync(edited, token);
            var handler = new SentHandler { DraftPresent = true, UpdatedAt = draft.UpdatedAt.AddMinutes(1) };
            using var http = Client(handler);
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.Conflict, Assert.Single(result.Items).Status);

            await using var restarted = new EncryptedMailStore(path, new string('A', 64));
            await restarted.InitializeAsync(token);
            var captured = Assert.Single(await restarted.GetLocalDraftsAsync(token));
            Assert.Equal("stable-id", captured.ProviderImmutableId);
            Assert.Equal(edited.Body, captured.Body);
            Assert.Equal(edited.UpdatedAt, captured.UpdatedAt);
            Assert.Equal(draft.SyncedLocalUpdatedAt, captured.SyncedLocalUpdatedAt);
            Assert.Equal(draft.ProviderUpdatedAt, captured.ProviderUpdatedAt);
            Assert.Equal(edited.ProviderETag, captured.ProviderETag);
        }, stable: false);
    }

    [Theory]
    [InlineData("no-translation")]
    [InlineData("wrong-source")]
    [InlineData("duplicate")]
    [InlineData("empty-target")]
    [InlineData("translation-missing")]
    public async Task UnavailableTranslationDoesNotInventAnIdentityOrConvertAnAlreadyMissingId(string mode)
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            var handler = new SentHandler { Mode = mode, DraftPresent = true, UpdatedAt = draft.UpdatedAt };
            using var http = Client(handler);
            var service = new DraftSynchronizationService(Provider(http), store);
            await service.SynchronizeAsync(account, mailbox, token);
            Assert.Null(Assert.Single(await store.GetLocalDraftsAsync(token)).ProviderImmutableId);
            handler.DraftPresent = false;
            handler.Requests.Clear();
            var result = await service.SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.MissingRemote, Assert.Single(result.Items).Status);
            Assert.Equal(draft.Body, Assert.Single(await store.GetLocalDraftsAsync(token)).Body);
            Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        }, stable: false);
    }

    [Theory]
    [InlineData("throttled")]
    [InlineData("server-transient")]
    [InlineData("network-transient")]
    public async Task IdTranslationRetriesTransientReadFailures(string mode)
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            var handler = new SentHandler { Mode = mode, DraftPresent = true, UpdatedAt = draft.UpdatedAt };
            using var http = Client(handler);
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.Unchanged, Assert.Single(result.Items).Status);
            Assert.Equal("stable-id", Assert.Single(await store.GetLocalDraftsAsync(token)).ProviderImmutableId);
            Assert.Equal(2, handler.TranslationAttempts);
            Assert.Equal(0, handler.SendCount);
        }, stable: false);
    }

    [Fact]
    public async Task LookupFailurePreservesDraftAndReportsTheActualFailure()
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            using var http = Client(new SentHandler { Mode = "forbidden" });
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.Failed, Assert.Single(result.Items).Status);
            Assert.Equal(draft.Body, Assert.Single(await store.GetLocalDraftsAsync(token)).Body);
        });
    }

    [Fact]
    public async Task EditDuringSentLookupPreventsRemoval()
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            using var http = Client(new SentHandler
            {
                BeforeStableLookup = () => store.SaveLocalDraftAsync(
                    draft with { Body = "New unsynced work", UpdatedAt = draft.UpdatedAt.AddMinutes(1) }, token)
            });
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.MissingRemote, Assert.Single(result.Items).Status);
            Assert.Equal("New unsynced work", Assert.Single(await store.GetLocalDraftsAsync(token)).Body);
        });
    }

    [Fact]
    public async Task OutboxPersistsStableIdentityBeforeSendingAndRecoversLostAcknowledgementAfterRestart()
    {
        await WithDraft(async (store, account, mailbox, draft, path, token) =>
        {
            await store.SaveLocalDraftAsync(draft with { IsQueued = true, Attachments = [] }, token);
            var handler = new SentHandler
            {
                DraftPresent = true,
                UpdatedAt = draft.UpdatedAt,
                LoseSendAcknowledgement = true,
                BeforeSend = async () => Assert.Equal("stable-id", (await store.GetLocalDraftAsync(draft.Id, token))!.ProviderImmutableId)
            };
            using var http = Client(handler);
            var provider = Provider(http);
            await new OutboxService(provider, store).ProcessAsync(account, mailbox, draft.Id, token);
            Assert.True(Assert.Single(await store.GetMailActionsAsync(token)).NeedsSendReview);
            Assert.Equal(1, handler.SendCount);

            await using var restarted = new EncryptedMailStore(path, new string('A', 64));
            await restarted.InitializeAsync(token);
            await new OutboxService(provider, restarted).ProcessAsync(account, mailbox, draft.Id, token);
            Assert.Empty(await restarted.GetLocalDraftsAsync(token));
            Assert.Empty(await restarted.GetMailActionsAsync(token));
            Assert.Equal(1, handler.SendCount);
        }, stable: false);
    }

    [Fact]
    public async Task ReturningUnconfirmedSendToDraftClearsBothProviderIdentities()
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            await store.SaveLocalDraftAsync(draft with { IsQueued = true, Attachments = [] }, token);
            var handler = new SentHandler { DraftPresent = true, LoseSendAcknowledgement = true };
            using var http = Client(handler);
            var service = new OutboxService(Provider(http), store);
            await service.ProcessAsync(account, mailbox, draft.Id, token);
            Assert.Equal("stable-id", (await store.GetLocalDraftAsync(draft.Id, token))!.ProviderImmutableId);
            Assert.True(await service.ReturnToDraftAsync(account, mailbox, "send:" + draft.Id, token));
            var returned = Assert.Single(await store.GetLocalDraftsAsync(token));
            Assert.Null(returned.ProviderDraftId);
            Assert.Null(returned.ProviderImmutableId);
            Assert.Equal(draft.Body, returned.Body);
            Assert.Equal(1, handler.SendCount);
        }, stable: false);
    }

    [Fact]
    public async Task LegacyIdentityCanStillConfirmSentWhenItRemainsResolvable()
    {
        await WithDraft(async (store, account, mailbox, _, _, token) =>
        {
            var handler = new SentHandler { Mode = "original" };
            using var http = Client(handler);
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.RemovedSent, Assert.Single(result.Items).Status);
            Assert.Empty(await store.GetLocalDraftsAsync(token));
            Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        }, stable: false);
    }

    [Fact]
    public async Task CreatingDraftCapturesIdentityBeforeSend()
    {
        var account = Account;
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Author");
        var handler = new SentHandler();
        using var http = Client(handler);
        var provider = Provider(http);
        var token = TestContext.Current.CancellationToken;
        var draft = await provider.CreateDraftAsync(account, mailbox, new("Subject", [], "Body", false), token);
        Assert.Equal("old-id", draft.ProviderId);
        Assert.Equal("stable-id", draft.ProviderImmutableId);
        await provider.SendDraftAsync(account, mailbox, draft.ProviderImmutableId!, token);
        Assert.True(await provider.IsDraftSentAsync(account, mailbox, draft.ProviderImmutableId!, token));
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedRegularIdRebindsExistingDraftWithoutImportingOrCreatingAnother(bool conflict)
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            var edited = conflict ? draft with { Body = "Unsynced work", UpdatedAt = draft.UpdatedAt.AddMinutes(2) } : draft;
            await store.SaveLocalDraftAsync(edited, token);
            var handler = new SentHandler
            {
                DraftPresent = true, RegularId = "moved-id",
                UpdatedAt = conflict ? draft.UpdatedAt.AddMinutes(1) : draft.UpdatedAt
            };
            using var http = Client(handler);
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(conflict ? DraftSyncStatus.Conflict : DraftSyncStatus.Unchanged, Assert.Single(result.Items).Status);
            var rebound = Assert.Single(await store.GetLocalDraftsAsync(token));
            Assert.Equal(draft.Id, rebound.Id);
            Assert.Equal("moved-id", rebound.ProviderDraftId);
            Assert.Equal("stable-id", rebound.ProviderImmutableId);
            Assert.Equal(edited.Body, rebound.Body);
            Assert.Equal(draft.SyncedLocalUpdatedAt, rebound.SyncedLocalUpdatedAt);
            Assert.Equal(draft.ProviderUpdatedAt, rebound.ProviderUpdatedAt);
            Assert.Equal(0, handler.CreateCount);
            Assert.Equal(0, handler.SendCount);
        });
    }

    [Fact]
    public async Task AmbiguousStableIdentityDoesNotRebindOrImportDuplicates()
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            var handler = new SentHandler { DraftPresent = true, RegularId = "moved-id", Ambiguous = true };
            using var http = Client(handler);
            var result = await new DraftSynchronizationService(Provider(http), store).SynchronizeAsync(account, mailbox, token);
            Assert.Equal(DraftSyncStatus.Conflict, Assert.Single(result.Items).Status);
            Assert.Equal(draft.ProviderDraftId, Assert.Single(await store.GetLocalDraftsAsync(token)).ProviderDraftId);
            Assert.Equal(0, handler.CreateCount);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningUnconfirmedMovedDraftPreservesIdentityOrRejectsAmbiguity(bool ambiguous)
    {
        await WithDraft(async (store, account, mailbox, draft, _, token) =>
        {
            await store.SaveLocalDraftAsync(draft with { IsQueued = true, Attachments = [] }, token);
            var handler = new SentHandler { DraftPresent = true, UpdatedAt = draft.UpdatedAt, LoseSendAcknowledgement = true };
            using var http = Client(handler);
            var provider = Provider(http);
            var service = new OutboxService(provider, store);
            await service.ProcessAsync(account, mailbox, draft.Id, token);
            handler.DraftPresent = true;
            handler.RegularId = "moved-id";
            handler.Ambiguous = ambiguous;
            if (ambiguous)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReturnToDraftAsync(account, mailbox, "send:" + draft.Id, token));
                Assert.True(Assert.Single(await store.GetLocalDraftsAsync(token)).IsQueued);
                Assert.True(Assert.Single(await store.GetMailActionsAsync(token)).NeedsSendReview);
                return;
            }
            Assert.True(await service.ReturnToDraftAsync(account, mailbox, "send:" + draft.Id, token));
            var returned = Assert.Single(await store.GetLocalDraftsAsync(token));
            Assert.False(returned.IsQueued);
            Assert.Equal("moved-id", returned.ProviderDraftId);
            Assert.Equal("stable-id", returned.ProviderImmutableId);
            await new DraftSynchronizationService(provider, store).SynchronizeAsync(account, mailbox, token);
            Assert.Single(await store.GetLocalDraftsAsync(token));
            Assert.Equal(0, handler.CreateCount);
            Assert.Equal(1, handler.SendCount);
        });
    }

    private static MailAccount Account => new("microsoft365", "account", "tenant", "author@example.test", "Author", ProviderCapabilities.Mail);
    private static HttpClient Client(SentHandler handler) => new(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    private static Microsoft365MailProvider Provider(HttpClient client) => new((_, _) => Task.FromResult("fictional-token"), client);

    private static async Task WithDraft(
        Func<EncryptedMailStore, MailAccount, Mailbox, LocalDraft, string, CancellationToken, Task> test, bool shared = false, bool stable = true)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-draft-sent-" + Guid.NewGuid());
        var path = Path.Combine(directory, "mail.db");
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var store = new EncryptedMailStore(path, new string('A', 64));
            await store.InitializeAsync(token);
            var account = Account;
            var mailbox = new Mailbox(account.AccountId, shared ? "shared+ops@example.test" : account.EmailAddress, "Author", IsShared: shared);
            var baseline = DateTimeOffset.UtcNow.AddHours(-1);
            var draft = new LocalDraft("draft", account.AccountId, mailbox.Id, "recipient@example.test", "", "",
                "Subject", "Original body", [new("file.txt", "text/plain", "attachment"u8.ToArray())], baseline,
                ProviderDraftId: "old-id", SyncedLocalUpdatedAt: baseline, ProviderUpdatedAt: baseline,
                SyncStatus: DraftSyncStatus.MissingRemote, ProviderImmutableId: stable ? "stable-id" : null);
            await store.SaveLocalDraftAsync(draft, token);
            await test(store, account, mailbox, draft, path, token);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class SentHandler : HttpMessageHandler
    {
        public string Mode { get; init; } = "sent";
        public bool DraftPresent { get; set; }
        public string RegularId { get; set; } = "old-id";
        public bool Ambiguous { get; set; }
        public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
        public bool LoseSendAcknowledgement { get; init; }
        public int SendCount { get; private set; }
        public int CreateCount { get; private set; }
        public int TranslationAttempts { get; private set; }
        public Func<Task>? BeforeStableLookup { get; init; }
        public Func<Task>? BeforeSend { get; init; }
        public List<(HttpMethod Method, Uri Uri, bool ImmutableIds)> Requests { get; } = [];

        private string DraftJson(string id) => JsonSerializer.Serialize(new
        {
            id, isDraft = true, parentFolderId = "drafts", subject = "Subject", lastModifiedDateTime = UpdatedAt,
            toRecipients = new[] { new { emailAddress = new { name = "", address = "recipient@example.test" } } },
            ccRecipients = Array.Empty<object>(), bccRecipients = Array.Empty<object>(),
            body = new { contentType = "text", content = "Original body" }, hasAttachments = false
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var immutable = request.Headers.TryGetValues("Prefer", out var preferences) && preferences.Contains("IdType=\"ImmutableId\"");
            Requests.Add((request.Method, uri, immutable));
            if (uri.AbsolutePath.EndsWith("/mailFolders/drafts/messages"))
                return Json(DraftPresent ? "{\"value\":[" + DraftJson(RegularId) + (Ambiguous ? "," + DraftJson("other-id") : "") + "]}" : "{\"value\":[]}");
            if (uri.AbsolutePath.EndsWith("/messages") && request.Method == HttpMethod.Post)
            {
                DraftPresent = true;
                CreateCount++;
                return Json(DraftJson(RegularId));
            }
            if (uri.AbsolutePath.EndsWith("/messages/" + RegularId + "/attachments")) return Json("{\"value\":[]}");
            if (uri.AbsolutePath.EndsWith("/messages/" + RegularId))
            {
                if (Mode == "original") return Json("{\"isDraft\":false,\"parentFolderId\":\"sent\"}");
                if (!DraftPresent) return new(HttpStatusCode.NotFound);
                if (request.Method == HttpMethod.Patch) return new(HttpStatusCode.OK);
                Assert.True(immutable);
                return Json(DraftJson("stable-id"));
            }
            if (uri.AbsolutePath.EndsWith("/translateExchangeIds"))
            {
                Assert.True(DraftPresent, "A stale ID cannot be translated after it stops resolving.");
                Assert.Equal(HttpMethod.Post, request.Method);
                TranslationAttempts++;
                if (TranslationAttempts == 1 && Mode is "throttled" or "server-transient" or "network-transient")
                {
                    if (Mode == "network-transient") throw new HttpRequestException("Temporary connection failure");
                    var transient = new HttpResponseMessage(Mode == "throttled" ? HttpStatusCode.TooManyRequests : HttpStatusCode.ServiceUnavailable);
                    transient.Headers.RetryAfter = new(TimeSpan.Zero);
                    return transient;
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var ids = body.RootElement.GetProperty("inputIds").EnumerateArray().Select(item => item.GetString()).ToArray();
                Assert.Equal(Ambiguous ? 2 : 1, ids.Length);
                Assert.Contains(RegularId, ids);
                Assert.Equal("restId", body.RootElement.GetProperty("sourceIdType").GetString());
                Assert.Equal("restImmutableEntryId", body.RootElement.GetProperty("targetIdType").GetString());
                return Mode switch
                {
                    "translation-missing" => new(HttpStatusCode.NotFound),
                    "no-translation" => Json("{\"value\":[]}"),
                    "wrong-source" => Json("{\"value\":[{\"sourceId\":\"unrelated\",\"targetId\":\"stable-id\"}]}"),
                    "empty-target" => Json("{\"value\":[{\"sourceId\":\"old-id\",\"targetId\":\"\"}]}"),
                    "duplicate" => Json("{\"value\":[{\"sourceId\":\"old-id\",\"targetId\":\"stable-id\"},{\"sourceId\":\"old-id\",\"targetId\":\"other-id\"}]}"),
                    _ => Json(JsonSerializer.Serialize(new { value = ids.Select(sourceId => new { sourceId, targetId = "stable-id" }) }))
                };
            }
            if (uri.AbsolutePath.EndsWith("/messages/stable-id/send"))
            {
                Assert.True(immutable);
                if (BeforeSend is not null) await BeforeSend();
                SendCount++;
                DraftPresent = false;
                if (LoseSendAcknowledgement) throw new HttpRequestException("Lost acknowledgement");
                return new(HttpStatusCode.Accepted);
            }
            if (uri.AbsolutePath.EndsWith("/messages/stable-id"))
            {
                Assert.True(immutable);
                if (BeforeStableLookup is not null) await BeforeStableLookup();
                return Mode switch
                {
                    "forbidden" => new(HttpStatusCode.Forbidden),
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
