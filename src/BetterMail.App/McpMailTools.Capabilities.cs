using System.Reflection;
using ModelContextProtocol.Server;

namespace BetterMail.App;

internal sealed partial class McpMailTools
{
    private static string[] RegisteredToolNames() => typeof(McpMailTools).GetMethods()
        .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
        .Where(attribute => attribute is not null)
        .Select(attribute => attribute!.Name!)
        .Order(StringComparer.Ordinal).ToArray();

    private static readonly object CapabilityUsage = new
    {
        discovery = "registeredTools lists tools implemented by this server, not permission grants. If a listed tool is absent from your client, refresh MCP tool discovery or reconnect this server; if necessary start a new session. Check that both sessions use the same endpoint and running BetterMail version. A missing client tool does not prove the server lacks uploads. If get_capabilities itself is missing, check discovery/endpoint/version before proceeding. Do not invent tool calls unavailable to your client.",
        permissions = "allowWrites controls draft edits and uploads; allowSending additionally controls sending. Mail operations require an enabled mailboxId. Drive operations independently require an allowedDriveAccounts accountKey. These settings do not replace user authorization to send or publicly share content.",
        mailAttachments = new
        {
            summary = "Create the draft first, then upload each attachment separately. create_draft intentionally has no attachment parameter. Nothing is sent by this upload sequence.",
            steps = new[]
            {
                "1. Choose a mailboxId from list_mailboxes. Call create_draft(mailboxId, to, subject, body, ...); retain its id as draftId. For an existing draft use its draftId.",
                "2. Call read_draft(mailboxId, draftId). Retain updatedAt exactly as expectedUpdatedAt. Obtain the actual file bytes using your client's authorized file access; this server does not read client paths or fetch attachment URLs.",
                "3. Compute size in bytes and SHA-256 hex over the complete raw file. Call begin_attachment_upload(mailboxId, draftId, expectedUpdatedAt, name, contentType, size, sha256). Retain returned id as uploadId.",
                "4. Starting at offset 0, call upload_attachment_chunk(mailboxId, uploadId, offset, contentBase64) with sequential chunks of at most maxChunkBytes decoded bytes. Base64-encode each raw chunk separately. Use the returned nextOffset; never base64-encode the path or invent file content. A zero-byte file needs no chunk calls.",
                "5. Call complete_attachment_upload(mailboxId, uploadId) after all bytes arrive. This verifies size/hash and returns the new updatedAt. Read the draft again to verify its attachments/body. Repeat steps 2–5 serially for each additional attachment, using the fresh draft version.",
                "6. Only after reviewing the complete draft and receiving explicit send authorization, call send_draft. Upload completion never sends the draft."
            },
            oversized = "Above directAttachmentBudgetBytes for total attachments, completion uploads to the sender account's OneDrive Attachments folder and inserts an anyone-with-link read-only URL with a requested one-year expiration. Obtain explicit public-sharing authorization and enable that Drive account before completing. Sharing happens at completion, before sending; tenant policy may reject the link/expiry. This flow currently uses OneDrive, not Google Drive.",
            recovery = "Use get_attachment_upload to inspect an interrupted upload. Identical chunk retries at the same offset are safe. Reject gaps or different retry bytes. If the draft changed, read it again and cancel_attachment_upload before starting a new upload with its current version. Do not blindly repeat an uncertain remote upload/share. cancel_attachment_upload only discards staging; remove_draft_attachment removes an attached file using its index and current expectedUpdatedAt."
        },
        driveUploads = new
        {
            steps = new[]
            {
                "1. Use list_drive_accounts for accountKey, then list_drive_items to choose a folder. Mailbox access alone does not grant Drive access.",
                "2. Call begin_drive_upload with accountKey, file name, contentType, size, SHA-256 and optional parentId (omitted means root). For replacement supply replaceItemId and expectedETag from get_drive_item.",
                "3. Write sequential raw-byte base64 chunks with upload_drive_chunk using uploadId, offset and contentBase64; follow its returned nextOffset.",
                "4. Call complete_drive_upload. For interrupted operations inspect get_drive_upload before retrying; cancel_drive_upload discards staging without undoing a completed remote change."
            },
            sharing = "Drive upload does not create a sharing link. Use share_drive_file only when explicitly authorized."
        }
    };
}
