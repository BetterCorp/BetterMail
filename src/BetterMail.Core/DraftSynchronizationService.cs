namespace BetterMail.Core;

public enum DraftSyncStatus
{
    Unchanged,
    CreatedRemote,
    UpdatedRemote,
    UpdatedLocal,
    ImportedRemote,
    Conflict,
    MissingRemote,
    UnsupportedAttachment,
    Failed
}

public sealed record DraftSyncItem(
    string LocalDraftId,
    string? ProviderDraftId,
    DraftSyncStatus Status,
    string? Error = null);

public sealed record DraftSyncResult(IReadOnlyList<DraftSyncItem> Items)
{
    public bool HasFailures => Items.Any(static item =>
        item.Status is DraftSyncStatus.Conflict or
            DraftSyncStatus.MissingRemote or
            DraftSyncStatus.UnsupportedAttachment or
            DraftSyncStatus.Failed);
}

public sealed class DraftSynchronizationService(IMailProvider provider, IDraftStore store)
{
    public async Task<DraftSyncResult> SynchronizeAsync(
        MailAccount account,
        Mailbox mailbox,
        CancellationToken cancellationToken = default)
    {
        if (mailbox.AccountId != account.AccountId)
        {
            throw new InvalidOperationException("The mailbox does not belong to this account.");
        }

        // Fetch and validate the complete server snapshot before changing local data.
        var remoteDrafts = await provider.GetDraftsAsync(account, mailbox, cancellationToken).ConfigureAwait(false);
        if (remoteDrafts.Any(draft =>
                draft.AccountId != account.AccountId ||
                draft.MailboxId != mailbox.Id))
        {
            throw new InvalidOperationException("The provider returned a draft owned by another account or mailbox.");
        }
        var remoteById = remoteDrafts.ToDictionary(static draft => draft.ProviderId, StringComparer.Ordinal);
        var localDrafts = (await store.GetLocalDraftsAsync(cancellationToken).ConfigureAwait(false))
            .Where(draft => draft.AccountId == account.AccountId && draft.MailboxId == mailbox.Id)
            .ToArray();
        var mappedRemoteIds = localDrafts
            .Select(static draft => draft.ProviderDraftId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var results = new List<DraftSyncItem>();

        foreach (var local in localDrafts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(local.ProviderDraftId))
                {
                    var created = await provider.CreateDraftAsync(
                        account, mailbox, ToMessage(local), cancellationToken).ConfigureAwait(false);
                    ValidateOwner(account, mailbox, created);
                    await SaveMetadataAsync(local, created, cancellationToken).ConfigureAwait(false);
                    results.Add(new(local.Id, created.ProviderId, DraftSyncStatus.CreatedRemote));
                    continue;
                }

                if (!remoteById.TryGetValue(local.ProviderDraftId, out var remote))
                {
                    results.Add(new(
                        local.Id,
                        local.ProviderDraftId,
                        DraftSyncStatus.MissingRemote,
                        "The server draft was not found; the local draft was kept."));
                    continue;
                }
                if (remote.HasUnsupportedAttachments)
                {
                    results.Add(new(
                        local.Id,
                        remote.ProviderId,
                        DraftSyncStatus.UnsupportedAttachment,
                        "The server draft contains an Outlook item or reference attachment; the local draft was kept."));
                    continue;
                }

                var localChanged = local.SyncedLocalUpdatedAt is null ||
                                   local.UpdatedAt > local.SyncedLocalUpdatedAt;
                var remoteChanged = local.ProviderUpdatedAt is null ||
                                    remote.UpdatedAt > local.ProviderUpdatedAt ||
                                    remote.ETag is not null &&
                                    local.ProviderETag is not null &&
                                    !string.Equals(remote.ETag, local.ProviderETag, StringComparison.Ordinal);
                if (localChanged && remoteChanged)
                {
                    results.Add(new(
                        local.Id,
                        remote.ProviderId,
                        DraftSyncStatus.Conflict,
                        "Both the local and server drafts changed; neither copy was overwritten."));
                }
                else if (localChanged)
                {
                    var updated = await provider.UpdateDraftAsync(
                        account, mailbox, remote.ProviderId, ToMessage(local), cancellationToken).ConfigureAwait(false);
                    ValidateOwner(account, mailbox, updated);
                    await SaveMetadataAsync(local, updated, cancellationToken).ConfigureAwait(false);
                    results.Add(new(local.Id, updated.ProviderId, DraftSyncStatus.UpdatedRemote));
                }
                else if (remoteChanged)
                {
                    await store.SaveLocalDraftAsync(
                        ToLocalDraft(local.Id, remote),
                        cancellationToken).ConfigureAwait(false);
                    results.Add(new(local.Id, remote.ProviderId, DraftSyncStatus.UpdatedLocal));
                }
                else
                {
                    results.Add(new(local.Id, remote.ProviderId, DraftSyncStatus.Unchanged));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new(local.Id, local.ProviderDraftId, DraftSyncStatus.Failed, exception.Message));
            }
        }

        foreach (var remote in remoteDrafts.Where(draft => !mappedRemoteIds.Contains(draft.ProviderId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localId = Guid.NewGuid().ToString("N");
            if (remote.HasUnsupportedAttachments)
            {
                results.Add(new(
                    localId,
                    remote.ProviderId,
                    DraftSyncStatus.UnsupportedAttachment,
                    "The server draft contains an Outlook item or reference attachment and was not imported."));
                continue;
            }
            try
            {
                await store.SaveLocalDraftAsync(ToLocalDraft(localId, remote), cancellationToken).ConfigureAwait(false);
                results.Add(new(localId, remote.ProviderId, DraftSyncStatus.ImportedRemote));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(new(localId, remote.ProviderId, DraftSyncStatus.Failed, exception.Message));
            }
        }

        return new DraftSyncResult(results);
    }

    private Task SaveMetadataAsync(
        LocalDraft local,
        CloudDraft remote,
        CancellationToken cancellationToken) =>
        store.UpdateLocalDraftSyncMetadataAsync(
            local.Id,
            remote.ProviderId,
            local.UpdatedAt,
            remote.UpdatedAt,
            remote.ETag,
            cancellationToken);

    private static void ValidateOwner(MailAccount account, Mailbox mailbox, CloudDraft draft)
    {
        if (draft.AccountId != account.AccountId || draft.MailboxId != mailbox.Id)
        {
            throw new InvalidOperationException("The provider returned a draft owned by another account or mailbox.");
        }
    }

    internal static DraftMessage ToMessage(LocalDraft draft) => new(
        draft.Subject,
        ParseAddresses(draft.To),
        draft.Body,
        draft.IsHtml,
        ParseAddresses(draft.Cc),
        ParseAddresses(draft.Bcc),
        draft.Attachments);

    internal static LocalDraft ToLocalDraft(string localId, CloudDraft draft) => new(
        localId,
        draft.AccountId,
        draft.MailboxId,
        FormatAddresses(draft.Message.To),
        FormatAddresses(draft.Message.Cc ?? []),
        FormatAddresses(draft.Message.Bcc ?? []),
        draft.Message.Subject,
        draft.Message.Body,
        draft.Message.Attachments ?? [],
        draft.UpdatedAt,
        draft.Message.IsHtml,
        draft.ProviderId,
        draft.UpdatedAt,
        draft.UpdatedAt,
        draft.ETag);

    private static IReadOnlyList<MailAddress> ParseAddresses(string value)
    {
        var addresses = new List<MailAddress>();
        foreach (var part in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!System.Net.Mail.MailAddress.TryCreate(part, out var parsed))
            {
                throw new InvalidOperationException($"'{part}' is not a valid email address.");
            }
            addresses.Add(new(parsed.DisplayName, parsed.Address));
        }
        return addresses;
    }

    private static string FormatAddresses(IReadOnlyList<MailAddress> addresses) =>
        string.Join("; ", addresses.Select(static address => address.ToString()));
}
