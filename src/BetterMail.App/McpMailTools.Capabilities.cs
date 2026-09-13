using System.Reflection;
using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace BetterMail.App;

internal sealed partial class McpMailTools
{
    private static string[] RegisteredToolNames() => typeof(McpMailTools).GetMethods()
        .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
        .Where(attribute => attribute is not null)
        .Select(attribute => attribute!.Name!)
        .Order(StringComparer.Ordinal).ToArray();

    [McpServerTool(Name = "get_action_guide", ReadOnly = true), Description("Discover how to perform content operations. topic is all, mail, calendar, people, tasks, notes, or drive. Returns current server tool names/descriptions and workflow/permission guidance. Registered tools may still be unavailable in a stale client or denied by account permissions. Never claim UI/MCP parity from a tool count alone.")]
    public object GetActionGuide(string topic = "all")
    {
        _ = EnabledConfiguration();
        var words = topic.ToLowerInvariant() switch
        {
            "all" => Array.Empty<string>(), "mail" => ["mail", "draft", "attachment", "busy", "action", "response", "reply", "forward", "thread", "folders"],
            "calendar" => ["calendar", "event"], "people" => ["contact", "people"],
            "tasks" => ["task"], "notes" => ["note"], "drive" => ["drive"],
            _ => throw new McpException("Choose all, mail, calendar, people, tasks, notes, or drive.")
        };
        var tools = typeof(McpMailTools).GetMethods().Select(method => new
        {
            name = method.GetCustomAttribute<McpServerToolAttribute>()?.Name,
            description = method.GetCustomAttribute<DescriptionAttribute>()?.Description
        }).Where(tool => tool.name is not null && (words.Length == 0 || words.Any(word => tool.name.Contains(word, StringComparison.Ordinal))))
            .OrderBy(tool => tool.name, StringComparer.Ordinal).ToArray();
        return new { topic, tools, usage = CapabilityUsage };
    }

    private static readonly object CapabilityUsage = new
    {
        discovery = "registeredTools lists tools implemented by this server, not permission grants. If a listed tool is absent from your client, refresh MCP tool discovery or reconnect this server; if necessary start a new session. Check that both sessions use the same endpoint and running BetterMail version. A missing client tool does not prove the server lacks uploads. If get_capabilities itself is missing, check discovery/endpoint/version before proceeding. Do not invent tool calls unavailable to your client.",
        permissions = "allowWrites controls content changes; allowSending additionally controls mail sending and calendar invitations/updates/cancellations. Calendar, contacts, tasks and notes require an explicitly enabled allowedWorkspaceAccounts accountKey. Mail operations require an enabled mailboxId. Drive operations independently require an allowedDriveAccounts accountKey. These settings do not replace user authorization to send or publicly share content.",
        replies = "A new message is not a reply. Use create_reply_draft or create_forward_draft for the corresponding action; the general form is create_response_draft with mailboxId, source messageId and explicit kind (Reply, ReplyAll, Forward), To, Cc and Bcc. Read read_mail/read_mail_headers first, honor Reply-To and user recipient restrictions. Empty Cc/Bcc means none; no recipients are inferred. The provider creates a saved response draft; upload attachments to its returned id, read_draft, then send_draft only when authorized. The client must read actual local file bytes; a Windows path cannot be used by a Linux/remote client or passed to BetterMail as an upload. If the client cannot access those bytes, request the file through that client's supported file mechanism.",
        workspaces = "Use list_workspace_accounts first. Calendar: list_calendars → list_events → create/update/delete_event, or create_event_from_mail using an independently allowed source mailbox. People: search_contacts → create/update/delete_contact; optional mailboxId selects an allowed shared address book. Tasks: list_task_lists → list_tasks; create/rename/delete_task_list and create/update/complete/delete tasks. Notes: list_notebooks → list_note_sections → list_note_pages → read/create/update/delete_note_page. Remote mutations can have uncertain outcomes; inspect state before retrying. Workspace reads use provider APIs and require connectivity; cache/UI refresh happens through normal background sync.",
        mailActions = "set_mail_state changes read/flag/pin explicitly. move_mail to a well-known folder handles archive/delete/junk/not-junk; list_folders supplies IDs. Repeat scoped calls for multi-selection. list_drafts/read_draft includes sync issue metadata; delete_draft removes unwanted drafts. list_busy/get_action includes failure details and pause status; three failures pause automatic attempts. check_mail_action investigates server state without mutations; recover_mail_action rechecks exact identity and repairs stale move/state IDs, rejecting ambiguity and concurrent changes; retry_mail_action explicitly retries after fixing the cause, preserving history. Unconfirmed sends cannot be retried. cancel_mail_action attempts cancellation before execution; sync_mail leaves paused actions paused. Existing attachment read/export tools and Drive upload tools can be composed to save attachments to Drive.",
        limitations = "Content operations only: settings, account sign-in and granting MCP access remain in the app. Cross-account mail moves, Google Drive, and creating/deleting OneNote notebooks or sections are not supported by the current app/provider. A registered tool is not a guarantee that every account/provider supports the action. Microsoft OneNote library limits still apply. Read current state before destructive or replacement updates; provider workspace writes generally lack atomic version checks. Do not claim a queued operation was completed remotely; inspect Busy state.",
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
