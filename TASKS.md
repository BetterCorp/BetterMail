# BetterMail desktop completion backlog

This is the source of truth for completing the Microsoft 365 desktop application.
Google Workspace, Android/iOS projects, and platform-specific installer/store builds are deferred.

Status: `[ ]` queued, `[-]` in progress, `[x]` verified complete, `[!]` blocked by an external dependency or tenant policy.

## P0 — current correctness blockers

- [-] Make the existing shell respond immediately while the same running window is resized. Source breakpoints and staged phone views are implemented; canonical executable resize smoke testing remains.
  - Wide: application rail + folder tree + message list + reading pane.
  - Compact/tablet: rail + folder/list stage + reading pane.
  - Phone width: one folder/list/message stage with back navigation and bottom rail.
  - Settings, workspace modules, WebView, splitters, focus, and scroll regions must never overlap or clip.
  - Verify live transitions at 390, 720, 900, 1200, 1420, and 1920 pixels.
- [x] Preserve the selected folder, message, reading position, and WebView during F9 and 60-second sync.
- [x] Merge sync results into visible state without a global busy overlay or closing the selected message.
- [x] Keep delayed mark-as-read and flag changes metadata-only.
- [x] Repair sparse Microsoft Graph delta messages without turning valid subjects into `(no subject)`.
- [x] Request the complete delegated scope set during account add/re-authentication and never from individual modules.
- [x] Detect an existing account missing required consent and show one actionable re-authentication instruction.
- [x] Load normal, inline CID, and paged attachments without invalid base-attachment `$select` fields.
- [x] Make account management contextual at every width: each account row visibly owns `Re-authenticate`, `Add shared mailbox`, and `Remove`; nest its shared mailboxes below it and never require selecting an account first.
- [x] Remove `Add account` and `Add shared mailbox` actions from the folder pane; account changes belong only in Settings > Accounts.
- [x] Keep exactly one sync action in the application header and exactly one settings action at the bottom/end of the application rail; no mail-rail or duplicate top-right copies.
- [x] Render provider parent/child folders as actual nested expandable tree nodes, never a flattened folder list.
- [!] Tenant administrator consent remains governed by Microsoft Entra tenant policy; BetterMail cannot bypass it.

## P1 — desktop mail workflow completion

- [x] Multiple Microsoft 365 accounts, unified inbox, manual shared mailboxes, and account-owned shared-mailbox settings.
- [x] Folder hierarchy, local search, delta cursors, F9, 60-second sync, read/unread, flag, archive, junk, and delete.
- [x] Compose, send, reply, forward, Cc/Bcc, default signature, and small attachments.
- [x] Autosave encrypted local drafts, reopen them with the original sender/attachments, and remove them after successful send.
- [x] Synchronize drafts with Microsoft Graph at startup/F9/60-second sync boundaries, including provider CRUD/send, encrypted ID mapping, attachment upload sessions, mailbox locks, exactly-once mapped send/delete, and non-destructive conflict handling. Local autosave remains immediate; cloud writes are sync-boundary based.
- [x] Render conversation threads with expand/collapse, quoted-message separation, stable selection, one active body WebView, CID attachments, and responsive action overflow.
- [x] Add Microsoft Graph upload sessions for attachments from 3 MiB through the client cap, with chunking, resume offsets, cancellation, and transient retries.
- [x] Add per-account/mailbox signatures and sender defaults with migration from the previous global signature.
- [x] Add native desktop notifications with account/folder context and a persisted setting to disable them; baseline/deduplication prevents startup, metadata-update, and newly-linked shared-mailbox history floods.
- [ ] Add offline/error states, retry affordances, cancellation, and actionable Graph error messages.
- [ ] Finish shared-mailbox send/read validation for Send As and Send on behalf failure cases.
- [-] Add keyboard commands for compose, reply, reply-all, forward, delete, archive, search, folders, next/previous message, and escape/back. All listed commands except dedicated keyboard folder navigation are verified.

## P2 — Microsoft 365 workspace workflow completion

- [x] Workspace aggregation rule: Calendar, People, To Do, Drive, and Notes combine every linked account by default while retaining provider/account identity, colour, filtering, and actionable per-account errors.
- [x] Calendar, People, To Do, OneDrive, and OneNote read/list/search vertical slices.
- [x] Calendar provider: account/calendar ownership plus create/edit/delete events, attendees, recurrence, and reminders.
- [x] Calendar UI: aggregated Outlook-style day/work-week/week/month views, mini-month navigation, today/previous/next controls, simultaneous multi-calendar overlays, account/calendar colour toggles, and event CRUD editor.
- [ ] Calendar parity polish: all-day/timezone/body/organizer/response/online-meeting fields and recurrence-occurrence editing.
- [x] People provider: account-owned contact create/edit/delete with validated Graph payloads.
- [x] Build a local discovered-people query from message senders/recipients with normalized addresses, display names, account/mailbox provenance, frequency, and last-contacted time.
- [x] People UI: combine saved provider contacts and discovered mail correspondents across all accounts; distinguish source/type, deduplicate safely, search, and expose contact editor/delete actions only for saved contacts.
- [x] To Do provider: account/list ownership plus create/edit/complete/delete tasks.
- [x] To Do UI: all-account/list responsive shell integration with search, task/list CRUD, paging, richer task fields, provenance, and partial-account errors.
- [x] Drive UI: left pane groups each linked account/provider as a root with its own expandable directory tree; the main pane shows the selected directory contents without flattening accounts.
- [x] OneDrive provider: account-owned hierarchical/paged folder operations, cross-account-search-ready paths, streaming upload/download, rename/delete/create-folder, and resumable large uploads.
- [x] Drive search is cross-account/provider by default, labels every result with account/path, and supports optional account filtering.
- [x] Drive actions operate on the owning account/path: folder navigation, create folder, upload/download, rename, delete, refresh, and attach-from-drive.
- [x] Compose attachment picker can choose local files or provider-relative cloud items while retaining provider/account/item identity through bounded download/send.
- [x] OneNote provider: account-owned notebook → section → page hierarchy, paging, untrusted content retrieval, and supported create/structured-edit/delete operations.
- [x] OneNote UI: per-account notebook/section/page tree, sanitized page viewer, supported editor actions, delete confirmation, partial-account errors, and sole-shell integration.
- [x] Remove first-account assumptions from the integrated workspace ViewModels; partial account failure does not hide healthy account results.
- [ ] Release assertion: Calendar, People, To Do, OneDrive, and OneNote must perform a real authenticated load/action or show one actionable re-authentication/error state; no inert module shells.

## P3 — Outlook-equivalent shell, rendering, and QA

- [x] Application header/search, application rail, separate folder pane, command bar, compact message list, reading header, themes, accents, and density setting.
- [x] Remote pictures blocked by default with an explicit `Load pictures` banner; safe links open externally.
- [x] Replace missing CID/blocked-content gaps with intentional Outlook-style messaging while preserving sanitisation and CSP.
- [-] Finish sender/recipient details, attachment affordances, reply-all, overflow actions, and message context menus. Safe sender/To/Cc Reply All, reading/row actions, CID attachments, and context menus are complete; remaining attachment/sender-detail polish stays open.
- [-] Remove remaining visual jank at 100%, 125%, 150%, and 200% display scaling. Semantic themes, responsive overflow, staged phone views, fluid Drive columns, and readable Calendar event colors are complete; live multi-scale smoke testing remains.
- [ ] Verify long account names, folder names, subjects, addresses, counts, RTL text, and high-contrast themes.
- [-] Complete screen-reader names, tab order, visible focus, keyboard-only operation, and contrast checks. Repeated template tab indices were removed and shared focus/contrast states plus command/body automation names were added; live screen-reader verification remains.
- [ ] Compare against current Outlook at 1920×1080 and retain before/after/reference screenshots.
- [ ] Add focused UI checks covering navigation, live resize, folder selection, message selection, settings/module overlays, rendering, and compose.

## Release gate

- [x] Eliminate competing `win-x64`/`win-x64-update` outputs; the documented and launched executable is the canonical verified artifact.
- [x] All automated tests pass with zero warnings (110/110, Release).
- [x] No disclosed client secret value or secret ID is present in source or published artifacts.
- [ ] Fresh account onboarding and existing-account re-authentication are tested separately.
- [x] Canonical-build source assertions confirm there are no folder-pane account buttons, duplicate sync/settings actions, inert module shells, or flattened child folders; the published executable remains running through startup smoke.
- [ ] Manual smoke test covers primary mailbox, shared mailbox, every workspace module, offline startup, sync, and restart persistence.
- [x] Publish the verified self-contained Windows artifact and update the README to match actual behavior.

## Explicitly deferred

- Google Workspace/Gmail/Google Calendar/Google Drive provider implementation.
- Android and iOS application projects, mobile WebView integration, device testing, and store releases.
- MSI/MSIX/macOS/Linux-specific installers and store packaging beyond development artifacts.
