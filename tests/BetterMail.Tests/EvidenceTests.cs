using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterMail.App;
using BetterMail.Core;
using ModelContextProtocol;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BetterMail.Tests;

public sealed class EvidenceTests
{
    [Fact]
    public async Task IndexedEvidenceIsScopedPagedDeduplicatedAndExportedWithOriginalBytes()
    {
        await WithStore(async (store, account, mailbox, hidden, provider, service) =>
        {
            var token = TestContext.Current.CancellationToken;
            await store.ApplySyncPageAsync("inline", new([Message(mailbox.Id, "one") with { HasAttachments = false }], null, false), token);
            var one = await service.IndexMessageAsync(mailbox.Id, "one", cancellationToken: token);
            var two = await service.IndexMessageAsync(mailbox.Id, "two", cancellationToken: token);
            var secret = await service.IndexMessageAsync(hidden.Id, "hidden", cancellationToken: token);
            Assert.Equal("Complete", one.AttachmentState);
            var first = await service.SearchAttachmentsAsync([mailbox.Id], "REG123", limit: 1, cancellationToken: token);
            Assert.Single(first.Items);
            Assert.True(first.HasMore);
            var second = await service.SearchAttachmentsAsync([mailbox.Id], "REG123", first.NextCursor, 1, token);
            Assert.Single(second.Items);
            Assert.False(second.HasMore);
            Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
            Assert.All(first.Items.Concat(second.Items), item => Assert.Equal(mailbox.Id, item.Message.Message.MailboxId));
            await Assert.ThrowsAsync<EvidenceException>(() => service.SearchAttachmentsAsync([hidden.Id], "REG123", first.NextCursor, 1, token));
            Assert.Equal(2, first.Completeness.Documents);
            Assert.Equal(2, first.Completeness.SearchableDocuments);
            Assert.False(first.Completeness.Complete);
            var duplicate = Assert.Single((await store.FindEvidenceDuplicatesAsync([mailbox.Id], cancellationToken: token)).Items, item => item.ExactBytes);
            Assert.Equal(2, duplicate.Occurrences);
            Assert.DoesNotContain(secret.Id, duplicate.RecordIds);

            var document = first.Items[0].Document!;
            Assert.True(EvidenceLink.TryParse(document.SourceLink, out var resolved));
            Assert.Equal(document.Id, resolved);
            var reviewed = await service.ReviewAsync(document.Id, DocumentKind.Registration, VerificationStatus.Verified,
                "Reviewer", "Compared with the original", cancellationToken: token);
            Assert.NotNull(reviewed.ReviewedAt);
            await service.RetryDocumentAsync(document.Id, cancellationToken: token);
            Assert.Equal(VerificationStatus.Verified, (await store.GetEvidenceDocumentAsync(document.Id, token))!.Verification);
            Assert.Single((await store.SearchEvidenceDocumentsAsync([mailbox.Id], kind: DocumentKind.Registration,
                verification: VerificationStatus.Verified, cancellationToken: token)).Items);

            var exported = await service.ExportAsync([one.Id, two.Id], id => Assert.Equal(mailbox.Id, id), token);
            Assert.True(exported.Export.Complete);
            Assert.Equal(EvidenceHash.Of(exported.Content), exported.Export.Sha256);
            using var zip = new ZipArchive(new MemoryStream(exported.Content));
            Assert.Single(zip.Entries, entry => entry.FullName.StartsWith("attachments/", StringComparison.Ordinal));
            Assert.Equal(provider.Bytes, await EntryBytes(zip.Entries.Single(entry => entry.FullName.StartsWith("attachments/", StringComparison.Ordinal)), token));
            Assert.Equal(provider.Mime, await EntryBytes(zip.GetEntry($"messages/{one.Id}.eml")!, token));
            using var manifest = JsonDocument.Parse(await EntryBytes(zip.GetEntry("manifest.json")!, token));
            Assert.True(manifest.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal(2, manifest.RootElement.GetProperty("documents").GetArrayLength());
            Assert.All(zip.Entries, entry => Assert.DoesNotContain("..", entry.FullName));
            Assert.Contains("original@example.com", Encoding.UTF8.GetString(await EntryBytes(zip.GetEntry($"messages/{one.Id}.json")!, token)));

            await store.ApplySyncPageAsync("delete", new([Message(mailbox.Id, "one") with { IsDeleted = true }], null, false), token);
            Assert.Null(await store.GetMessageAsync(mailbox.Id, "one", token));
            Assert.NotNull(await store.GetEvidenceMessageAsync(one.Id, token));
            Assert.NotNull(await store.GetEvidenceDocumentAsync(document.Id, token));
            await store.DeleteAccountAsync(account.ProviderId, account.AccountId, token);
            Assert.Null(await store.GetEvidenceMessageAsync(one.Id, token));
            Assert.Null(await store.GetEvidenceExportAsync(exported.Export.Id, token));
        });
    }

    [Fact]
    public async Task McpEvidenceRejectsHiddenMailboxesRevocationAndUnauthorizedDownloads()
    {
        await WithStore(async (store, _, mailbox, hidden, _, service) =>
        {
            var token = TestContext.Current.CancellationToken;
            var visible = await service.IndexMessageAsync(mailbox.Id, "one", cancellationToken: token);
            var secret = await service.IndexMessageAsync(hidden.Id, "hidden", cancellationToken: token);
            var visibleDoc = Assert.Single((await store.SearchEvidenceDocumentsAsync([mailbox.Id], messageId: visible.Id, cancellationToken: token)).Items);
            var secretDoc = Assert.Single((await store.SearchEvidenceDocumentsAsync([hidden.Id], messageId: secret.Id, cancellationToken: token)).Items);
            var settings = new McpConfiguration(Enabled: true, MailboxIds: [mailbox.Id]);
            var tools = new McpMailTools(store, () => settings, () => Task.CompletedTask, (_, _, _) => Task.CompletedTask, service);
            await Assert.ThrowsAsync<McpException>(() => tools.ReadEvidence(secretDoc.Id, token));
            await Assert.ThrowsAsync<McpException>(() => tools.ReadEvidence(secretDoc.SourceLink, token));
            var resolved = JsonSerializer.SerializeToElement(await tools.ReadEvidence(visibleDoc.SourceLink, token));
            Assert.Equal(visibleDoc.Id, resolved.GetProperty("document").GetProperty("Id").GetString());
            Assert.Equal("evidence/records/" + visibleDoc.Id, resolved.GetProperty("recordPath").GetString());
            await Assert.ThrowsAsync<McpException>(() => tools.ReadAttachment(secretDoc.Id, cancellationToken: token));
            await Assert.ThrowsAsync<McpException>(() => tools.ExportEvidence([visible.Id, secret.Id], token));
            await Assert.ThrowsAsync<McpException>(() => tools.ReviewDocument(visibleDoc.Id, DocumentKind.Identity, VerificationStatus.Verified, "Me", cancellationToken: token));
            var chunk = JsonSerializer.SerializeToElement(await tools.ReadAttachment(visibleDoc.Id, 0, 5, token));
            Assert.Equal(5, Convert.FromBase64String(chunk.GetProperty("contentBase64").GetString()!).Length);
            Assert.Equal(5, chunk.GetProperty("nextOffset").GetInt32());
            await Assert.ThrowsAsync<McpException>(() => tools.ReadAttachment(visibleDoc.Id, -1, cancellationToken: token));
            await Assert.ThrowsAsync<McpException>(() => tools.SearchAttachmentText("REG123", [mailbox.Id, hidden.Id], cancellationToken: token));

            var saved = await store.GetMcpConfigurationAsync(token);
            await using var endpoint = new McpEndpoint(tools, 0, saved.EndpointPath, () => settings.Enabled, () => saved.AccessKey);
            await endpoint.StartAsync(token);
            using var http = new HttpClient();
            var url = endpoint.Address + "/evidence/files/" + visibleDoc.Id;
            using var unauthenticated = await http.GetAsync(url, token);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", saved.AccessKey);
            using var downloaded = await http.GetAsync(url, token);
            Assert.Equal(HttpStatusCode.OK, downloaded.StatusCode);
            Assert.Equal(visibleDoc.Sha256, EvidenceHash.Of(await downloaded.Content.ReadAsByteArrayAsync(token)));
            settings = settings with { MailboxIds = [] };
            using var revoked = await http.GetAsync(url, token);
            Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        });
    }

    [Fact]
    public async Task RelatedSearchExplainsLiteralMatchesWithoutInferringDomainRelationships()
    {
        await WithStore(async (store, _, mailbox, hidden, _, service) =>
        {
            var token = TestContext.Current.CancellationToken;
            await store.ApplySyncPageAsync("related", new([
                Message(mailbox.Id, "domain-lookalike") with { From = new("Lookalike", "person@notexample.com"), To = [new("Us", "accounts@local.test")], Body = "example.com" },
                Message(mailbox.Id, "invoice") with { Body = "Acme Holdings invoice INV-2026-0001" }
            ], null, false), token);
            var domains = await service.SearchRelatedAsync([mailbox.Id], new(Domains: ["example.com"]), cancellationToken: token);
            Assert.DoesNotContain(domains.Items, item => item.Message.Message.ProviderId == "domain-lookalike");
            Assert.All(domains.Items, item => Assert.All(item.Matches, match => Assert.Equal("Participant domain", match.Field)));
            var invoices = await service.SearchRelatedAsync([mailbox.Id], new(CompanyNames: ["Acme Holdings"], InvoiceReferences: ["INV-2026-0001"]), cancellationToken: token);
            var result = Assert.Single(invoices.Items);
            Assert.Equal(2, result.Matches.Count);
            Assert.DoesNotContain(hidden.Id, JsonSerializer.Serialize(invoices));
            Assert.NotEmpty((await service.SearchRelatedAsync([mailbox.Id], new(EmailAliases: ["Client <client@example.com>"]), cancellationToken: token)).Items);
            Assert.Throws<EvidenceException>(() => new CorrespondenceTerms(CompanyNames: [null!]).Validate());
        });
    }

    [Fact]
    public async Task MissingBytesAndUnsupportedDocumentsStayExplicitAndExportsReportPartialEvidence()
    {
        await WithStore(async (store, _, mailbox, _, provider, service) =>
        {
            var token = TestContext.Current.CancellationToken;
            provider.MissingBytes = true;
            var captured = await service.IndexMessageAsync(mailbox.Id, "one", cancellationToken: token);
            var document = Assert.Single((await store.SearchEvidenceDocumentsAsync([mailbox.Id], messageId: captured.Id, cancellationToken: token)).Items);
            Assert.Equal("Failed", document.State);
            Assert.Equal("content_unavailable", document.Extraction.Issue!.Code);
            var export = await service.ExportAsync([captured.Id], cancellationToken: token);
            Assert.False(export.Export.Complete);
            Assert.Contains(export.Export.Issues, issue => issue.Code == "attachment_unavailable");
            provider.MissingBytes = false;
            document = await service.RetryDocumentAsync(document.Id, cancellationToken: token);
            Assert.Equal("Complete", document.State);
            Assert.NotNull(document.Sha256);
        });
    }

    [Fact]
    public async Task ExtractsPdfAndOfficeTextAndReportsUnsupportedAndTruncatedContent()
    {
        var token = TestContext.Current.CancellationToken;
        var extractor = new AttachmentTextExtractor();
        var text = await extractor.ExtractAsync("certificate.pdf", "application/pdf", PdfBytes(), token);
        Assert.Contains("REGISTRATION 123456", text.Text);
        Assert.True(text.Complete);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            await writer.WriteAsync("<document><p><t>Acme registration REG123</t></p></document>");
        }
        var office = await extractor.ExtractAsync("registration.docx", "application/octet-stream", memory.ToArray(), token);
        Assert.Contains("REG123", office.Text);
        var unsupported = await extractor.ExtractAsync("file.bin", "application/octet-stream", [0, 1, 2], token);
        Assert.False(unsupported.Complete);
        Assert.Equal("unsupported_format", unsupported.Issue!.Code);
        var longText = await extractor.ExtractAsync("long.txt", "text/plain", Encoding.UTF8.GetBytes(new string('x', 200_001)), token);
        Assert.Equal(200_000, longText.Text.Length);
        Assert.False(longText.Complete);
        var image = await extractor.ExtractAsync("scan.png", "image/png", [0], token);
        Assert.Equal("ocr_unavailable", image.Issue!.Code);
    }

    [Fact]
    public async Task WindowsOcrReadsRenderedPdf()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await EvidenceOcr.RecognizeAsync(PdfBytes(), true, TestContext.Current.CancellationToken);
        Assert.True(result.Complete, result.Issue?.Message);
        Assert.Contains("123456", result.Text);
    }

    [Theory]
    [InlineData("bettermail://evidence/../../secret")]
    [InlineData("bettermail://evil/00000000000000000000000000000000")]
    [InlineData("https://evidence/00000000000000000000000000000000")]
    [InlineData("bettermail://evidence/00000000000000000000000000000000?path=secret")]
    public void RejectsInvalidEvidenceLinks(string value) => Assert.False(EvidenceLink.TryParse(value, out _));

    private static byte[] PdfBytes()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(600, 800).AddText("REGISTRATION 123456", 26, new PdfPoint(50, 600), font);
        return builder.Build();
    }

    private static async Task<byte[]> EntryBytes(ZipArchiveEntry entry, CancellationToken token)
    {
        await using var stream = entry.Open();
        return await EvidenceHash.ReadBoundedAsync(stream, 100 * 1024 * 1024, token);
    }

    private static MailMessage Message(string mailbox, string id) => new(mailbox, id, "thread", "<original@example.com>", "inbox", "Attached",
        new("Client", "client@example.com"), [new("Accounts", "accounts@example.com")], DateTimeOffset.UtcNow, "Attached", "See attached", false, true, true, MailImportance.Normal, [], null);

    private static async Task WithStore(Func<EncryptedMailStore, MailAccount, Mailbox, Mailbox, EvidenceProvider, EvidenceService, Task> action)
    {
        var directory = Directory.CreateTempSubdirectory("bettermail-evidence-test-");
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory.FullName, "mail.db"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            var token = TestContext.Current.CancellationToken;
            await store.InitializeAsync(token);
            var account = new MailAccount("test", "account", "tenant", "user@example.com", "User", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "User");
            var hidden = new Mailbox(account.AccountId, "hidden@example.com", "Hidden", true);
            await store.SaveAccountAsync(account, token);
            await store.SaveMailboxAsync(mailbox, token);
            await store.SaveMailboxAsync(hidden, token);
            await store.ApplySyncPageAsync("seed", new([Message(mailbox.Id, "one"), Message(mailbox.Id, "two"), Message(hidden.Id, "hidden")], null, false), token);
            var provider = new EvidenceProvider();
            var service = new EvidenceService(store, () => provider, new AttachmentTextExtractor());
            await action(store, account, mailbox, hidden, provider, service);
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class EvidenceProvider : IMailProvider
    {
        public byte[] Bytes { get; } = Encoding.UTF8.GetBytes("Company registration REG123. Contract evidence.");
        public byte[] Mime { get; } = Encoding.UTF8.GetBytes("From: client@example.com\r\nTo: accounts@example.com\r\nMessage-ID: <original@example.com>\r\nDate: Wed, 9 Sep 2026 10:00:00 +0200\r\n\r\nOriginal content\r\n");
        public bool MissingBytes { get; set; }
        public Task<byte[]> GetMimeMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) => Task.FromResult(Mime);
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MailAttachment>>([new("attachment", "../../contract.txt", "text/plain", Bytes.Length, false, null, null)]);
        public Task<MailAttachment?> GetAttachmentAsync(MailAccount account, Mailbox mailbox, string messageId, string attachmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MailAttachment?>(new(attachmentId, "../../contract.txt", "text/plain", Bytes.Length, false, null, MissingBytes ? null : Bytes));
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailFolder>>([]);
        public Task<MailSyncPage> SyncFolderAsync(MailAccount account, Mailbox mailbox, string folderId, string? cursor, CancellationToken cancellationToken = default) => Task.FromResult(new MailSyncPage([], null, false));
        public Task<MailMessage> GetMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) => Task.FromResult(Message(mailbox.Id, messageId));
        public Task MarkReadAsync(MailAccount account, Mailbox mailbox, string messageId, bool isRead, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MoveMessageAsync(MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetFlaggedAsync(MailAccount account, Mailbox mailbox, string messageId, bool isFlagged, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SendAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
