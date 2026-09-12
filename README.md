![BetterMail - Local-first desktop email. Less digging, more finding.](asset-pack/03-github/bettermail-readme-banner.png)

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)
![Avalonia 12](https://img.shields.io/badge/Avalonia-12-8B44AC)
![Microsoft Graph](https://img.shields.io/badge/Microsoft-Graph-0078D4?logo=microsoft)
[![Build](https://github.com/BetterCorp/BetterMail/actions/workflows/build.yml/badge.svg?branch=master)](https://github.com/BetterCorp/BetterMail/actions/workflows/build.yml)
[![AGPL v3 license](https://img.shields.io/badge/license-AGPL--3.0--only-blue)](LICENSE)

---

BetterMail is an Outlook-style desktop client built around fast local storage, cross-account
search, and one consistent interface for Microsoft 365 and Gmail. Mail is cached and indexed
locally so selecting messages and searching does not depend on provider round trips.

> [!WARNING]
> BetterMail is under active development. Keep independent backups of important data and
> review changes before using it as your primary client.

![BetterMail mail workspace with personal information redacted](asset-pack/08-screen-shots/bettermail-desktop-redacted.png)

*Microsoft 365 mail workspace. Personal and organizational information has been redacted.*

## Highlights

| Area | Current support |
| --- | --- |
| **Mail** | Multiple accounts, unified inbox, shared mailboxes, folder trees, threads, drafts, attachments, reply/forward, archive, delete, junk, flags, and read state |
| **Search** | Encrypted local index, cross-account results, scoped mail/people/files search, folder paths, and archive opt-in |
| **Microsoft 365** | Outlook mail and calendars, People, To Do, OneDrive, OneNote, shared mailboxes, Send As, and Send on behalf |
| **Google Workspace** | Gmail sync/search, label badges, drafts, attachments, send, archive, spam, stars, and read state |
| **Calendar** | Aggregated calendars, month and timeline views, calendar colours, event availability, and event editing |
| **Files** | Account-relative OneDrive trees, cross-drive search, sharing links, and attaching cloud files to mail |
| **Desktop UX** | Responsive Outlook-style panes, keyboard navigation, F9 sync, background sync, notifications, compact mode, and light/dark/system themes |
| **Security** | Sanitized HTML, blocked remote content by default, encrypted SQLite storage, DPAPI-backed keys on Windows, and secure token caching |

Google Calendar, Contacts, Tasks, and Drive are not implemented yet. A Google account therefore
appears in mail views, but not in Microsoft-only workspace modules.

## Screenshots

> [!NOTE]
> Personal, organizational, message, calendar, contact, and file data in every screenshot has
> been permanently replaced with opaque pixels. Public images are flattened PNGs, not blurred
> originals.

### Cross-account search

![BetterMail cross-account search with sensitive results redacted](asset-pack/08-screen-shots/bettermail-search-redacted.png)

### Calendar

![BetterMail calendar with sensitive calendar details redacted](asset-pack/08-screen-shots/bettermail-calendar-redacted.png)

### People

![BetterMail People workspace with sensitive contact details redacted](asset-pack/08-screen-shots/bettermail-people-redacted.png)

### OneDrive

![BetterMail OneDrive workspace with sensitive file details redacted](asset-pack/08-screen-shots/bettermail-drive-redacted.png)

## Quick start

### Prerequisites

- .NET SDK 10.0.201 or a compatible .NET 10 patch.
- Windows 10/11, or a glibc Linux desktop supported by Avalonia.
- A Microsoft 365 work or school account, or a Google account with Gmail enabled.

BetterMail release builds include the Microsoft and Google OAuth credentials supplied by the build
pipeline. End users do not need to create app registrations or configure redirect URLs: install
the app, add an account, and sign in.

From the repository root:

```powershell
dotnet restore BetterMail.slnx
dotnet run --project src/BetterMail.App
```

Run the checks:

```powershell
dotnet build BetterMail.slnx -m:1
dotnet run --project tests/BetterMail.Tests
```

Create the older self-contained Windows and Linux development builds:

```powershell
./scripts/publish.ps1
```

Published builds are written to `artifacts/win-x64` and `artifacts/linux-x64`.

## Local MCP access

MCP is disabled by default. In **Settings > MCP**, select the mailboxes a client may access,
enable the endpoint, and click **Apply MCP settings**. Creating drafts and moving or deleting
mail require the edit permission; sending saved drafts requires the additional send permission.
New mailboxes are excluded until explicitly selected. Uncheck enablement and apply to stop access.

Configure your MCP client with these fields:

| Field | Value |
| --- | --- |
| Transport | Streamable HTTP |
| URL | Copy the entire endpoint from BetterMail: `http://127.0.0.1:47831/bm/<installation-token>` (default port) |
| HTTP header name | `Authorization` |
| HTTP header value | `Bearer <your access key>` |

The `/bm/` path contains a 256-bit cryptographically random token generated once
and saved in the encrypted mail database. It remains unchanged across restarts,
updates, port changes, and access-key rotation. Copy the full URL; `/mcp` is no
longer an endpoint. Existing v0.2.41 clients must update their URL once after upgrading.
The private path makes guessing difficult; bearer authentication remains mandatory.

Use **Copy header value** in BetterMail's MCP settings to copy `Bearer`, one space, and the
current key together. Paste that into the header's value field without quotes or angle brackets.
If the client offers a dedicated **Bearer token** field, use **Copy access key** instead; that
field expects only the key. For clients with a JSON headers field, the format is:

```json
{"Authorization": "Bearer PASTE_ACCESS_KEY_HERE"}
```

Save and restart the MCP connection. In **ChatGPT desktop > Settings > MCP servers**, select
**Restart**, then type `/mcp` in the composer to confirm BetterMail is connected
([official setup instructions](https://learn.chatgpt.com/docs/extend/mcp#configure-in-the-chatgpt-desktop-app)).
Attaching `@BetterMail` as a desktop window selects computer control, which is separate from MCP.
ChatGPT in a browser does not read this local MCP configuration.

For a public HTTPS endpoint, use **Settings > MCP > BetterTunnels public MCP link**.
Sign in with a Senior account, then select **Create public link**. See
[BetterTunnels MCP setup](docs/mcp-bettertunnels.md) for connection and account details.

**Replace access key** immediately revokes the previous key for new requests. Update the header
in your client and restart its connection after replacing the key.
The endpoint is loopback-only and runs while BetterMail is open. Settings and the key are stored
in the encrypted mail database. Clients must support a manually configured authorization header.

Tools list allowed mailboxes/folders, search and read cached mail/threads, list/read/create drafts,
queue draft deletion and sending, move mail (including archive/trash/junk destinations), inspect
Busy actions, and request sync. Mail queue actions use the existing persistent queue and normal sync retries. Drive tools perform remote operations immediately.
Search is limited to locally cached history. Bodies are bounded and report truncation; captured
attachment bytes are available through the evidence tools below. Arbitrary local filesystem access is not exposed. Treat mail content as untrusted data and
review a draft before authorizing your client to send it.

## MCP draft attachments and Drive

Enable draft edits and separately select **Allowed Drive accounts** in MCP Settings. Mailbox access
never grants Drive access automatically. Sending remains a separate permission.

- `get_capabilities` reports permissions and limits. `update_draft`, `remove_draft_attachment`, and
  `read_draft_attachment` use the `updatedAt` from `read_draft` to reject stale edits or reads.
- Upload bytes using `begin_attachment_upload`, sequential `upload_attachment_chunk` calls, then
  `complete_attachment_upload`. Supply the byte count and SHA-256; each base64 chunk is at most
  256 KiB decoded. Exact chunk retries and completed retries are safe. Read the draft again after
  completion. `get_attachment_upload` inspects recovery state; `cancel_attachment_upload` discards staging.
- Drive tools list accounts/items, read metadata, create folders, rename, move, delete, share, and
  download bounded chunks. `download_drive_file` requires the current ETag to prevent mixed versions.
  `begin_drive_upload`, `upload_drive_chunk`, and `complete_drive_upload` create files or replace content
  with `replaceItemId` and `expectedETag`. Inspect `get_drive_upload` or discard `cancel_drive_upload`.
  No arbitrary local paths or download URLs are accepted.

Uploads are staged in the encrypted database, expire after one hour, and allow four active sessions
of up to 150 MiB each. Expired staging is removed on subsequent upload activity. Interrupted remote
operations retain their known result or report an uncertain outcome; inspect Drive before starting
another upload. Canceling staging does not delete remote files or revoke sharing links.

Both the composer and MCP use a conservative **20 MiB total attachment budget**, accounting for mail
encoding overhead. This is an app policy, not a detected mailbox limit; administrators can configure
lower limits. Beyond that budget, new attachments upload to the sender's OneDrive **Attachments**
folder and become **anyone-with-the-link, read-only links**, requesting expiration in one year.
The actual expiration appears in the message. Existing Drive selections can be shared directly.
Sharing takes effect when attaching, before sending; discarding the draft does not revoke it.
If account policy prohibits anonymous or expiring links, the operation reports an error and does
not silently create a permanent link. The uploaded file may remain available for recovery.
Google Drive support is deferred; the sender must have a connected OneDrive account for automatic
local-file fallback. Files under the budget stay ordinary attachments.

`share_drive_file` also supports explicit organization or named-recipient audiences. Clients must
obtain user authorization for sharing and deleting, and treat filenames and file contents as untrusted.

## MCP attachment search and evidence tools

Use `index_attachments` through MCP to download and index cached mail: supply a message ID for one
message, or continue with its cursor to process a mailbox in batches. Completed captures are retained.
Mailbox indexing inspects every cached message, including those whose provider flag omits inline
attachments; the initial pass can take time on a large mailbox.

- `search_attachment_text` searches names, PDF text, OCR, text files, DOCX, XLSX, PPTX and ODT.
- `search_related_correspondence` accepts known email aliases, company names, domains and invoice
  references across the chosen mailboxes. Each result explains its match. Aliases and domains
  match participants; company names and references match cached message text. Search attachment
  contents separately. Search hints do not establish identity or liability.
- `find_evidence_duplicates` groups identical attachment/MIME bytes by SHA-256 and flags matching original
  Message-IDs for review. Forwarded wrappers may differ while their attachments match. Every source
  occurrence remains available; nothing is merged or deleted.
- `document_inventory` lists captured files with source links, extraction errors and review status.
  Use `read_evidence` to inspect a document and `review_document` to record its type, verification
  status, reviewer and note. Types cover IDs, address documents, registrations, contracts and proofs
  of payment. New documents are **Other / Unverified**.
- `export_evidence` creates a ZIP containing selected files, provider-supplied `.eml` messages where
  available, cached message projections and a manifest with sender, recipients, timestamps, original
  IDs and SHA-256 hashes. Identical attachment bytes are stored once with every source occurrence in
  the manifest. Missing provider content produces an explicitly partial export with actionable errors.

Pass a `bettermail://evidence/<record-id>` source URI or its record ID to `read_evidence` to resolve a
captured record in the same installation. These are MCP identifiers; they do not launch a desktop
window. The returned `recordPath` also resolves through the authenticated HTTP endpoint. Captures
survive normal cache pruning and source moves/deletions; removing their account removes them too.
These links identify saved evidence, not a guarantee that the provider's current copy is unchanged.

Coverage reports distinguish inspected messages, searchable documents and incomplete extractions.
Search covers the configured local sync history and captured attachments; it never claims that an
entire provider mailbox was searched. Increase sync history and sync before indexing older mail.
Office extraction covers document XML text, not embedded image OCR or embedded files. OCR may
misread names/numbers; inspect the source before marking a document verified. Reviewer names and
verification statuses are supplied assertions. Hashes identify bytes, not authenticity.

Windows uses installed Windows OCR languages. If no engine is available, install the relevant OCR
language in **Windows Settings > Time & language**, then call `retry_attachment`. Linux/macOS require
Tesseract and Poppler's `pdftoppm` on `PATH` for image/scanned-PDF OCR. Executable paths can be set with
`BETTERMAIL_TESSERACT_PATH` and `BETTERMAIL_PDFTOPPM_PATH`; `BETTERMAIL_OCR_LANGUAGE` selects the installed
Tesseract language (default `eng`). External OCR uses private temporary files and removes them after
processing. Missing engines and unsupported/password-protected files remain visible with errors.

Limits are 25 MiB per indexed attachment, 100 PDF pages/image frames and 200,000 extracted characters,
50 MiB per original MIME message, and 100 selected records / 100 MiB per export. Truncation is explicit.
Paging is a live cache view; restart a search after indexing or exporting to include newly available
text/hashes. Duplicate groups show at most 200 source links and report omitted members.

Use `list_attachments` for captured attachment metadata and `evidence_coverage` for indexing progress.
Each request uses the mailbox allowlist; `review_document` also requires edit access.
Existing tools retain their response shapes; `search_mail_page`, `read_thread_page` and
`list_mailboxes_page` / `list_folders_page` / `list_drafts_page` / `list_busy_page` provide continuation
tokens. Continue until `hasMore` is false; an empty page can still have a continuation token.

`read_attachment` returns base64 chunks of up to 256 KiB. For downloads, append the returned
`evidence/files/<id>` or `evidence/exports/<id>` path to the MCP endpoint URL and send the same
`Authorization: Bearer` header. Access keys never appear in source links. Export downloads expire
after 24 hours; expired encrypted archives are purged at startup and when another export is created.

## Sync and delivery recovery

Settings > Accounts shows each mailbox's last complete mail sync and its latest error. These states
survive restart. Requested sync history is shown explicitly; search remains limited to cached mail.
Use F9 to retry now, or Re-authenticate for a sign-in error. Background retries run every minute.

A send interrupted after submission is held in Busy as **Delivery unconfirmed**. It is not automatically
resent, even after restart. Microsoft 365 can resolve the state when the original message ID still
provides positive sent evidence; a missing draft alone does not prove delivery. Otherwise, check Sent
in your provider and choose **I found it in Sent** or **Return to drafts**. Returning to drafts preserves
the body and attachments; sending again is an explicit action. Requests explicitly rejected by the
provider can retry at the next sync. This does not guarantee exactly-once provider delivery.

## Builds, releases, and updates

`checks.yml` builds and tests pull requests on Windows, Linux and macOS without OAuth secrets,
publishes smoke artifacts, and checks Linux desktop startup. `build.yml` runs on `master` and can also be called by the release workflow. It resolves a build
version from the latest stable release as `<release>-build.<run>.<UTC timestamp>`, then builds
Windows, Linux, and macOS in parallel. Master build artifacts are retained for 14 days.

Push a strict semantic-version tag to publish a release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

`release.yml` runs only for strict `vX.Y.Z` tags. It validates the version, calls `build.yml` with
that exact release version, waits for all three platform builds, and attaches their Velopack
packages to one GitHub release. Installed builds use those release assets for update checks; do
not remove the generated release metadata files.

For a local release package, restore the pinned tool and choose a runtime:

```powershell
dotnet tool restore
./scripts/package-release.ps1 -Runtime win-x64 -Version 1.0.0
```

Supported CI runtimes are `win-x64`, `linux-x64`, and `osx-arm64`. Public production releases
should be code-signed and macOS builds notarized before they are presented as trusted downloads.
See [release signing setup](docs/release-signing.md) for optional CI credentials and validation.

## Microsoft sign-in

> [!IMPORTANT]
> **No Microsoft Entra setup is required for normal use.** BetterMail ships with its own
> multi-tenant public-client registration. Install the app, add an account, and sign in.

BetterMail requests the complete delegated permission set when an account is added or
re-authenticated, avoiding separate consent prompts for Mail, Calendar, People, Tasks, OneDrive,
and OneNote.

Tenant policy can still require administrator consent. Shared-mailbox access and Send As or Send
on behalf rights must already be granted by the mailbox's Microsoft 365 administrator.

<details>
<summary><strong>Custom registrations for rebuilt or redistributed clients</strong></summary>

<br>

Developers distributing a custom build can replace the bundled application ID with their own
multi-tenant public-client registration. Configure it with:

- The **Mobile and desktop applications** platform.
- The `http://localhost` redirect URI.
- Public-client flows enabled.
- The delegated Graph permissions listed below.

| Capability | Delegated permission |
| --- | --- |
| Sign-in | `User.Read` |
| Mail | `Mail.ReadWrite`, `Mail.Send` |
| Shared mailboxes | `Mail.ReadWrite.Shared`, `Mail.Send.Shared` |
| Calendars | `Calendars.ReadWrite` |
| People | `Contacts.ReadWrite` |
| Tasks | `Tasks.ReadWrite` |
| Notes | `Notes.ReadWrite` |
| OneDrive | `Files.ReadWrite` |

Microsoft uses a public desktop client and has no client secret. Configure a local or custom build
with:

```powershell
$env:BETTERMAIL_MICROSOFT_CLIENT_ID = "00000000-0000-0000-0000-000000000000"
dotnet run --project src/BetterMail.App
```

</details>

## Google sign-in

BetterMail uses Google's desktop OAuth flow with PKCE, the system browser, and a random loopback
callback port. Configure local or custom builds with `BETTERMAIL_GOOGLE_CLIENT_ID` and
`BETTERMAIL_GOOGLE_CLIENT_SECRET`. Release builds inject those values from GitHub Actions secrets;
they are not committed to this repository. A desktop client credential embedded at build time is
recoverable from the shipped application and must not be treated as a confidential server secret.
User tokens are saved in the encrypted local database.

The release repository must define these Actions secrets before packaging:

- `BETTERMAIL_MICROSOFT_CLIENT_ID`
- `BETTERMAIL_GOOGLE_CLIENT_ID`
- `BETTERMAIL_GOOGLE_CLIENT_SECRET`

BetterMail requests `gmail.modify`, plus OpenID email/profile scopes. `gmail.modify` is a
[restricted Gmail scope](https://developers.google.com/workspace/gmail/api/auth/scopes), so a
publicly distributed OAuth app must complete Google's consent-screen verification requirements.
Development users added as OAuth test users can sign in before verification.

## Shared mailboxes

Microsoft Graph cannot enumerate every shared mailbox or determine all effective Send As and
Send on behalf grants. Add a shared mailbox by address under its owning account in **Settings >
Accounts**, then choose the permission already granted by the Microsoft 365 administrator.

Choosing a send mode in BetterMail does not grant Exchange permission. Exchange remains
authoritative and BetterMail validates mailbox access before saving it.

## Local data and security

BetterMail stores its database, preferences, token cache, and cached content under the operating
system's local application-data directory in a `BetterMail` folder.

On Windows, the database is normally:

```text
%LOCALAPPDATA%\BetterMail\mail.db
```

The path is resolved through the operating system rather than hard-coded. The SQLite database is
encrypted. First launch automatically generates and securely saves a random database key:
Windows uses DPAPI for the current user, macOS uses Keychain, and Linux uses the desktop Secret
Service keyring through libsecret. Normal onboarding requires no environment variables or database
password. If secure storage is locked or unavailable, BetterMail shows an unlock/retry dialog.

`BETTERMAIL_DATABASE_KEY` remains an optional override for existing installations and custom setups;
its original derivation is unchanged. After removing an old override, BetterMail asks for the previous
database password once, verifies it against the existing database, and remembers it in secure storage.
It does not re-encrypt or reset your mail. If you lose an automatically generated key, restore the
original key and database from a backup; BetterMail will not replace the missing key over existing data.

Remote images remain blocked until explicitly allowed, scripts are removed from HTML mail, and
attachment bytes are normally loaded on demand. Evidence indexing explicitly retains captured
attachments, extracted text, source snapshots and review metadata in the encrypted database.

Removing an account deletes its local cache and locally stored token only. It does not delete
cloud data.

## Project structure

| Project | Responsibility |
| --- | --- |
| `BetterMail.App` | Avalonia desktop shell, workspaces, views, themes, and desktop integration |
| `BetterMail.Core` | Provider contracts, encrypted local store, models, draft reconciliation, and sync engine |
| `BetterMail.Microsoft365` | Microsoft identity, Graph requests, throttling, mail, and workspace adapters |
| `BetterMail.Google` | Google desktop OAuth and Gmail REST adapter |
| `BetterMail.Tests` | xUnit regression and behavior checks |
| `asset-pack` | Canonical logos, desktop icons, web assets, banners, and redacted screenshots |

## Platform notes

- On Ubuntu, install the WebKitGTK fallback: `sudo apt install libwebkit2gtk-4.1-0 libsoup-3.0-0`.
  Debian 13+ can optionally use WPE WebKit (`libwpewebkit-2.0-1`); Ubuntu does not package WPE.
- Linux secure token storage requires Secret Service/libsecret.
- Self-contained builds include the .NET runtime and managed dependencies, but platform graphics,
  credential-store, and WebView libraries remain operating-system dependencies.
- This development build is not a compliance-certified release.

## Contributing

Keep changes focused and run the smallest relevant test plus the solution build before submitting
them. For the complete backlog and implementation status, see [TASKS.md](TASKS.md).

Use the structured [GitHub issue forms](https://github.com/BetterCorp/BetterMail/issues/new/choose)
for bugs, feature requests, and support. See [SUPPORT.md](SUPPORT.md) before including diagnostics.

## License

BetterMail is licensed under the [GNU Affero General Public License v3.0](LICENSE), version 3 only
(`AGPL-3.0-only`).
