using System.Net;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;
using BetterMail.Microsoft365;
using Microsoft.Data.Sqlite;

namespace BetterMail.Tests;

public sealed class DeletedMailRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AlreadyDeletedMessageClearsFreshAndLegacyPausedActionsWithoutAnotherMove(bool shared, bool legacy)
    {
        await WithPendingDelete(async (store, path, account, mailbox, action, token) =>
        {
            if (legacy) await WriteLegacyRecoveryAsync(path, action.Id, token);
            var handler = new RecoveryHandler();
            using var http = Client(handler);
            IMailProvider provider = new MailProviderRouter([(account.ProviderId, Provider(http))]);
            var diagnostics = new MailActionDiagnostics(store, provider);
            Assert.Contains("found in the requested destination", await diagnostics.CheckAsync(account, mailbox, action.Id, token));
            var vm = new MainWindowViewModel(store, Path.GetDirectoryName(path)!, _ => { }, _ => { }, null, provider);
            vm.Accounts.Add(account);
            vm.Mailboxes.Add(mailbox);
            await (Task)typeof(MainWindowViewModel).GetMethod("ProcessMailActionsAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null)!;
            Assert.Empty(vm.BusyActions);
            Assert.Empty(await store.GetMailActionsAsync(token));
            var recovered = (await store.GetMailActionAsync(action.Id, token))!;
            Assert.True(recovered.Accepted);
            Assert.Equal("trash-real-id", recovered.DestinationId);
            Assert.Equal("new-id", recovered.ProviderId);
            Assert.Equal(1, recovered.FailureCount);
            Assert.Null(await store.GetMessageAsync(mailbox.Id, "old-id", token));
            Assert.Equal("trash-real-id", (await store.GetMessageAsync(mailbox.Id, "new-id", token))!.FolderId);
            Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
            Assert.DoesNotContain(handler.Requests, request => request.Uri.Query.Contains("$search"));
            var lookup = handler.Requests.First(request => request.Uri.Query.Contains("$filter"));
            Assert.Contains("internetMessageId eq '<quoted''identity@example.test>'", Uri.UnescapeDataString(lookup.Uri.Query));
            Assert.Contains(shared ? "/users/" : "/me/", lookup.Uri.AbsolutePath);
            Assert.False(await diagnostics.TryAutomaticRecoveryAsync(account, mailbox, action.Id, token));
        }, shared);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("wrong-identity")]
    [InlineData("changed-refetch")]
    [InlineData("looping-page")]
    public async Task MissingAmbiguousOrChangedMessagesKeepThePendingActionAndDoNotMutateMail(string mode)
    {
        await WithPendingDelete(async (store, path, account, mailbox, action, token) =>
        {
            using (var http = Client(new RecoveryHandler { Mode = mode }))
                Assert.False(await new MailActionDiagnostics(store, Provider(http)).TryAutomaticRecoveryAsync(account, mailbox, action.Id, token));
            var pending = Assert.Single(await store.GetMailActionsAsync(token));
            Assert.Equal("old-id", pending.ProviderId);
            Assert.Equal("deleteditems", pending.DestinationId);
            Assert.True(pending.IsRetryPaused);
            Assert.Equal(MailAction.CurrentRecoveryVersion, pending.AutomaticRecoveryVersion);
            Assert.NotNull(await store.GetMessageAsync(mailbox.Id, "old-id", token));
            // Persist the recovery claim: another startup must not repeatedly execute it.
            await using var reopened = new EncryptedMailStore(path, new string('A', 64));
            await reopened.InitializeAsync(token);
            Assert.Null(await reopened.ClaimAutomaticRecoveryAsync(action.Id, token));
        });
    }

    [Fact]
    public async Task ExactIdentityFoundOutsideDestinationRepairsAndAuthorizesOneRetry()
    {
        await WithPendingDelete(async (store, _, account, mailbox, action, token) =>
        {
            var handler = new RecoveryHandler { Mode = "source" };
            using var http = Client(handler);
            var diagnostics = new MailActionDiagnostics(store, Provider(http));
            Assert.True(await diagnostics.TryAutomaticRecoveryAsync(account, mailbox, action.Id, token));
            var retry = await store.StartMailActionAsync(action.Id, token);
            Assert.NotNull(retry);
            Assert.Equal("new-id", retry.ProviderId);
            Assert.Equal("trash-real-id", retry.DestinationId);
            Assert.Equal("inbox-real-id", retry.SourceFolderId);
            await store.FailMailActionAsync(action.Id, "Still not found", token);
            Assert.False(await diagnostics.TryAutomaticRecoveryAsync(account, mailbox, action.Id, token));
            Assert.Null(await store.StartMailActionAsync(action.Id, token));
            Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
        });
    }

    [Fact]
    public async Task FolderDiscoveryResolvesDeleteArchiveAndJunkAliases()
    {
        var account = Account;
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Author");
        using var http = Client(new RecoveryHandler());
        var folders = await Provider(http).GetFoldersAsync(account, mailbox, TestContext.Current.CancellationToken);
        foreach (var alias in new[] { "inbox", "sentitems", "deleteditems", "archive", "junkemail" })
            Assert.Equal(alias == "deleteditems" ? "trash-real-id" : alias + "-real-id", Assert.Single(folders, folder => folder.WellKnownName == alias).ProviderId);
    }

    [Fact]
    public async Task StatusCheckReportsMissingDestinationWithoutTreatingTheMessageAsMissing()
    {
        await WithPendingDelete(async (store, _, account, mailbox, action, token) =>
        {
            var handler = new RecoveryHandler { Mode = "missing-folder-current" };
            using var http = Client(handler);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => new MailActionDiagnostics(store, Provider(http)).CheckAsync(account, mailbox, action.Id, token));
            Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
            Assert.DoesNotContain(handler.Requests, request => request.Uri.Query.Contains("$filter"));
            Assert.False(Assert.Single(await store.GetMailActionsAsync(token)).AutomaticRecoveryAttempted);
        });
    }

    private static MailAccount Account => new("microsoft365", "account", "tenant", "author@example.test", "Author", ProviderCapabilities.Mail);
    private static HttpClient Client(RecoveryHandler handler) => new(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") };
    private static Microsoft365MailProvider Provider(HttpClient client) => new((_, _) => Task.FromResult("fictional-token"), client);

    private static async Task WithPendingDelete(Func<EncryptedMailStore, string, MailAccount, Mailbox, MailAction, CancellationToken, Task> test, bool shared = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-deleted-recovery-" + Guid.NewGuid());
        var path = Path.Combine(directory, "mail.db");
        var token = TestContext.Current.CancellationToken;
        try
        {
            await using var store = new EncryptedMailStore(path, new string('A', 64));
            await store.InitializeAsync(token);
            var account = Account;
            var mailbox = new Mailbox(account.AccountId, shared ? "shared+ops@example.test" : account.EmailAddress, "Author", IsShared: shared);
            using var json = JsonDocument.Parse(RecoveryHandler.Message("old-id", "inbox-real-id"));
            var message = Microsoft365MailProvider.MapMessage(mailbox, json.RootElement) with { Subject = "Old subject" };
            await store.ApplySyncPageAsync("seed", new([message], null, false), token);
            var action = await store.QueueMoveAsync(account, message, new(mailbox.Id, "deleteditems", "Deleted Items", 0, 0), token);
            await store.StartMailActionAsync(action.Id, token);
            await store.FailMailActionAsync(action.Id, "The specified object was not found in the store.", token);
            await test(store, path, account, mailbox, action, token);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task WriteLegacyRecoveryAsync(string path, string id, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        await connection.OpenAsync(token);
        await using var key = connection.CreateCommand();
        key.CommandText = $"PRAGMA key = '{new string('A', 64)}';";
        await key.ExecuteNonQueryAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE mail_actions SET payload_json = json_remove(json_set(payload_json, '$.AutomaticRecoveryAttempted', json('true')), '$.AutomaticRecoveryVersion') WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(token);
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        public string Mode { get; init; } = "destination";
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];
        public static string Message(string id, string folder, string identity = "<quoted'identity@example.test>") => JsonSerializer.Serialize(new
        { id, parentFolderId = folder, internetMessageId = identity, subject = "Changed subject", body = new { content = "Full body", contentType = "text" }, isRead = true });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add((request.Method, request.RequestUri!));
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            if (path.EndsWith("/messages/old-id"))
                return Mode == "missing-folder-current" ? Json(Message("old-id", "inbox-real-id")) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var folder = Mode == "source" ? "inbox-real-id" : "trash-real-id";
            if (path.EndsWith("/messages/new-id"))
                return Json(Message("new-id", folder, Mode == "changed-refetch" ? "<different@example.test>" : "<quoted'identity@example.test>"));
            if (uri.Query.Contains("$filter"))
            {
                Assert.DoesNotContain("$search", uri.Query);
                var next = uri.GetLeftPart(UriPartial.Path) + "?$filter=continued&$skiptoken=next";
                var continued = uri.Query.Contains("$skiptoken");
                return Mode switch
                {
                    "missing" => Json("{\"value\":[]}"),
                    "wrong-identity" => Json("{\"value\":[" + Message("new-id", folder, "<different@example.test>") + "]}"),
                    "looping-page" => Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next })),
                    "duplicate" when continued => Json("{\"value\":[" + Message("copy-id", folder) + "]}"),
                    "duplicate" => Json("{\"value\":[" + Message("new-id", folder) + "],\"@odata.nextLink\":" + JsonSerializer.Serialize(next) + "}"),
                    _ when !continued => Json("{\"value\":[],\"@odata.nextLink\":" + JsonSerializer.Serialize(next) + "}"),
                    _ => Json("{\"value\":[" + Message("new-id", folder) + "]}")
                };
            }
            if (path.EndsWith("/mailFolders"))
                return Json(JsonSerializer.Serialize(new { value = new[] { "inbox", "sentitems", "deleteditems", "archive", "junkemail" }
                    .Select(alias => new { id = alias == "deleteditems" ? "trash-real-id" : alias + "-real-id", displayName = alias, unreadItemCount = 0, totalItemCount = 1 }) }));
            if (path.Contains("/mailFolders/"))
            {
                if (Mode == "missing-folder-current") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var alias = path[(path.LastIndexOf('/') + 1)..];
                return Json(JsonSerializer.Serialize(new { id = alias == "deleteditems" ? "trash-real-id" : alias + "-real-id" }));
            }
            throw new InvalidOperationException("Unexpected provider operation: " + request.Method + " " + uri);
        }
        private static Task<HttpResponseMessage> Json(string content) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
    }
}
