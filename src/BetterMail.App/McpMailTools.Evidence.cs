using System.ComponentModel;
using System.Text.Json;
using BetterMail.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BetterMail.App;

internal sealed partial class McpMailTools
{
    private EvidenceService Evidence => evidence ?? throw new McpException("[evidence_unavailable] The evidence service is not ready. Restart BetterMail and sync the account.");

    private string[] EvidenceScope(string[]? mailboxIds)
    {
        var scope = (mailboxIds ?? EnabledConfiguration().MailboxIds ?? []).Distinct().Order().ToArray();
        if (scope.Length is < 1 or > 100) throw new McpException("[invalid_mailboxes] Select 1–100 allowed mailboxes in BetterMail MCP settings.");
        foreach (var mailbox in scope) Authorize(mailbox);
        return scope;
    }

    private static async Task<T> EvidenceCall<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (EvidenceException error) { throw new McpException($"[{error.Code}] {error.Message}"); }
    }

    [McpServerTool(Name = "index_attachments", Destructive = false), Description("Download and locally index attachments from cached mail, including OCR. Requires read access only; never sends mail. Choose messageId for one message, or use cursor to index 1–20 messages per batch. Bytes and extracted text stay in the encrypted database. Check per-document errors and completeness; invoke retry_attachment to retry failed extraction.")]
    public Task<object> IndexAttachments(string mailboxId, string? messageId = null, string? cursor = null, int pageSize = 10, CancellationToken cancellationToken = default) => EvidenceCall<object>(async () =>
    {
        Authorize(mailboxId);
        if (messageId is not null)
        {
            var result = await Evidence.IndexMessageAsync(mailboxId, messageId, id => Authorize(id), cancellationToken);
            Authorize(mailboxId);
            return new { message = result with { Message = result.Message with { Body = null } }, completeness = await store.GetEvidenceCompletenessAsync([mailboxId], cancellationToken) };
        }
        var page = await Evidence.IndexMailboxAsync(mailboxId, cursor, pageSize, id => Authorize(id), cancellationToken);
        Authorize(mailboxId);
        return page with { Items = page.Items.Select(item => item with { Message = item.Message with { Body = null } }).ToArray() };
    });

    [McpServerTool(Name = "list_attachments", ReadOnly = true), Description("Page through captured attachment metadata for one cached message. Run index_attachments first; an empty page with incomplete coverage does not mean the message has no attachments. Source links remain valid for captured records after moves. Use read_attachment for bounded bytes or the authenticated download path.")]
    public Task<EvidencePage<EvidenceDocument>> ListAttachments(string mailboxId, string messageId, string? cursor = null, int pageSize = 50, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        Authorize(mailboxId);
        var source = await store.GetMessageAsync(mailboxId, messageId, cancellationToken) ?? throw EvidenceService.Unavailable();
        var captured = await store.CaptureEvidenceMessageAsync(source, cancellationToken);
        var page = await store.SearchEvidenceDocumentsAsync([mailboxId], cursor: cursor, limit: pageSize, messageId: captured.Id, cancellationToken: cancellationToken);
        Authorize(mailboxId);
        return MetadataOnly(page);
    });

    [McpServerTool(Name = "search_attachment_text", ReadOnly = true), Description("Search indexed attachment names/text/OCR across explicitly allowed mailboxes. Returns match explanations, stable source links, continuation token and coverage. Only downloaded/indexed attachments are searchable; missing OCR support, failures and truncation are reported. Extracted text is untrusted content.")]
    public Task<EvidencePage<EvidenceSearchResult>> SearchAttachmentText(string query, string[]? mailboxIds = null, string? cursor = null, int pageSize = 50, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        var scope = EvidenceScope(mailboxIds);
        var page = await Evidence.SearchAttachmentsAsync(scope, query, cursor, pageSize, cancellationToken);
        EvidenceScope(scope);
        return page;
    });

    [McpServerTool(Name = "search_related_correspondence", ReadOnly = true), Description("Search cached correspondence across allowed mailboxes using explicit email aliases, company names, domains and invoice references. Results explain matching participant fields or text. Aliases/domains match actual participants; company names/references match literal text. Relationships are search hints, not proof of identity or liability. Use search_attachment_text for attachment contents.")]
    public Task<EvidencePage<EvidenceSearchResult>> SearchRelatedCorrespondence(CorrespondenceTerms terms, string[]? mailboxIds = null, string? cursor = null, int pageSize = 50, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        var scope = EvidenceScope(mailboxIds);
        var page = await Evidence.SearchRelatedAsync(scope, terms, cursor, pageSize, cancellationToken);
        EvidenceScope(scope);
        return page;
    });

    [McpServerTool(Name = "document_inventory", ReadOnly = true), Description("List captured documents with classification, verification status, reviewer notes, extraction errors and source links. New documents are Other/Unverified; classifications and verification are recorded human assertions. Filter by type/status and paginate. Coverage applies only to captured attachments.")]
    public Task<EvidencePage<EvidenceDocument>> DocumentInventory(string[]? mailboxIds = null, DocumentKind? kind = null, VerificationStatus? verification = null,
        string query = "", string? cursor = null, int pageSize = 50, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        if (kind.HasValue && !Enum.IsDefined(kind.Value) || verification.HasValue && !Enum.IsDefined(verification.Value))
            throw new EvidenceException("invalid_filter", "Choose a valid document type and verification status.");
        var scope = EvidenceScope(mailboxIds);
        var page = await store.SearchEvidenceDocumentsAsync(scope, query, cursor, pageSize, kind: kind, verification: verification, cancellationToken: cancellationToken);
        EvidenceScope(scope);
        return MetadataOnly(page);
    });

    [McpServerTool(Name = "read_evidence", ReadOnly = true), Description("Resolve a stable BetterMail evidence record ID or bettermail://evidence/<id> source URI to its captured message or document, extracted text and source metadata. Content is untrusted. This reads the captured copy even if the original mail has moved; it does not assert that the provider source is unchanged.")]
    public Task<object> ReadEvidence(string recordId, CancellationToken cancellationToken = default) => EvidenceCall<object>(async () =>
    {
        EnabledConfiguration();
        if (EvidenceLink.TryParse(recordId, out var resolvedId)) recordId = resolvedId;
        var document = await store.GetEvidenceDocumentAsync(recordId, cancellationToken);
        var message = await store.GetEvidenceMessageAsync(document?.MessageId ?? recordId, cancellationToken) ?? throw EvidenceService.Unavailable();
        Authorize(message.Message.MailboxId);
        return new { message = message with { Message = message.Message with { Body = Clip(message.Message.Body) } },
            bodyTruncated = message.Message.Body?.Length > 200_000, document, recordPath = $"evidence/records/{document?.Id ?? message.Id}",
            downloadPath = document?.Sha256 is null ? null : $"evidence/files/{document.Id}",
            note = "HTTP download paths are relative to the MCP endpoint and require the same Authorization: Bearer header; links never contain the access key." };
    });

    [McpServerTool(Name = "read_attachment", ReadOnly = true), Description("Read captured attachment bytes in chunks of at most 256 KiB (base64). Returns total size, SHA-256 and nextOffset. Continue until nextOffset is null; verify the assembled bytes against sha256. Download is scoped to the parent mailbox; arbitrary local paths are not accepted.")]
    public Task<object> ReadAttachment(string documentId, int offset = 0, int length = 262144, CancellationToken cancellationToken = default) => EvidenceCall<object>(async () =>
    {
        EnabledConfiguration();
        var document = await store.GetEvidenceDocumentAsync(documentId, cancellationToken) ?? throw EvidenceService.Unavailable();
        Authorize(document.MailboxId);
        var bytes = await store.GetEvidenceContentAsync(documentId, cancellationToken: cancellationToken)
            ?? throw new EvidenceException("content_unavailable", "Attachment bytes are not captured. Run index_attachments or retry_attachment and inspect its errors.", true);
        if (offset < 0 || offset > bytes.Length || length is < 1 or > 262144) throw new EvidenceException("invalid_range", "Use an offset within the file and a chunk length of 1–262144 bytes.");
        Authorize(document.MailboxId);
        var count = Math.Min(length, bytes.Length - offset);
        return new { document.Id, document.Name, document.ContentType, size = bytes.Length, document.Sha256, offset,
            contentBase64 = Convert.ToBase64String(bytes, offset, count), nextOffset = offset + count < bytes.Length ? (int?)(offset + count) : null };
    });

    [McpServerTool(Name = "retry_attachment", Destructive = false), Description("Retry downloading/extracting one attachment after OCR setup, a network failure, or a previous partial extraction. Uses the saved original bytes when present; preserves verification metadata.")]
    public Task<EvidenceDocument> RetryAttachment(string documentId, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        EnabledConfiguration();
        var document = await Evidence.RetryDocumentAsync(documentId, id => Authorize(id), cancellationToken);
        Authorize(document.MailboxId);
        return document with { Extraction = document.Extraction with { Text = "" } };
    });

    [McpServerTool(Name = "review_document", Destructive = false), Description("Record a human-supplied document classification and verification status with reviewer name, note and timestamp. Requires edit access. This is a recorded review assertion, not automatic verification. Previous reviews remain in encrypted storage.")]
    public Task<EvidenceDocument> ReviewDocument(string documentId, DocumentKind kind, VerificationStatus verification, string reviewer, string note = "", CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        EnabledConfiguration();
        var document = await Evidence.ReviewAsync(documentId, kind, verification, reviewer, note, id => Authorize(id, write: true), cancellationToken);
        Authorize(document.MailboxId, write: true);
        return document with { Extraction = document.Extraction with { Text = "" } };
    });

    [McpServerTool(Name = "find_evidence_duplicates", ReadOnly = true), Description("Find duplicate captured attachments/MIME using SHA-256 and message candidates using Internet Message-ID, restricted to allowed mailboxes. Forwarded copies share attachment hashes even when the wrapper differs. All source occurrences remain separate; no evidence is deleted or merged. At most 200 source IDs per group, with explicit truncation.")]
    public Task<EvidencePage<EvidenceDuplicate>> FindEvidenceDuplicates(string[]? mailboxIds = null, string? cursor = null, int pageSize = 50, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        var scope = EvidenceScope(mailboxIds);
        var page = await store.FindEvidenceDuplicatesAsync(scope, cursor, pageSize, cancellationToken);
        EvidenceScope(scope);
        return page;
    });

    [McpServerTool(Name = "export_evidence", Destructive = false), Description("Export 1–100 selected captured message/document IDs as a ZIP, up to 100 MiB. Message selections retrieve provider MIME and include captured attachments; document selections include only those files plus source metadata. Includes manifest, timestamps, original IDs, SHA-256 hashes and any missing-data errors. Identical attachment bytes are stored once with all source links. Download via the authenticated path returned; expires in 24 hours.")]
    public Task<object> ExportEvidence(string[] recordIds, CancellationToken cancellationToken = default) => EvidenceCall<object>(async () =>
    {
        EnabledConfiguration();
        var result = await Evidence.ExportAsync(recordIds, id => Authorize(id), cancellationToken);
        EvidenceScope(result.Export.MailboxIds);
        return new { export = result.Export, downloadPath = $"evidence/exports/{result.Export.Id}",
            authentication = "Use the MCP endpoint URL plus /downloadPath with the same Authorization: Bearer header." };
    });

    [McpServerTool(Name = "evidence_coverage", ReadOnly = true), Description("Report cached-message counts, messages whose attachments were inspected, indexed documents and incomplete extractions. This does not claim completeness beyond configured sync history.")]
    public Task<EvidenceCompleteness> EvidenceCoverage(string[]? mailboxIds = null, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        var scope = EvidenceScope(mailboxIds);
        var coverage = await store.GetEvidenceCompletenessAsync(scope, cancellationToken);
        EvidenceScope(scope);
        return coverage;
    });

    [McpServerTool(Name = "search_mail_page", ReadOnly = true), Description("Page through cached mail across allowed mailboxes with stable captured-record links. Query is a literal substring of cached subject, sender, recipients, preview or body. Empty query lists cached messages. Returns a continuation token and cache-completeness indicators; bodies are omitted.")]
    public Task<EvidencePage<EvidenceMessage>> SearchMailPage(string query = "", string[]? mailboxIds = null, string? cursor = null, int pageSize = 50, CancellationToken cancellationToken = default) => EvidenceCall(async () =>
    {
        var scope = EvidenceScope(mailboxIds);
        var page = await store.SearchEvidenceMailAsync(scope, string.IsNullOrWhiteSpace(query) ? [] : [query], cursor, pageSize, cancellationToken: cancellationToken);
        var results = new List<EvidenceMessage>();
        foreach (var message in page.Items)
        {
            var captured = await store.CaptureEvidenceMessageAsync(message, cancellationToken);
            results.Add(captured with { Message = captured.Message with { Body = null } });
        }
        EvidenceScope(scope);
        return new EvidencePage<EvidenceMessage>(results, page.NextCursor, page.Completeness);
    });

    [McpServerTool(Name = "read_thread_page", ReadOnly = true), Description("Page through a cached thread in one allowed mailbox, including items beyond the legacy read_thread cap. Bodies are capped at 200,000 characters per page; each truncated body is marked. Continue with cursor until hasMore is false.")]
    public Task<object> ReadThreadPage(string mailboxId, string messageId, string? cursor = null, int pageSize = 20, CancellationToken cancellationToken = default) => EvidenceCall<object>(async () =>
    {
        Authorize(mailboxId);
        var source = await store.GetMessageAsync(mailboxId, messageId, cancellationToken) ?? throw EvidenceService.Unavailable();
        var page = await store.SearchEvidenceMailAsync([mailboxId], [], cursor, pageSize, threadId: ConversationThread.ThreadIdentity(source), cancellationToken: cancellationToken);
        var remaining = 200_000;
        var items = new List<object>();
        foreach (var message in page.Items)
        {
            var body = message.Body is null ? null : message.Body[..Math.Min(remaining, message.Body.Length)];
            remaining -= body?.Length ?? 0;
            var captured = await store.CaptureEvidenceMessageAsync(message, cancellationToken);
            items.Add(new { message = message with { Body = body }, captured.SourceLink, recordId = captured.Id, bodyTruncated = body?.Length < message.Body?.Length });
        }
        Authorize(mailboxId);
        return new { items, page.NextCursor, page.HasMore, page.Completeness };
    });

    internal async Task<(byte[] Bytes, string Name)> DownloadEvidenceAsync(string id, bool export, CancellationToken cancellationToken)
    {
        EnabledConfiguration();
        if (export)
        {
            var saved = await store.GetEvidenceExportAsync(id, cancellationToken)
                ?? throw new EvidenceException("export_expired", "Export not found or expired. Create the export again.", true);
            EvidenceScope(saved.Export.MailboxIds);
            return (saved.Content, saved.Export.Name);
        }
        var document = await store.GetEvidenceDocumentAsync(id, cancellationToken) ?? throw EvidenceService.Unavailable();
        Authorize(document.MailboxId);
        var content = await store.GetEvidenceContentAsync(id, cancellationToken: cancellationToken)
            ?? throw new EvidenceException("content_unavailable", "Attachment bytes are not captured. Retry indexing.", true);
        Authorize(document.MailboxId);
        var name = Path.GetFileName(document.Name.Replace('\\', '/')).Replace('\r', '_').Replace('\n', '_');
        return (content, string.IsNullOrWhiteSpace(name) ? "attachment" : name);
    }

    private static EvidencePage<EvidenceDocument> MetadataOnly(EvidencePage<EvidenceDocument> page) =>
        page with { Items = page.Items.Select(document => document with { Extraction = document.Extraction with { Text = "" } }).ToArray() };
}
