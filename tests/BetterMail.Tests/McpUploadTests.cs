using System.Security.Cryptography;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;
using ModelContextProtocol;

namespace BetterMail.Tests;

public sealed class McpUploadTests
{
    [Fact]
    public async Task ChunksRejectGapsDifferentRetriesAndWrongOwnersThenCompleteExactlyOnce()
    {
        await WithStore(async (store, tools, draft, _) =>
        {
            byte[] bytes = [1, 2, 3, 4];
            var upload = await tools.BeginAttachmentUpload(draft.MailboxId, draft.Id, draft.UpdatedAt, "file.bin", "application/octet-stream", 4, Hash(bytes));
            await Assert.ThrowsAsync<McpException>(() => tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 1, "AQI=", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<McpException>(() => tools.UploadAttachmentChunk("hidden", upload.Id, 0, "AQI=", TestContext.Current.CancellationToken));
            Assert.Equal(2, await tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 0, "AQI=", TestContext.Current.CancellationToken));
            Assert.Equal(2, await tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 0, "AQI=", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<McpException>(() => tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 0, "AwQ=", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<McpException>(() => tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            await tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 2, "AwQ=", TestContext.Current.CancellationToken);
            var version = await tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken);
            Assert.Equal(version, await tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            var saved = (await store.GetLocalDraftAsync(draft.Id))!;
            Assert.Equal(bytes, Assert.Single(saved.Attachments).ContentBytes);
            Assert.False(saved.IsQueued);
            var content = JsonSerializer.Serialize(await tools.ReadDraftAttachment(draft.MailboxId, draft.Id, version, 0));
            Assert.Contains(Convert.ToBase64String(bytes), content);
            await tools.RemoveDraftAttachment(draft.MailboxId, draft.Id, version, 0);
            Assert.Empty((await store.GetLocalDraftAsync(draft.Id))!.Attachments);
        });
    }

    [Fact]
    public async Task HashMismatchAndConcurrentDraftEditPreserveDraft()
    {
        await WithStore(async (store, tools, draft, _) =>
        {
            var upload = await tools.BeginAttachmentUpload(draft.MailboxId, draft.Id, draft.UpdatedAt, "file", "text/plain", 1, Hash([2]));
            await tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 0, "AQ==", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<McpException>(() => tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            await tools.UpdateDraft(draft.MailboxId, draft.Id, draft.UpdatedAt, subject: "New subject");
            await Assert.ThrowsAsync<McpException>(() => tools.UpdateDraft(draft.MailboxId, draft.Id, draft.UpdatedAt, subject: "Stale"));
            await Assert.ThrowsAsync<McpException>(() => tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            var saved = (await store.GetLocalDraftAsync(draft.Id))!;
            Assert.Equal("New subject", saved.Subject);
            Assert.Empty(saved.Attachments);
        });
    }

    [Fact]
    public async Task LargeAttachmentRequiresIndependentDrivePermissionAndSharesOnce()
    {
        await WithStore(async (store, tools, draft, files) =>
        {
            // Existing attachment fills the budget, so even one more byte takes the Drive path.
            draft = draft with { Attachments = [new("existing", "application/octet-stream", new byte[LargeAttachmentPolicy.DirectAttachmentBudgetBytes])] };
            await store.SaveLocalDraftAsync(draft);
            var upload = await tools.BeginAttachmentUpload(draft.MailboxId, draft.Id, draft.UpdatedAt, "<report>.txt", "text/plain", 1, Hash([1]));
            await tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 0, "AQ==", TestContext.Current.CancellationToken);
            var noDrive = new McpMailTools(store, () => new(Enabled: true, AllowWrites: true, MailboxIds: [draft.MailboxId]), () => Task.CompletedTask, (_, _, _) => throw new Exception("Must not send"), filesProvider: () => files);
            await Assert.ThrowsAsync<McpException>(() => noDrive.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            Assert.Equal(0, files.Uploads);
            var version = await tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken);
            Assert.Equal(version, await tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            Assert.Equal(1, files.Uploads);
            Assert.Equal(1, files.Shares);
            Assert.Equal(AttachmentDriveSaveViewModel.NormalizeFileName("<report>.txt"), files.UploadedName);
            Assert.Equal("anonymous", files.Scope);
            Assert.InRange(files.Expiry, DateTimeOffset.UtcNow.AddYears(1).AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1).AddMinutes(1));
            var saved = (await store.GetLocalDraftAsync(draft.Id))!;
            Assert.Contains("&lt;report&gt;.txt", saved.Body);
            Assert.Contains("https://example.com/shared", saved.Body);
            Assert.Single(saved.Attachments);
            Assert.Equal("complete", (await tools.GetAttachmentUpload(draft.MailboxId, upload.Id)).State);
        });
    }

    [Fact]
    public async Task ShareFailureRetainsFileWithoutChangingDraftOrUploadingTwice()
    {
        await WithStore(async (store, tools, draft, files) =>
        {
            draft = draft with { Attachments = [new("existing", "application/octet-stream", new byte[LargeAttachmentPolicy.DirectAttachmentBudgetBytes])] };
            await store.SaveLocalDraftAsync(draft);
            files.FailShare = true;
            var upload = await tools.BeginAttachmentUpload(draft.MailboxId, draft.Id, draft.UpdatedAt, "report", "text/plain", 1, Hash([1]));
            await tools.UploadAttachmentChunk(draft.MailboxId, upload.Id, 0, "AQ==", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<McpException>(() => tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<McpException>(() => tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            Assert.Equal(1, files.Uploads);
            Assert.Equal(draft.Body, (await store.GetLocalDraftAsync(draft.Id))!.Body);
            var status = await tools.GetAttachmentUpload(draft.MailboxId, upload.Id);
            Assert.Equal("sharing", status.State);
            Assert.NotNull(status.File);
        });
    }

    [Fact]
    public async Task DriveUploadRetriesReturnRecordedResultAndRevocationBlocksAccess()
    {
        await WithStore(async (store, tools, draft, files) =>
        {
            var upload = await tools.BeginDriveUpload("microsoft365:account", "report", "text/plain", 1, Hash([1]));
            await tools.UploadDriveChunk("microsoft365:account", upload.Id, 0, "AQ==");
            var file = await tools.CompleteDriveUpload("microsoft365:account", upload.Id);
            Assert.Equal(file, await tools.CompleteDriveUpload("microsoft365:account", upload.Id));
            Assert.Equal(1, files.Uploads);
            Assert.Equal("complete", (await tools.GetDriveUpload("microsoft365:account", upload.Id)).State);
            var revoked = new McpMailTools(store, () => new(Enabled: true), () => Task.CompletedTask, (_, _, _) => Task.CompletedTask, filesProvider: () => files);
            await Assert.ThrowsAsync<McpException>(() => revoked.GetDriveUpload("microsoft365:account", upload.Id));
            Assert.Empty((await store.GetLocalDraftAsync(draft.Id))!.Attachments);
        });
    }

    [Fact]
    public void AttachmentBudgetHasExactBoundaryAndEscapesLinks()
    {
        Assert.False(LargeAttachmentPolicy.UseDrive(LargeAttachmentPolicy.DirectAttachmentBudgetBytes, []));
        Assert.True(LargeAttachmentPolicy.UseDrive(LargeAttachmentPolicy.DirectAttachmentBudgetBytes + 1, []));
        Assert.Contains("&lt;script&gt;", LargeAttachmentPolicy.LinkHtml("<script>", new("p", new("https://example.com/"), DateTimeOffset.UtcNow.AddDays(1), "anonymous")));
    }

    [Theory]
    [InlineData("edit", "anonymous", 10)]
    [InlineData("view", "organization", 10)]
    [InlineData("view", "anonymous", -1)]
    [InlineData("view", "anonymous", 400)]
    [InlineData("view", "anonymous", 0)]
    public void GraphRejectsBroaderPermanentOrExpiredSharing(string type, string scope, int days)
    {
        var permission = JsonSerializer.SerializeToElement(new { id = "p", link = new { type, scope, webUrl = "https://example.com/file" }, expirationDateTime = days == 0 ? (string?)null : DateTimeOffset.UtcNow.AddDays(days).ToString("O") });
        Assert.Throws<InvalidOperationException>(() => BetterMail.Microsoft365.Microsoft365WorkspaceProvider.ValidateReadOnlyLink(permission, "anonymous", DateTimeOffset.UtcNow.AddYears(1)));
    }

    [Fact]
    public void GraphUsesActualShorterExpirationAndRequestsReadOnlyAnonymousLink()
    {
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var permission = JsonSerializer.SerializeToElement(new { id = "p", link = new { type = "view", scope = "anonymous", webUrl = "https://example.com/file" }, expirationDateTime = expiry.ToString("O") });
        Assert.Equal(expiry, BetterMail.Microsoft365.Microsoft365WorkspaceProvider.ValidateReadOnlyLink(permission, "anonymous", DateTimeOffset.UtcNow.AddYears(1)).ExpiresAt);
        var payload = JsonSerializer.SerializeToElement(BetterMail.Microsoft365.Microsoft365WorkspaceProvider.ReadOnlyLinkPayload(expiry, "anonymous"));
        Assert.Equal("view", payload.GetProperty("type").GetString());
        Assert.Equal("anonymous", payload.GetProperty("scope").GetString());
        Assert.True(payload.GetProperty("retainInheritedPermissions").GetBoolean());
    }

    [Fact]
    public async Task QueuedDraftRejectsEditsAndUploadCompletion()
    {
        await WithStore(async (store, tools, draft, _) =>
        {
            var upload = await tools.BeginAttachmentUpload(draft.MailboxId, draft.Id, draft.UpdatedAt, "empty", "text/plain", 0, Hash([]));
            await store.SaveLocalDraftAsync(draft with { IsQueued = true });
            await Assert.ThrowsAsync<McpException>(() => tools.UpdateDraft(draft.MailboxId, draft.Id, draft.UpdatedAt, body: "Overwrite"));
            await Assert.ThrowsAsync<McpException>(() => tools.CompleteAttachmentUpload(draft.MailboxId, upload.Id, TestContext.Current.CancellationToken));
            Assert.Empty((await store.GetLocalDraftAsync(draft.Id))!.Attachments);
        });
    }

    [Fact]
    public async Task UploadQuotaCancellationAndWrongOwnerAreEnforced()
    {
        await WithStore(async (store, tools, draft, _) =>
        {
            var uploads = new List<McpAttachmentUpload>();
            for (var i = 0; i < 4; i++) uploads.Add(await tools.BeginAttachmentUpload(draft.MailboxId, draft.Id, draft.UpdatedAt, "empty", "text/plain", 0, Hash([])));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.BeginMcpAttachmentUploadAsync(draft.MailboxId, draft.Id, draft.UpdatedAt, "empty", "text/plain", 0, Hash([]), TestContext.Current.CancellationToken));
            await store.CancelMcpAttachmentUploadAsync("wrong-owner", uploads[0].Id);
            Assert.NotNull(await tools.GetAttachmentUpload(draft.MailboxId, uploads[0].Id));
            await tools.CancelAttachmentUpload(draft.MailboxId, uploads[0].Id);
            await Assert.ThrowsAsync<McpException>(() => tools.GetAttachmentUpload(draft.MailboxId, uploads[0].Id));
            await tools.BeginAttachmentUpload(draft.MailboxId, draft.Id, draft.UpdatedAt, "empty", "text/plain", 0, Hash([]));
        });
    }

    [Theory]
    [InlineData("report:.txt", "text/plain")]
    [InlineData("report.txt", "not a MIME type")]
    public async Task InvalidDriveMetadataNeverClaimsARemoteAttempt(string name, string contentType)
    {
        await WithStore(async (store, tools, _, files) =>
        {
            await Assert.ThrowsAsync<McpException>(() => tools.BeginDriveUpload("microsoft365:account", name, contentType, 1, Hash([1]), replaceItemId: "existing", expectedETag: "etag"));
            // Also protect sessions staged by a previous app version.
            var target = JsonSerializer.Serialize(new { ParentId = (string?)null, ReplaceItemId = "existing", ExpectedETag = "etag" });
            var upload = await store.BeginMcpAttachmentUploadAsync("drive:microsoft365:account", target, DateTimeOffset.MinValue, name, contentType, 1, Hash([1]));
            await store.WriteMcpAttachmentChunkAsync("drive:microsoft365:account", upload.Id, 0, [1]);
            await Assert.ThrowsAsync<McpException>(() => tools.CompleteDriveUpload("microsoft365:account", upload.Id));
            Assert.Equal("ready", (await tools.GetDriveUpload("microsoft365:account", upload.Id)).State);
            Assert.Equal(0, files.Uploads);
        });
    }

    [Fact]
    public async Task FailedFinalTransitionReportsRemoteFileAndSuccessfulRetryClearsChunks()
    {
        await WithStore(async (store, tools, _, files) =>
        {
            var upload = await tools.BeginDriveUpload("microsoft365:account", "report", "text/plain", 1, Hash([1]));
            await tools.UploadDriveChunk("microsoft365:account", upload.Id, 0, "AQ==");
            var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(EncryptedMailStore)
                .GetField("_connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            await using var command = connection.CreateCommand();
            // Simulate a lost compare-and-set exactly at the last transition, after upload.
            command.CommandText = "CREATE TRIGGER reject_completion BEFORE UPDATE OF remote_state ON mcp_attachment_uploads WHEN NEW.remote_state='complete' BEGIN SELECT RAISE(IGNORE); END;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            var error = await Assert.ThrowsAsync<McpException>(() => tools.CompleteDriveUpload("microsoft365:account", upload.Id));
            Assert.Contains("completed result could not be recorded", error.Message);
            Assert.Contains("file", error.Message);
            command.CommandText = "SELECT COUNT(*) FROM mcp_attachment_chunks;";
            Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            command.CommandText = "DROP TRIGGER reject_completion;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            var result = await tools.CompleteDriveUpload("microsoft365:account", upload.Id);
            Assert.Equal(result, await tools.CompleteDriveUpload("microsoft365:account", upload.Id));
            Assert.Equal(1, files.Uploads);
            command.CommandText = "SELECT COUNT(*) FROM mcp_attachment_chunks;";
            Assert.Equal(0L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Equal("complete", (await tools.GetDriveUpload("microsoft365:account", upload.Id)).State);
        });
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task WithStore(Func<EncryptedMailStore, McpMailTools, LocalDraft, Files, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync();
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail | ProviderCapabilities.Files);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account);
            await store.SaveMailboxAsync(mailbox);
            var draft = new LocalDraft("draft", account.AccountId, mailbox.Id, "to@example.com", "", "", "Subject", "Body", [], DateTimeOffset.UtcNow);
            await store.SaveLocalDraftAsync(draft);
            var files = new Files();
            var tools = new McpMailTools(store, () => new(Enabled: true, AllowWrites: true, MailboxIds: [mailbox.Id], DriveAccountIds: ["microsoft365:account"]), () => Task.CompletedTask, (_, _, _) => throw new Exception("Must not send"), filesProvider: () => files);
            await test(store, tools, draft, files);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Files : IFilesProvider
    {
        public int Uploads, Shares;
        public string? UploadedName;
        public bool FailShare;
        public string? Scope;
        public DateTimeOffset Expiry;
        public Task<IReadOnlyList<CloudFile>> SearchFilesAsync(MailAccount account, string query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudFile>>([]);
        public Task<IReadOnlyList<CloudDriveItem>> GetDriveItemsAsync(MailAccount account, CloudDriveItem? parent = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<CloudDriveItem>>([new("folder", "Attachments", 0, true, null, null, account.AccountId, account.ProviderId)]);
        public Task<CloudDriveItem> GetDriveItemAsync(MailAccount account, string itemId, CancellationToken cancellationToken = default) => Task.FromResult(new CloudDriveItem(itemId, "Attachments", 0, true, null, null, account.AccountId, account.ProviderId));
        public async Task<CloudDriveItem> UploadFileAsync(MailAccount account, CloudDriveItem? parent, string name, Stream content, long contentLength, string? contentType = null, CancellationToken cancellationToken = default)
        {
            Assert.Equal(AttachmentDriveSaveViewModel.NormalizeFileName(name), name);
            UploadedName = name;
            Uploads++;
            using var bytes = new MemoryStream();
            await content.CopyToAsync(bytes, cancellationToken);
            Assert.Equal(contentLength, bytes.Length);
            return new("file", name, contentLength, false, parent?.ProviderId, null, account.AccountId, account.ProviderId);
        }
        public Task<DriveShareLink> CreateReadOnlyLinkAsync(MailAccount account, CloudDriveItem item, DateTimeOffset expiresAt, string scope, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
        {
            Shares++; Scope = scope; Expiry = expiresAt;
            if (FailShare) throw new InvalidOperationException("Sharing prohibited by account policy");
            return Task.FromResult(new DriveShareLink("permission", new("https://example.com/shared"), expiresAt, scope));
        }
    }
}
