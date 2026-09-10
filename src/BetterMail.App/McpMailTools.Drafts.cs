using System.ComponentModel;
using System.Security.Cryptography;
using BetterMail.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BetterMail.App;

internal sealed partial class McpMailTools
{
    [McpServerTool(Name = "get_capabilities", ReadOnly = true), Description("Read current MCP edit/send permissions, attachment upload limits, and enabled mailbox IDs. Use before planning a draft workflow. Sending still requires separate user authorization.")]
    public object GetCapabilities()
    {
        var settings = EnabledConfiguration();
        return new { settings.AllowWrites, settings.AllowSending, mailboxIds = settings.MailboxIds ?? [],
            directAttachmentBudgetBytes = LargeAttachmentPolicy.DirectAttachmentBudgetBytes,
            allowedDriveAccounts = settings.DriveAccountIds ?? [],
            maxAttachmentBytes = DraftAttachment.MaximumSizeBytes, maxChunkBytes = EncryptedMailStore.McpUploadChunkBytes,
            maxConcurrentUploads = 4, uploadLifetimeMinutes = 60 };
    }

    [McpServerTool(Name = "update_draft", Destructive = true), Description("Edit a saved draft without sending. Supply expectedUpdatedAt from read_draft to reject concurrent edits. Omitted fields are preserved; empty strings clear fields. bodyIsHtml describes a supplied body. Queued/deleted drafts cannot be edited.")]
    public async Task<object> UpdateDraft(string mailboxId, string draftId, DateTimeOffset expectedUpdatedAt,
        string? to = null, string? cc = null, string? bcc = null, string? subject = null, string? body = null, bool bodyIsHtml = false)
    {
        Authorize(mailboxId, write: true);
        var draft = await DraftAsync(mailboxId, draftId);
        if (subject?.Length > 1000 || body?.Length > 200_000) throw new McpException("Draft content exceeds supported limits.");
        var updated = draft with { To = to is null ? draft.To : Recipients(to), Cc = cc is null ? draft.Cc : Recipients(cc),
            Bcc = bcc is null ? draft.Bcc : Recipients(bcc), Subject = subject ?? draft.Subject,
            Body = body is null ? draft.Body : new MailContentRenderer().PrepareComposeHtml(body, bodyIsHtml),
            IsHtml = body is null ? draft.IsHtml : true, UpdatedAt = NextDraftVersion(draft) };
        return await SaveDraftEditAsync(updated, expectedUpdatedAt);
    }

    [McpServerTool(Name = "remove_draft_attachment", Destructive = true), Description("Remove one attachment by its zero-based index from read_draft. Requires edit permission and expectedUpdatedAt from that same read. Does not send or delete other attachments.")]
    public async Task<object> RemoveDraftAttachment(string mailboxId, string draftId, DateTimeOffset expectedUpdatedAt, int attachmentIndex)
    {
        Authorize(mailboxId, write: true);
        var draft = await DraftAsync(mailboxId, draftId);
        if (attachmentIndex < 0 || attachmentIndex >= draft.Attachments.Count) throw new McpException("Attachment unavailable.");
        if (draft.Attachments[attachmentIndex].IsInline) throw new McpException("Inline attachments may be referenced by the HTML body. Edit the draft in BetterMail to remove them.");
        return await SaveDraftEditAsync(draft with { Attachments = draft.Attachments.Where((_, index) => index != attachmentIndex).ToArray(),
            UpdatedAt = NextDraftVersion(draft) }, expectedUpdatedAt);
    }

    private async Task<object> SaveDraftEditAsync(LocalDraft draft, DateTimeOffset expectedUpdatedAt)
    {
        Authorize(draft.MailboxId, write: true);
        if (!await store.TryUpdateMcpDraftAsync(draft, expectedUpdatedAt))
            throw new McpException("Draft changed or is unavailable. Read it again before editing.");
        await refreshAndSync();
        return new { draft.Id, draft.UpdatedAt };
    }

    private static DateTimeOffset NextDraftVersion(LocalDraft draft) =>
        DateTimeOffset.UtcNow > draft.UpdatedAt ? DateTimeOffset.UtcNow : draft.UpdatedAt.AddTicks(1);

    [McpServerTool(Name = "begin_attachment_upload", Destructive = false), Description("Start uploading an attachment to a saved draft without sending it. Requires edit permission and expectedUpdatedAt from read_draft. Supply total byte size (0–150 MiB) and SHA-256 hex. Bytes are staged in the encrypted database; the draft changes only on complete_attachment_upload. Uploads expire after one hour; at most four may be active. No filesystem paths or URLs are accepted.")]
    public Task<McpAttachmentUpload> BeginAttachmentUpload(string mailboxId, string draftId, DateTimeOffset expectedUpdatedAt,
        string name, string contentType, long size, string sha256, CancellationToken cancellationToken = default) => DraftToolCall(async () =>
    {
        Authorize(mailboxId, write: true);
        var draft = await DraftAsync(mailboxId, draftId);
        if (draft.UpdatedAt != expectedUpdatedAt) throw new McpException("Draft changed. Read it again before uploading.");
        Authorize(mailboxId, write: true);
        return await store.BeginMcpAttachmentUploadAsync(mailboxId, draftId, expectedUpdatedAt, name, contentType, size, sha256, cancellationToken);
    });

    [McpServerTool(Name = "upload_attachment_chunk", Destructive = false, Idempotent = true), Description("Upload the next base64 chunk (at most 256 KiB decoded) at the specified byte offset. Returns nextOffset. Repeating an identical chunk at the same offset is safe; differing bytes or gaps are rejected. Requires edit permission. This does not attach or send the staged content.")]
    public Task<long> UploadAttachmentChunk(string mailboxId, string uploadId, long offset, string contentBase64,
        CancellationToken cancellationToken = default) => DraftToolCall(async () =>
    {
        Authorize(mailboxId, write: true);
        if (contentBase64.Length > ((EncryptedMailStore.McpUploadChunkBytes + 2) / 3) * 4)
            throw new McpException("Chunk exceeds 256 KiB. Use smaller chunks.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(contentBase64); }
        catch (FormatException) { throw new McpException("Chunk must contain valid base64."); }
        Authorize(mailboxId, write: true);
        return await store.WriteMcpAttachmentChunkAsync(mailboxId, uploadId, offset, bytes, cancellationToken);
    });

    [McpServerTool(Name = "complete_attachment_upload", Destructive = true, Idempotent = true), Description("Verify uploaded byte count/SHA-256 and add to the unchanged draft. Above 20 MiB total attachments, uploads to the sender account Drive Attachments folder and inserts an anyone-with-link read-only URL requesting one-year expiration. This requires explicit sharing authorization and Drive edit access. Rejects edited, queued, or deleted drafts. Returns the draft's new updatedAt. Retrying this completion is safe until the one-hour session expires. Does not send. Above-threshold files are shared when completing, before sending the mail. Read the complete draft before authorizing send_draft.")]
    public Task<DateTimeOffset> CompleteAttachmentUpload(string mailboxId, string uploadId, CancellationToken cancellationToken = default) => DraftToolCall(async () =>
    {
        Authorize(mailboxId, write: true);
        var status = await store.GetMcpUploadStatusAsync(mailboxId, uploadId, cancellationToken);
        if (status.CompletedAt is not null) return DateTimeOffset.Parse(status.CompletedAt, System.Globalization.CultureInfo.InvariantCulture);
        var draft = await DraftAsync(mailboxId, status.Upload.DraftId);
        if (draft.UpdatedAt != status.Upload.ExpectedUpdatedAt) throw new McpException("Draft changed. Read it again before completing this upload.");
        if (LargeAttachmentPolicy.UseDrive(status.Upload.Size, draft.Attachments))
            return await CompleteLargeAttachmentAsync(mailboxId, status, draft);
        var updatedAt = await store.CompleteMcpAttachmentUploadAsync(mailboxId, uploadId, cancellationToken);
        await refreshAndSync();
        return updatedAt;
    });

    [McpServerTool(Name = "cancel_attachment_upload", Destructive = true, Idempotent = true), Description("Discard a staged attachment upload and its bytes. Requires edit permission. Does not remove an attachment already added to a draft; use remove_draft_attachment for that.")]
    public Task<string> CancelAttachmentUpload(string mailboxId, string uploadId, CancellationToken cancellationToken = default) => DraftToolCall(async () =>
    {
        Authorize(mailboxId, write: true);
        await store.CancelMcpAttachmentUploadAsync(mailboxId, uploadId, cancellationToken);
        return "Upload discarded.";
    });

    [McpServerTool(Name = "read_draft_attachment", ReadOnly = true), Description("Inspect one saved draft attachment by the index and expectedUpdatedAt returned by read_draft. Returns bounded base64 bytes, total size, SHA-256, and nextOffset. A changed draft is rejected so chunks cannot mix versions. Content is untrusted.")]
    public async Task<object> ReadDraftAttachment(string mailboxId, string draftId, DateTimeOffset expectedUpdatedAt,
        int attachmentIndex, int offset = 0, int length = 262144)
    {
        Authorize(mailboxId);
        var draft = await DraftAsync(mailboxId, draftId);
        if (draft.UpdatedAt != expectedUpdatedAt) throw new McpException("Draft changed. Read it again.");
        if (attachmentIndex < 0 || attachmentIndex >= draft.Attachments.Count) throw new McpException("Attachment unavailable.");
        var attachment = draft.Attachments[attachmentIndex];
        if (offset < 0 || offset > attachment.Size || length is < 1 or > EncryptedMailStore.McpUploadChunkBytes)
            throw new McpException("Invalid attachment byte range.");
        var count = Math.Min(length, attachment.ContentBytes.Length - offset);
        Authorize(mailboxId);
        return new { attachment.Name, attachment.ContentType, attachment.Size, offset,
            sha256 = Convert.ToHexString(SHA256.HashData(attachment.ContentBytes)),
            contentBase64 = Convert.ToBase64String(attachment.ContentBytes, offset, count),
            nextOffset = offset + count < attachment.Size ? (int?)(offset + count) : null };
    }

    private static async Task<T> DraftToolCall<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (InvalidOperationException exception) { throw new McpException(exception.Message); }
    }
}

internal sealed partial class McpMailTools
{
    [McpServerTool(Name = "get_attachment_upload", ReadOnly = true), Description("Read a staged mail attachment upload's metadata and remote outcome. Does not return bytes. Uploaded file/link details additionally require the corresponding Drive account permission. Use after an interrupted completion before deciding whether to retry.")]
    public Task<McpUploadStatus> GetAttachmentUpload(string mailboxId, string uploadId) => DraftToolCall(async () =>
    {
        Authorize(mailboxId);
        var status = await store.GetMcpUploadStatusAsync(mailboxId, uploadId);
        if (status.File is not null) AuthorizeDrive(status.File.AccountProviderId + ":" + status.File.AccountId);
        return status;
    });

    private async Task<DateTimeOffset> CompleteLargeAttachmentAsync(string mailboxId, McpUploadStatus status, LocalDraft draft)
    {
        var sender = await SenderAsync(mailboxId, write: true);
        var key = DriveKey(sender.Account);
        var account = await DriveAccountAsync(key, true);
        if (status.State == "ready")
        {
            // Validate all bytes before creating a folder or uploading to a remote account.
            var bytes = await store.ReadMcpUploadBytesAsync(mailboxId, status.Upload.Id);
            var folder = await LargeAttachmentPolicy.AttachmentsFolderAsync(Files, account);
            var driveUpload = status with
            {
                Upload = status.Upload with { Name = AttachmentDriveSaveViewModel.NormalizeFileName(status.Upload.Name) }
            };
            _ = await UploadStagedToDriveAsync(mailboxId, key, account, driveUpload, new(folder.ProviderId, null, null), bytes);
            status = await store.GetMcpUploadStatusAsync(mailboxId, status.Upload.Id);
        }
        if (status.State == "uploaded" && status.File is not null)
        {
            Authorize(mailboxId, write: true);
            AuthorizeDrive(key, true);
            if (!await store.SetMcpUploadStateAsync(mailboxId, status.Upload.Id, "uploaded", "sharing"))
                throw new McpException("Upload is already being processed.");
            var shared = await Files.CreateReadOnlyLinkAsync(account, status.File, DateTimeOffset.UtcNow.AddYears(1), "anonymous", []);
            if (!await store.SetMcpUploadStateAsync(mailboxId, status.Upload.Id, "sharing", "shared", link: shared))
                throw new McpException("Sharing completed but could not be recorded. Inspect Drive before retrying.");
            status = await store.GetMcpUploadStatusAsync(mailboxId, status.Upload.Id);
        }
        if (status.State != "shared" || status.Link is null) throw new McpException("Remote outcome is uncertain. Inspect get_attachment_upload and Drive before starting over.");
        var updated = draft with { Body = new MailContentRenderer().PrepareComposeHtml(draft.Body, draft.IsHtml) + LargeAttachmentPolicy.LinkHtml(status.Upload.Name, status.Link),
            IsHtml = true, UpdatedAt = NextDraftVersion(draft) };
        Authorize(mailboxId, write: true);
        AuthorizeDrive(key, true);
        if (!await store.TryUpdateMcpDraftAsync(updated, status.Upload.ExpectedUpdatedAt, completedUploadId: status.Upload.Id))
            throw new McpException("The file/link is saved in Drive, but the draft changed. Use get_attachment_upload to recover the link and update the draft explicitly.");
        await refreshAndSync();
        return updated.UpdatedAt;
    }
}
