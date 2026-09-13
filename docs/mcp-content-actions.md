# MCP content operations

Start with `get_capabilities`. It reports permissions, allowed mailbox/Drive/workspace accounts, limits, registered tools, and workflow guidance. Use `get_action_guide(topic)` with `mail`, `calendar`, `people`, `tasks`, `notes`, `drive`, or `all` for tool descriptions. This information comes from the running server, not a fixed tool-count assumption.

If a listed tool is missing in a client, refresh discovery/reconnect, check the endpoint and running app version, and if necessary start a fresh client session. Do not claim an operation is unsupported solely because one client has an older list. The caller must use tools actually exposed to it.

| Workspace | Content operations |
| --- | --- |
| Mail | Read/search messages and threads; read headers; create/edit/delete drafts; provider-backed reply/reply-all/forward drafts; importance, flags and receipt requests where supported; attachment upload/read/remove; send through Busy; move/archive/delete/junk/not-junk; read/flag/pin state; inspect/cancel pending actions and request sync |
| Calendar | List calendars/events; create/edit/delete events with descriptions, attendees, reminders and recurrence; create an event from the full cached email body |
| People | Search saved contacts, including allowed shared address books; create/edit/delete contacts with full contact details; search discovered correspondents and save them as contacts |
| To Do | List/create/rename/delete task lists; list/create/edit/delete tasks; complete/reopen tasks; descriptions, due dates, reminders, recurrence, categories and importance |
| Notes | Navigate notebooks, sections and pages; read page content; create/update/delete pages |
| Drive | List/read/search via existing tools; create folders; upload/replace/download/rename/move/delete files; create read-only sharing links |

Workspace account access is explicitly enabled in Settings → MCP and independent of mailboxes and Drive accounts. Edit permission applies to writes. Sending permission additionally gates mail sending and calendar operations that can issue invitations/updates/cancellations. These settings never replace user authorization for a particular send or public share. Settings/navigation/account sign-in and granting MCP access remain human actions in the app.

## Reply with an attachment

1. Read the source using `read_mail` and `read_mail_headers`; honor Reply-To and the user's recipient restrictions.
2. Call `create_response_draft` with explicit `kind` (`Reply`, `ReplyAll`, or `Forward`) and the complete To, Cc and Bcc. Empty CC/BCC means none. It creates a real provider response draft; `create_draft` creates a new message.
3. Read the returned draft, then use `begin_attachment_upload`, sequential `upload_attachment_chunk` calls, and `complete_attachment_upload`. Pass the exact current `updatedAt`, real byte size and SHA-256. Repeat serially with a fresh draft version for additional files.
4. Read the complete draft and only call `send_draft` when explicitly authorized. Inspect Busy state for the remote outcome.

The client must be able to read the actual file bytes. BetterMail does not read client paths or fetch arbitrary attachment URLs. A Windows path in a transcript is not an upload, nor evidence that another client can access the file. Obtain the file through the client's supported file mechanism when necessary.

Oversized attachments can create a public OneDrive link during upload completion, before mail is sent; obtain sharing authorization first. Forwarding preserves source attachments. Response creation may have an uncertain outcome if interrupted after the provider creates the draft; inspect drafts before retrying.

Microsoft 365 uses [provider response drafts](https://learn.microsoft.com/en-us/graph/api/message-createreply?view=graph-rest-1.0). Gmail uses the source thread ID plus [reply headers](https://developers.google.com/workspace/gmail/api/guides/threads); draft updates preserve those headers when attachments/body change.

## Provider limits and state

Cross-account mail moves, Google Drive, and creating/deleting OneNote notebooks or sections are not supported by the current app/provider. OneNote's document-library limits still apply. A registered tool is not a guarantee that every account supports that action.

Provider-backed workspace calls need connectivity. Mutations request normal background sync so the UI cache catches up; a stale cache is not proof a remote write failed. Read state before destructive or replacement updates. Notes have a best-effort modified-time guard; workspace provider writes generally do not offer atomic version checks. Paging uses offset/limit and should restart after collection changes. Do not blindly retry an uncertain remote mutation.
