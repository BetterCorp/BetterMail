using System.Collections.Concurrent;

namespace BetterMail.Core;

public sealed class SyncEngine(IMailProvider provider, IMailStore store)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mailboxLocks = new();

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
        var historyKey = historyDays > 0 ? historyDays.ToString() : "all";
        var cursorId = $"{mailbox.Id}:folder:{folder.ProviderId}:history:{historyKey}";
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
