using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BetterMail.Core;

public sealed class EvidenceService(EncryptedMailStore store, Func<IMailProvider?> provider, AttachmentTextExtractor extractor)
{
    private readonly SemaphoreSlim _work = new(1, 1);
    public EncryptedMailStore Store => store;

    private async Task<(IMailProvider Provider, MailAccount Account, Mailbox Mailbox)> ContextAsync(string mailboxId, CancellationToken cancellationToken)
    {
        var mailbox = (await store.GetMailboxesAsync(cancellationToken)).FirstOrDefault(item => item.Id == mailboxId)
            ?? throw new EvidenceException("mailbox_unavailable", "The mailbox was removed or is not cached. Reconnect it in Settings > Accounts.");
        var account = (await store.GetAccountsAsync(cancellationToken)).FirstOrDefault(item => item.AccountId == mailbox.AccountId)
            ?? throw new EvidenceException("account_unavailable", "The source account is unavailable. Reconnect it in Settings > Accounts.");
        return (provider() ?? throw new EvidenceException("provider_unavailable", "The mail provider is not connected. Sync the account and retry.", true), account, mailbox);
    }

    public async Task<EvidencePage<EvidenceMessage>> IndexMailboxAsync(string mailboxId, string? cursor = null, int limit = 10,
        Action<string>? authorize = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 20) throw new EvidenceException("invalid_page_size", "Index 1–20 messages per batch.");
        authorize?.Invoke(mailboxId);
        var page = await store.SearchEvidenceMailAsync([mailboxId], [], cursor, limit, cancellationToken: cancellationToken);
        var results = new List<EvidenceMessage>();
        foreach (var message in page.Items)
            results.Add(await IndexMessageAsync(mailboxId, message.ProviderId, authorize, cancellationToken));
        authorize?.Invoke(mailboxId);
        return new(results, page.NextCursor, await store.GetEvidenceCompletenessAsync([mailboxId], cancellationToken));
    }

    public async Task<EvidenceMessage> IndexMessageAsync(string mailboxId, string messageId, Action<string>? authorize = null,
        CancellationToken cancellationToken = default)
    {
        await _work.WaitAsync(cancellationToken);
        try { return await IndexMessageCoreAsync(mailboxId, messageId, authorize, cancellationToken); }
        finally { _work.Release(); }
    }

    private async Task<EvidenceMessage> IndexMessageCoreAsync(string mailboxId, string messageId, Action<string>? authorize, CancellationToken cancellationToken)
    {
        authorize?.Invoke(mailboxId);
        var message = await store.GetMessageAsync(mailboxId, messageId, cancellationToken)
            ?? throw new EvidenceException("message_unavailable", "The message is not in the local cache. Sync its folder and retry.", true);
        var captured = await store.CaptureEvidenceMessageAsync(message, cancellationToken);
        if (captured.AttachmentState == "Complete") return captured;
        try
        {
            var context = await ContextAsync(mailboxId, cancellationToken);
            var attachments = await context.Provider.GetAttachmentsAsync(context.Account, context.Mailbox, messageId, cancellationToken);
            if (attachments.Count > 500) throw new EvidenceException("attachment_limit", "This message has more than 500 attachments. Inspect it directly in the mail provider.");
            foreach (var attachment in attachments)
            {
                authorize?.Invoke(mailboxId);
                cancellationToken.ThrowIfCancellationRequested();
                var id = EvidenceHash.Of(Encoding.UTF8.GetBytes(captured.Id + "\0" + attachment.ProviderId));
                var previous = await store.GetEvidenceDocumentAsync(id, cancellationToken);
                if (previous?.State == "Complete") continue;
                var bytes = previous?.Sha256 is null ? null : await store.GetEvidenceContentAsync(id, cancellationToken: cancellationToken);
                var document = previous ?? new EvidenceDocument(id, captured.Id, mailboxId, attachment.ProviderId, attachment.Name,
                    attachment.ContentType, attachment.Size, attachment.IsInline, null, "Pending", new("", "None", false));
                try
                {
                    if (bytes is null)
                    {
                        if (attachment.Size > AttachmentTextExtractor.MaximumBytes)
                            throw new EvidenceException("file_too_large", "Attachment exceeds the 25 MiB indexing/download limit. Download it directly from the provider.");
                        var hydrated = attachment.ContentBytes is not null ? attachment :
                            await context.Provider.GetAttachmentAsync(context.Account, context.Mailbox, messageId, attachment.ProviderId, cancellationToken);
                        bytes = hydrated?.ContentBytes ?? throw new EvidenceException("content_unavailable", "The provider did not return file bytes. This may be a cloud link or unsupported attached item.");
                        if (bytes.Length > AttachmentTextExtractor.MaximumBytes)
                            throw new EvidenceException("file_too_large", "Attachment exceeds the 25 MiB indexing/download limit.");
                    }
                    var text = await extractor.ExtractAsync(document.Name, document.ContentType, bytes, cancellationToken);
                    document = document with { Size = bytes.LongLength, Sha256 = EvidenceHash.Of(bytes), Extraction = text,
                        State = text.Complete ? "Complete" : "Partial" };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    document = document with { State = "Failed", Extraction = new("", "None", false, Issue: Issue(error, id)) };
                    if (bytes?.Length > AttachmentTextExtractor.MaximumBytes) bytes = null;
                }
                authorize?.Invoke(mailboxId);
                await store.SaveEvidenceDocumentAsync(document, bytes, cancellationToken);
            }
            // Enumeration completeness is separate from per-document extraction completeness.
            captured = captured with { AttachmentState = "Complete", Issue = null };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { captured = captured with { AttachmentState = "Failed", Issue = Issue(error, captured.Id) }; }
        authorize?.Invoke(mailboxId);
        await store.SetEvidenceMessageStateAsync(captured, cancellationToken: cancellationToken);
        return captured;
    }

    public async Task<EvidenceDocument> RetryDocumentAsync(string id, Action<string>? authorize = null, CancellationToken cancellationToken = default)
    {
        await _work.WaitAsync(cancellationToken);
        try
        {
            var document = await store.GetEvidenceDocumentAsync(id, cancellationToken) ?? throw Unavailable();
            authorize?.Invoke(document.MailboxId);
            var bytes = await store.GetEvidenceContentAsync(id, cancellationToken: cancellationToken);
            if (bytes is null)
            {
                var message = await store.GetEvidenceMessageAsync(document.MessageId, cancellationToken) ?? throw Unavailable();
                await store.SetEvidenceMessageStateAsync(message with { AttachmentState = "Pending" }, cancellationToken: cancellationToken);
                await IndexMessageCoreAsync(document.MailboxId, message.Message.ProviderId, authorize, cancellationToken);
                return await store.GetEvidenceDocumentAsync(id, cancellationToken) ?? throw Unavailable();
            }
            var text = await extractor.ExtractAsync(document.Name, document.ContentType, bytes, cancellationToken);
            document = document with { Extraction = text, State = text.Complete ? "Complete" : "Partial" };
            authorize?.Invoke(document.MailboxId);
            await store.SaveEvidenceDocumentAsync(document, null, cancellationToken);
            return document;
        }
        finally { _work.Release(); }
    }

    public async Task<EvidencePage<EvidenceSearchResult>> SearchAttachmentsAsync(string[] mailboxes, string query, string? cursor = null,
        int limit = 50, CancellationToken cancellationToken = default)
    {
        var page = await store.SearchEvidenceDocumentsAsync(mailboxes, query, cursor, limit, cancellationToken: cancellationToken);
        var results = new List<EvidenceSearchResult>();
        foreach (var document in page.Items)
        {
            var message = await store.GetEvidenceMessageAsync(document.MessageId, cancellationToken) ?? throw Unavailable();
            results.Add(new(message with { Message = message.Message with { Body = null } }, document with { Extraction = document.Extraction with { Text = "" } },
                [new("Attachment text/name (full-text tokens)", query, Snippet(document.Extraction.Text.Length == 0 ? document.Name : document.Extraction.Text, query))]));
        }
        return new(results, page.NextCursor, page.Completeness);
    }

    public async Task<EvidencePage<EvidenceSearchResult>> SearchRelatedAsync(string[] mailboxes, CorrespondenceTerms terms,
        string? cursor = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var validated = (terms ?? throw new EvidenceException("invalid_terms", "Supply aliases, company names, domains or invoice references.")).Validate();
        var page = await store.SearchEvidenceMailAsync(mailboxes, validated.Select(term => term.Term).ToArray(), cursor, limit, cancellationToken: cancellationToken);
        var results = new List<EvidenceSearchResult>();
        foreach (var message in page.Items)
        {
            var addresses = new[] { message.From }.Concat(message.To).Concat(message.Cc ?? []).ToArray();
            var text = message.Subject + "\n" + message.Preview + "\n" + message.Body;
            var matches = new List<EvidenceMatch>();
            foreach (var (field, term) in validated)
            {
                if (field == "Email alias")
                {
                    var address = new System.Net.Mail.MailAddress(term).Address;
                    matches.AddRange(addresses.Where(item => item.Address.Equals(address, StringComparison.OrdinalIgnoreCase))
                        .Select(item => new EvidenceMatch("Participant email", term, item.ToString())));
                }
                else if (field == "Domain")
                    matches.AddRange(addresses.Where(item => item.Address.EndsWith("@" + term, StringComparison.OrdinalIgnoreCase))
                        .Select(item => new EvidenceMatch("Participant domain", term, item.ToString())));
                else
                {
                    var allText = string.Join('\n', addresses.Select(item => item.ToString())) + "\n" + text;
                    if (allText.Contains(term, StringComparison.OrdinalIgnoreCase)) matches.Add(new(field, term, Snippet(allText, term)));
                }
            }
            if (matches.Count == 0) continue;
            var captured = await store.CaptureEvidenceMessageAsync(message, cancellationToken);
            results.Add(new(captured with { Message = captured.Message with { Body = null } }, null, matches.Distinct().ToArray()));
        }
        return new(results, page.NextCursor, page.Completeness);
    }

    public async Task<EvidenceDocument> ReviewAsync(string id, DocumentKind kind, VerificationStatus verification,
        string reviewer, string note, Action<string>? authorize = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(verification) || string.IsNullOrWhiteSpace(reviewer) || reviewer.Length > 100 || note is null || note.Length > 2000)
            throw new EvidenceException("invalid_review", "Choose a document type and verification status, supply a reviewer (1–100 characters), and a note of up to 2,000 characters.");
        await _work.WaitAsync(cancellationToken);
        try
        {
            var document = await store.GetEvidenceDocumentAsync(id, cancellationToken) ?? throw Unavailable();
            authorize?.Invoke(document.MailboxId);
            document = document with { Kind = kind, Verification = verification, Reviewer = reviewer.Trim(), Note = note, ReviewedAt = DateTimeOffset.UtcNow };
            await store.ReviewEvidenceDocumentAsync(document, cancellationToken);
            return document;
        }
        finally { _work.Release(); }
    }

    public async Task<(EvidenceExport Export, byte[] Content)> ExportAsync(string[] ids, Action<string>? authorize = null,
        CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Length is < 1 or > 100 || ids.Any(string.IsNullOrWhiteSpace))
            throw new EvidenceException("invalid_selection", "Select 1–100 evidence record IDs to export.");
        await _work.WaitAsync(cancellationToken);
        try
        {
            var messages = new Dictionary<string, EvidenceMessage>();
            var documents = new Dictionary<string, EvidenceDocument>();
            var selectedMessages = new HashSet<string>();
            var issues = new List<EvidenceIssue>();
            // Validate every selected record before fetching or exporting any content.
            foreach (var id in ids.Distinct())
            {
                var document = await store.GetEvidenceDocumentAsync(id, cancellationToken);
                var message = await store.GetEvidenceMessageAsync(document?.MessageId ?? id, cancellationToken) ?? throw Unavailable();
                authorize?.Invoke(message.Message.MailboxId);
                messages[message.Id] = message;
                if (document is null) selectedMessages.Add(message.Id);
                else documents[id] = document with { Extraction = document.Extraction with { Text = "" } };
            }
            using var memory = new MemoryStream();
            var files = new Dictionary<string, object>();
            long total = 0;
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                async Task<string> AddFile(string path, byte[] bytes)
                {
                    if (files.ContainsKey(path)) return path;
                    total += bytes.Length;
                    if (total > 100 * 1024 * 1024) throw new EvidenceException("export_too_large", "Selected evidence exceeds 100 MiB. Export a smaller selection.");
                    var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(bytes, cancellationToken);
                    files[path] = new { path, sha256 = EvidenceHash.Of(bytes), size = bytes.LongLength };
                    return path;
                }
                foreach (var id in selectedMessages)
                {
                    var message = messages[id];
                    authorize?.Invoke(message.Message.MailboxId);
                    try
                    {
                        var mime = await store.GetEvidenceContentAsync(id, message: true, cancellationToken);
                        if (mime is null)
                        {
                            var context = await ContextAsync(message.Message.MailboxId, cancellationToken);
                            mime = await context.Provider.GetMimeMessageAsync(context.Account, context.Mailbox, message.Message.ProviderId, cancellationToken);
                            if (mime.Length > 50 * 1024 * 1024) throw new EvidenceException("file_too_large", "MIME messages are limited to 50 MiB.");
                            message = message with { MimeSha256 = EvidenceHash.Of(mime) };
                            authorize?.Invoke(message.Message.MailboxId);
                            await store.SetEvidenceMessageStateAsync(message, mime, cancellationToken);
                            messages[id] = message;
                        }
                        if (EvidenceHash.Of(mime) != message.MimeSha256)
                            throw new EvidenceException("hash_mismatch", "Captured MIME bytes do not match the saved SHA-256 hash. Export stopped.");
                        await AddFile($"messages/{id}.eml", mime);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (EvidenceException error) when (error.Code is "export_too_large" or "hash_mismatch") { throw; }
                    catch (Exception error) when (error is not OutOfMemoryException) { issues.Add(Issue(error, id)); }
                    // Preserve the cached projection explicitly, even if the provider's MIME is unavailable.
                    await AddFile($"messages/{id}.json", JsonSerializer.SerializeToUtf8Bytes(message));
                    if (message.AttachmentState != "Complete") issues.Add(new("attachments_not_indexed", "Attachment enumeration is incomplete. Index this message before exporting separate attachment files.", id, true));
                    string? cursor = null;
                    do
                    {
                        var page = await store.SearchEvidenceDocumentsAsync([message.Message.MailboxId], cursor: cursor, limit: 200, messageId: id, cancellationToken: cancellationToken);
                        foreach (var item in page.Items) documents[item.Id] = item with { Extraction = item.Extraction with { Text = "" } };
                        cursor = page.NextCursor;
                    } while (cursor is not null);
                }
                foreach (var document in documents.Values)
                {
                    authorize?.Invoke(document.MailboxId);
                    var bytes = await store.GetEvidenceContentAsync(document.Id, cancellationToken: cancellationToken);
                    if (bytes is null) { issues.Add(new("attachment_unavailable", "Attachment bytes were not captured; retry indexing or download from the provider.", document.Id, true)); continue; }
                    if (EvidenceHash.Of(bytes) != document.Sha256) throw new EvidenceException("hash_mismatch", "Captured attachment bytes do not match the saved SHA-256 hash. Export stopped.");
                    await AddFile($"attachments/{document.Sha256}", bytes);
                }
                foreach (var mailbox in messages.Values.Select(message => message.Message.MailboxId).Distinct()) authorize?.Invoke(mailbox);
                var manifest = new
                {
                    schemaVersion = 1, exportedAt = DateTimeOffset.UtcNow, complete = issues.Count == 0,
                    note = "EML files are provider-supplied MIME; JSON files are cached projections, not original MIME. Hashes establish byte identity, not authenticity or legal verification. Reviewer names are supplied by the reviewer. Identical attachment bytes are stored once; all source occurrences are retained.",
                    messages = messages.Values.Select(message => message with { Message = message.Message with { Body = null } }),
                    documents = documents.Values.Select(document => new
                    {
                        document = document with { Extraction = document.Extraction with { Text = "" } },
                        file = document.Sha256 is null ? null : $"attachments/{document.Sha256}"
                    }), files = files.Values.ToArray(), issues
                };
                await AddFile("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
            }
            var content = memory.ToArray();
            var export = new EvidenceExport(Guid.NewGuid().ToString("N"), "BetterMail-evidence.zip", EvidenceHash.Of(content), content.LongLength,
                DateTimeOffset.UtcNow.AddDays(1), messages.Values.Select(message => message.Message.MailboxId).Distinct().ToArray(), issues.Count == 0, issues);
            await store.SaveEvidenceExportAsync(export, content, cancellationToken);
            return (export, content);
        }
        finally { _work.Release(); }
    }

    public static EvidenceIssue Issue(Exception error, string? id = null) => error switch
    {
        EvidenceException evidence => new(evidence.Code, evidence.Message, id, evidence.Retryable),
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => new("provider_access_denied", "The provider denied access. Reauthenticate the account and verify shared-mailbox permissions.", id, true),
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => new("provider_item_missing", "The source was moved or deleted at the provider. Sync the mailbox and select its current record.", id, true),
        HttpRequestException => new("provider_request_failed", "The provider could not be reached. Retry when connected. " + error.Message, id, true),
        _ => new("evidence_failed", error.Message, id)
    };

    public static EvidenceException Unavailable() => new("record_unavailable", "Evidence record not found in this installation or its source account was removed.");

    private static string Snippet(string text, string term)
    {
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        var start = Math.Max(0, index - 70);
        return text.Substring(start, Math.Min(240, text.Length - start)).ReplaceLineEndings(" ");
    }
}
