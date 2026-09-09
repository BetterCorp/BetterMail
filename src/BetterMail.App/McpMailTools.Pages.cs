using System.ComponentModel;
using BetterMail.Core;
using ModelContextProtocol.Server;

namespace BetterMail.App;

internal sealed partial class McpMailTools
{
    [McpServerTool(Name = "list_mailboxes_page", ReadOnly = true), Description("Paginated allowed mailboxes. If the list changes between pages the cursor is rejected; restart paging. No unselected mailbox is returned.")]
    public Task<object> ListMailboxesPage(string? cursor = null, int pageSize = 50) => EvidenceCall<object>(async () =>
    {
        var mailboxes = await ListMailboxes();
        foreach (var mailbox in mailboxes) Authorize(mailbox.Id);
        return CachedPage(mailboxes, mailbox => mailbox.Id, "mailboxes", cursor, pageSize);
    });

    [McpServerTool(Name = "list_folders_page", ReadOnly = true), Description("Paginated cached folders for an allowed mailbox. Restart paging if the folder list changes. Cached folder metadata may lag provider changes.")]
    public Task<object> ListFoldersPage(string mailboxId, string? cursor = null, int pageSize = 50) => EvidenceCall<object>(async () =>
    {
        var folders = await ListFolders(mailboxId);
        Authorize(mailboxId);
        return CachedPage(folders, folder => folder.ProviderId, "folders:" + mailboxId, cursor, pageSize);
    });

    [McpServerTool(Name = "list_drafts_page", ReadOnly = true), Description("Paginated saved draft metadata in an allowed mailbox, excluding queued/deleted drafts and attachment bytes. Restart paging if drafts change.")]
    public Task<object> ListDraftsPage(string mailboxId, string? cursor = null, int pageSize = 50) => EvidenceCall<object>(async () =>
    {
        Authorize(mailboxId);
        var drafts = (await store.GetLocalDraftSummariesAsync()).Where(draft => draft.MailboxId == mailboxId && !draft.IsQueued)
            .Select(draft => new { draft.Id, draft.Subject, draft.To, draft.Cc, draft.Bcc, draft.UpdatedAt, draft.Importance, draft.IsFlagged }).ToArray();
        Authorize(mailboxId);
        return CachedPage(drafts, draft => draft.Id, "drafts:" + mailboxId, cursor, pageSize);
    });

    [McpServerTool(Name = "list_busy_page", ReadOnly = true), Description("Paginated pending Busy actions in an allowed mailbox. Restart paging if the action list changes during sync.")]
    public Task<object> ListBusyPage(string mailboxId, string? cursor = null, int pageSize = 50) => EvidenceCall<object>(async () =>
    {
        var actions = await ListBusy(mailboxId);
        Authorize(mailboxId);
        return CachedPage(actions, action => action.Id, "busy:" + mailboxId, cursor, pageSize);
    });

    private static object CachedPage<T>(IEnumerable<T> source, Func<T, string> key, string kind, string? cursor, int pageSize)
    {
        if (pageSize is < 1 or > 200) throw new EvidenceException("invalid_page_size", "Page size must be between 1 and 200.");
        var all = source.OrderBy(key, StringComparer.Ordinal).ToArray();
        var scope = EvidenceCursor.ScopeFor(new { kind, keys = all.Select(key).ToArray() });
        var continuation = EvidenceCursor.Parse(cursor, scope);
        var offset = continuation?.After ?? 0;
        if (offset > all.Length) throw new EvidenceException("invalid_cursor", "This list changed. Restart from the first page.");
        var items = all.Skip((int)offset).Take(pageSize).ToArray();
        var next = offset + items.Length < all.Length ? new EvidenceCursor(scope, offset + items.Length, all.Length).Encode() : null;
        return new { items, nextCursor = next, hasMore = next is not null, completeness = new { cachedOnly = true, cachedTotal = all.Length, note = "This is the current local list, not proof of provider-wide completeness." } };
    }
}
