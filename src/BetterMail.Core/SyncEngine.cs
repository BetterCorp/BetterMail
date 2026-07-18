using System.Collections.Concurrent;

namespace BetterMail.Core;

public sealed class SyncEngine(IMailProvider provider, IMailStore store)
{
    private const string FullHistoryVersion = "all-v2";
    private const string BoundedHistoryVersion = "bounded-v2";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mailboxLocks = new();

    public static string CursorId(string mailboxId, string folderId, int historyDays)
    {
        var historyKey = historyDays > 0
            ? $"{BoundedHistoryVersion}-{historyDays}"
            : FullHistoryVersion;
        return $"{mailboxId}:folder:{folderId}:history:{historyKey}";
    }

    public Task<int> SyncFolderAsync(
        MailAccount account,
        Mailbox mailbox,
        MailFolder folder,
        CancellationToken cancellationToken = default) =>
        SyncFolderAsync(account, mailbox, folder, 0, cancellationToken);

    public async Task<int> SyncFolderAsync(
        MailAccount account,
        Mailbox mailbox,
        MailFolder folder,
        int historyDays,
        CancellationToken cancellationToken = default)
    {
        var cursorId = CursorId(mailbox.Id, folder.ProviderId, historyDays);
        DateTimeOffset? receivedSince = historyDays > 0 ? DateTimeOffset.UtcNow.AddDays(-historyDays) : null;
        var mailboxLock = _mailboxLocks.GetOrAdd(cursorId, static _ => new SemaphoreSlim(1, 1));
        await mailboxLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var cursor = await store.GetSyncCursorAsync(cursorId, cancellationToken).ConfigureAwait(false);
            var changed = 0;

            do
            {
                var page = await provider.SyncFolderAsync(
                    account, mailbox, folder.ProviderId, cursor, receivedSince, cancellationToken).ConfigureAwait(false);
                await store.ApplySyncPageAsync(cursorId, page, cancellationToken).ConfigureAwait(false);
                changed += page.Messages.Count;
                cursor = page.NextCursor;

                if (!page.HasMore)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(cursor))
                {
                    throw new InvalidOperationException("The provider returned another page without a cursor.");
                }
            }
            while (true);

            return changed;
        }
        finally
        {
            mailboxLock.Release();
        }
    }
}
