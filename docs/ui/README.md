# Workspace UI review

This change applies a shared visual treatment to Mail, Calendar, Files, Notes, People, and To Do. It builds on the reliability/onboarding changes merged in PR #2.

## Design references

These are observed patterns in established products, not a formal UI standard:

- [Notion Mail](https://mobbin.com/screens/6cbf9d6c-b0d2-4d89-9ee5-57bbd6fec0cf): quiet navigation, clear unread hierarchy, and a separate reading area.
- [Cloaked inbox](https://mobbin.com/screens/be1d5c4b-2c3a-4173-b04d-5aa37f46609c): distinct navigation, message list, and reading columns.
- [Google Drive](https://mobbin.com/screens/4e58234e-a5d7-4119-8ab6-f4d7dbc4f20d): consistent navigation and search, recognizable file icons, and aligned file metadata.
- [Skiff Calendar](https://mobbin.com/screens/14bee891-46ef-4335-b911-15b68ada21f7): compact controls around a spacious weekly grid.

BetterMail keeps its existing workflows and commands. The implementation introduces labeled vector navigation, light/dark surface colors, consistent search and action styling, keyboard-visible mail quick actions, portable file icons, and a wider calendar sidebar. The empty mail reader's visibility now binds to the main window rather than the nested conversation model, avoiding overlapping placeholders.

## Actual app screenshots

Captured from the real Avalonia views on Linux at 1440 × 960 using fictional offline data. Mail and Notes show overview/navigation states; the capture environment does not reliably render the embedded native web content. These are screenshots, not mockups. Files uses Microsoft 365/OneDrive fixtures; Google Drive integration is not implemented. Disabled controls reflect the read-only fixture. No connected account data or credentials are used.

| Workspace | Light | Dark |
| --- | --- | --- |
| Mail | [Screenshot](screenshots/mail-light.png) | [Screenshot](screenshots/mail-dark.png) |
| Calendar | [Screenshot](screenshots/calendar-light.png) | [Screenshot](screenshots/calendar-dark.png) |
| Files | [Screenshot](screenshots/files-light.png) | [Screenshot](screenshots/files-dark.png) |
| Notes | [Screenshot](screenshots/notes-light.png) | [Screenshot](screenshots/notes-dark.png) |
| People | [Screenshot](screenshots/people-light.png) | [Screenshot](screenshots/people-dark.png) |
| To Do | [Screenshot](screenshots/todos-light.png) | [Screenshot](screenshots/todos-dark.png) |

[390-pixel mail layout](screenshots/mail-phone.png) · [480-pixel minimum height](screenshots/minimum-height.png)

## Reproduce

From the repository root on Linux with the normal Avalonia dependencies, Python 3, and Xvfb installed:

```sh
dotnet build tools/BetterMail.UiPreview -c Release -m:1
xvfb-run -a -s '-screen 0 1440x960x24' dotnet run --project tools/BetterMail.UiPreview -c Release --no-build -- docs/ui/screenshots
```

The preview is a separate development tool, not a production app mode. It loads the real application resources and view models with an offline provider that rejects writes, uses a temporary profile, and captures X11 pixels (no additional Python packages). Calendar samples use the current local day and offset so they remain visible when regenerated. The capture also checks that navigation neither overlaps nor clips at the 480-pixel minimum window height. The preview project is Linux-only and is built by Linux CI; it is not shipped in application packages.

## Responsive sync and workspace follow-up

Navigation stays available while mailbox sync or another workspace load is pending. Calendar, Notes, and To Do retain their loaded trees for unchanged accounts; repeated navigation shares an in-flight load. Explicit refresh remains available. Mail page requests apply only to the current navigation selection. Provider folder discovery, mailbox sync, and workspace-cache refresh run on worker threads; collection changes return to the UI context.

The sync button opens activity details during a running sync, including the current mailbox/folder, completed mailbox count, queued sends, Busy actions, view refresh, draft reconciliation, health reporting, workspace cache, and maintenance. Indeterminate indicators are used where the provider supplies no total.

Settings → Accounts → Mail sidebar includes Up / Down controls for every primary and shared mailbox. Mailboxes can be interleaved independently of their linked account, and each can start collapsed. Both preferences persist across restarts. Separate Workspace up / Workspace down controls order the linked accounts in other workspaces. Generic navigation/search/attachment labels use Drive; provider labels next to an account retain OneDrive or Google Drive as appropriate. This does not add Google Drive provider support.

People uses compact virtualized rows with secondary actions in an overflow menu. Mail and Calendar share a neutral expandable account header. Drive new-folder and rename inputs appear in action flyouts rather than occupying the toolbar.

Additional Mobbin references inspected:

- [Workable people directory](https://mobbin.com/screens/68f0b023-8b26-47bb-858b-ebeb3cf90e45): simple searchable rows with identity and contact information.
- [Twenty contacts](https://mobbin.com/screens/806b70c1-22a1-4278-a736-e5abb6d77f43): compact people list, restrained surfaces, and contextual details.

[Sync activity](screenshots/sync-progress-dark.png) · [New folder](screenshots/drive-new-folder-light.png)

All captures use fictional offline fixtures. The activity screenshot deliberately supplies synthetic progress values to the real UI; it is not a production sync trace or benchmark. The user's reference screenshot is not part of this repository. Responsiveness tests hold provider requests open and verify navigation and retained workspace state; no production-account performance benchmark was performed.


## Save attachments to Drive

Both inline-mail and detached conversation attachment previews offer **Save to Drive** alongside **Save as**. The destination window lists connected accounts with Files capability and their folders, including the account root. Saving uploads the existing attachment bytes in the background, disables repeat submission, and shows an indeterminate progress bar followed by the actual returned filename. Cancel upload (or closing the destination window) requests cancellation; uncertain results never claim that no file was created.

OneDrive is supported in this change. Google Drive authorization and provider support remain deferred. Small OneDrive uploads now request `@microsoft.graph.conflictBehavior=rename`, matching the existing large-file upload session behavior, so duplicate filenames keep both files. See [Microsoft's conflict behavior documentation](https://learn.microsoft.com/en-us/graph/api/resources/driveitem?view=graph-rest-1.0#instance-attributes).

[Attachment viewer](screenshots/attachment-preview-light.png) · [Destination, light](screenshots/attachment-save-drive-light.png) · [Destination, dark](screenshots/attachment-save-drive-dark.png)

These are the actual Avalonia views with fictional offline data. Regression tests cover account/folder selection, root uploads, exact bytes and metadata, duplicate submissions, failure, cancellation, and mail-only account exclusion. No live account upload was performed.

The destination picker exposes an editable filename and blocks upload when the name exceeds 255 characters or its path through the selected folders exceeds 400 characters. Inline validation updates when the filename or destination changes; users can shorten the name or choose a folder nearer the root. Limits follow [Microsoft documentation](https://support.microsoft.com/en-us/onedrive/what-are-file-path-length-limits).

## Bulk selection, compact People, and background artwork

Mail supports Ctrl/Cmd-click, Shift-click, and Ctrl/Cmd+A (all currently loaded rows). The selection count appears above the list. Delete, the toolbar Move menu, and dragging to a folder operate on the selection. Background read/flag updates preserve selected message identities. The offline preview checks native Ctrl-click, Shift-click, Ctrl+A, and selection retention after a message replacement.

People uses compact, single-line rows: name and email remain visible; account provenance is available in the tooltip and actions menu. Arbitrary initial-letter badges are replaced with artwork or a neutral person outline.

Contact artwork is off by default. Enable **Settings → Accounts → People images → Load external contact images** to opt in. Disabling it cancels pending row requests and removes displayed artwork, including cached images. When enabled, contact artwork tries a domain's `default._bimi` TXT logo, then the email's Gravatar, then the domain's `/favicon.ico`. Known shared mailbox domains (including Gmail, Outlook, Yahoo, iCloud, and Proton) use only Gravatar; they skip both BIMI DNS and favicon requests. This is an explicit domain list, not a mail-host lookup, so custom business domains retain their own artwork. These are visual hints, not authenticated sender badges; this does not verify BIMI certificates or message authentication. TXT lookup uses Cloudflare DNS over HTTPS, Gravatar receives a SHA-256 email hash, and image hosts receive HTTPS requests. Requests carry no mailbox credentials. There is no persistent image cache: small raster results and misses are cached in memory. Static SVG geometry is rasterized locally; unsupported/active SVG falls through to the next source. The SVG dependency is pinned to the SkiaSharp 3-compatible release used by Avalonia.

Drive image files request Microsoft Graph's medium thumbnails instead of downloading the full originals. List and grid views use the same asynchronous image control. Artwork is loaded for visible rows with four concurrent workers, bounded downloads, timeouts, a bounded cache, and cancellation when rows leave the viewport. Recycled rows reject stale results. Public image connections reject private addresses and validate redirects; invalid/missing images retain the ordinary file/person icon.

The updated People and Files screenshots use fictional contacts and locally generated thumbnail fixtures. [Bulk selection screenshot](screenshots/mail-multiselect-light.png) shows the real native selection state; disabled write buttons belong to the offline preview provider.

Mail sender images are separately controlled by **Settings → Accounts → Mail images → Show sender images in mail** (off by default). The message list and conversation message headers, including separate message windows, share the contact artwork cache and lookup policy. Disabling Mail images cancels its pending loads and hides the image slots without changing the People setting.

After normal mail and workspace sync finish, an optional background pass warms missing contact artwork from locally cached contacts. Either People images or Mail images must be enabled. It performs one lookup at a time, yields when image workers are busy, and cancels when normal sync restarts or both image settings are disabled. Successful contact images remain reusable while held in the bounded in-memory cache; misses wait 24 hours before retrying. Prefetch stops when the cache is full instead of evicting visible artwork. There is no persistent photo store: restarting the app or foreground cache eviction can require another lookup. Email changes naturally use a different cache key.

People opens from the local contact cache and mail history, then refreshes in the background. Its Table/Cards switch persists across restarts; card rows remain virtualized and adapt from one to four columns. Calendar initializes from cached calendars and events before refreshing remotely, retains calendar visibility choices, and rejects outdated event results after navigation. First use without cached data still needs an initial provider fetch. The message list uses a compact single-line folder/count header, with a second line only for bulk selection.


## Mail navigation, search and mailbox layout

Search uses a labelled Filters button, a compact results header, and rows that stretch across the results panel. Opening the filter form preserves plain terms such as `bob`; focusing a nonempty search field or pressing Enter reruns the search, even while an older provider search is pending. References reviewed in Mobbin: [Front advanced search](https://mobbin.com/screens/43b2e9ff-ac6c-4240-9d4a-201fc24fd597), [Skiff search](https://mobbin.com/screens/cce393ee-75b8-48b4-934d-346f1b1583ab), and [Notion Mail filters](https://mobbin.com/screens/37573e33-3175-4605-9082-f0fc04d436be).

Double-click opens the clicked message immediately and hydrates its cached conversation afterward. Thread headers retain selectable text without an extra down-arrow button. Mail-list refreshes restore selected row identities and preserve scroll unless the user navigates or scrolls meanwhile. Sync-issue drafts have a direct × delete action with per-row progress; deleting another issue does not wait for the previous network deletion.

| Updated UI | Light | Dark |
| --- | --- | --- |
| Search results | [Screenshot](screenshots/search-results-light.png) | [Screenshot](screenshots/search-results-dark.png) |
| Mailbox order and collapse defaults | [Screenshot](screenshots/mailbox-order-light.png) | [Screenshot](screenshots/mailbox-order-dark.png) |
| Draft issue quick actions | [Screenshot](screenshots/draft-issue-actions-light.png) | [Screenshot](screenshots/draft-issue-actions-dark.png) |

The offline preview checks full-width search rows, selection after full reconciliation and individual state updates, and immediate preview opening without a cached thread. All screenshots in this section use fictional data.


## Status badges, People and day agenda

Busy and Sync Issues use compact badges; both disappear when empty. Sync Issues includes conflicts in the same list, distinguished by the existing status labels. Queued actions start in a muted informational state, turn orange after one failed attempt, and red after repeated failures. Their failure count persists across restarts; successful completion removes the queued action. The sync button also reflects consecutive mail/workspace failures and returns to its informational state after recovery. Unconfirmed sends remain red because they require review rather than automatic resending.

People cards highlight only the hovered card. Saved contacts have a labelled Details action; mail-discovered people have a Discovered badge and Save contact action. The editor supports first/last names, email addresses, mobile/work/home phones, company, job title, office/location and notes. **Settings → Accounts → Default account for new contacts** chooses the destination for new contacts and saved discoveries. Existing contacts retain their original owner. Microsoft 365 reads these fields during contact sync and patches only details the user changes; unknown or unchanged fields are omitted from the update. No Google People API is added.

The global search prompt names the current workspace, whose results appear first. Explicit `type:` filters still take precedence. Workspace headers now hold actions instead of duplicate search boxes; Ctrl+F returns to global search. People results opened from search show a clearable filter in their action bar.

The next-event chip opens today's cached agenda with a live Now marker, past/current/upcoming states and links to individual event previews. The preview puts title/date/time and Join meeting before location, organizer, attendees and notes. References reviewed: [ClickUp event popover](https://mobbin.com/screens/c410b28a-f27d-497b-8c03-1cd4418a567c) and [Salesforce event summary](https://mobbin.com/screens/96ccd056-5033-46a6-8e5a-4e34eaadf1f3). The agenda uses local dates/time and includes overlapping overnight events; provider sync refreshes its cache.

| UI | Screenshots |
| --- | --- |
| People cards and discovery | [Hover](screenshots/people-card-hover-light.png) · [Dark](screenshots/people-cards-dark.png) |
| Expanded contact editor | [Light](screenshots/contact-details-light.png) |
| Today agenda | [Light](screenshots/today-agenda-light.png) · [Dark](screenshots/today-agenda-dark.png) |
| Event preview | [Light](screenshots/event-details-light.png) · [Dark](screenshots/event-details-dark.png) |

All examples use fictional data from the offline native preview. Regression tests cover persisted retry counts, badge recovery, default contact ownership, safe partial updates, richer contact search, cached agenda boundaries, selection-dependent updates and keyboard-accessible thread headers.

### Separate mail window actions

**Settings → Mail & notifications → Separate mail windows** provides independent **Stay open** / **Close** choices for Reply, Reply all, Forward, Archive, Delete, Move, junk status, read status, flag and pin. Existing profiles default to Stay open. Close happens after a move/state action is durably queued, before the main-window feedback animation or provider sync finishes. Reply/forward closes the source preview after the composer opens. Cancelling a folder picker or rejecting an action keeps the preview open. Pending work and retry failures remain available in the main window.

The pending-action line is anchored to the bottom of the entire conversation header in both the reading pane and separate previews.

| Change | Preview |
| --- | --- |
| Window action settings (light) | [Screenshot](screenshots/preview-action-settings-light.png) |
| Window action settings (dark) | [Screenshot](screenshots/preview-action-settings-dark.png) |
| Thread header pending action | [Screenshot](screenshots/thread-action-bottom-light.png) |
