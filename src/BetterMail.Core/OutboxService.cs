using System.Net;

namespace BetterMail.Core;

// The caller serializes this with draft editing/synchronization for the owning mailbox.
public sealed class OutboxService(IMailProvider provider, EncryptedMailStore store)
{
    public async Task<bool> ReturnToDraftAsync(MailAccount account, Mailbox mailbox, string actionId,
        CancellationToken cancellationToken = default)
    {
        var action = await store.GetMailActionAsync(actionId, cancellationToken).ConfigureAwait(false);
        if (action is not { NeedsSendReview: true }) return false;
        if (action.AccountId != account.AccountId || action.MailboxId != mailbox.Id || mailbox.AccountId != account.AccountId)
            throw new InvalidOperationException("The queued draft belongs to another mailbox.");
        var clearMapping = false;
        if (action.ProviderId is not null)
        {
            // A complete draft listing is authoritative about which IDs are still drafts.
            // Lookup failures leave the queued item and its mapping untouched.
            var drafts = await provider.GetDraftsAsync(account, mailbox, cancellationToken).ConfigureAwait(false);
            if (drafts.Any(draft => draft.AccountId != account.AccountId || draft.MailboxId != mailbox.Id))
                throw new InvalidOperationException("The provider returned a draft owned by another mailbox.");
            clearMapping = drafts.All(draft => draft.ProviderId != action.ProviderId);
        }
        return await store.ReturnUnconfirmedSendToDraftAsync(actionId, clearMapping, cancellationToken).ConfigureAwait(false);
    }

    public async Task ProcessAsync(MailAccount account, Mailbox mailbox, string draftId,
        CancellationToken cancellationToken = default)
    {
        var local = await store.GetLocalDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
        if (local is not { IsQueued: true }) return;
        if (local.AccountId != account.AccountId || local.MailboxId != mailbox.Id || mailbox.AccountId != account.AccountId)
            throw new InvalidOperationException("The queued draft belongs to another mailbox.");
        if (local.SendAccepted)
        {
            await store.DeleteLocalDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
            return;
        }
        var actionId = "send:" + draftId;
        if (await store.StartMailActionAsync(actionId, cancellationToken).ConfigureAwait(false) is null)
        {
            var action = (await store.GetMailActionsAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(action => action.Id == actionId);
            if (action is { NeedsSendReview: true, ProviderId: not null })
            {
                try
                {
                    if (await provider.IsDraftSentAsync(account, mailbox, action.ProviderId, cancellationToken).ConfigureAwait(false))
                    {
                        await store.MarkOutboxSendAcceptedAsync(draftId, cancellationToken).ConfigureAwait(false);
                        await store.DeleteLocalDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // Keep the unresolved send visible. A failed lookup must never authorize a resend.
                }
            }
            return;
        }

        var attempted = false;
        try
        {
            var message = new DraftMessage(local.Subject, MailAddressList.Parse(local.To), local.Body, local.IsHtml,
                MailAddressList.Parse(local.Cc), MailAddressList.Parse(local.Bcc), local.Attachments,
                local.Importance, local.IsFlagged, local.RequestReadReceipt, local.RequestDeliveryReceipt);
            string? providerDraftId = null;
            if (provider.SupportsCloudDraftsFor(account))
            {
                var remote = local.ProviderDraftId is { Length: > 0 } existing
                    ? await provider.UpdateDraftAsync(account, mailbox, existing, message, cancellationToken).ConfigureAwait(false)
                    : await provider.CreateDraftAsync(account, mailbox, message, cancellationToken).ConfigureAwait(false);
                if (remote.AccountId != account.AccountId || remote.MailboxId != mailbox.Id)
                    throw new InvalidOperationException("The provider returned a draft owned by another mailbox.");
                await store.UpdateLocalDraftSyncMetadataAsync(local.Id, remote.ProviderId, local.UpdatedAt,
                    remote.UpdatedAt, remote.ETag, cancellationToken).ConfigureAwait(false);
                providerDraftId = remote.ProviderId;
            }
            // Persist before handing the request to the provider, including the crash window.
            await store.MarkSendAttemptedAsync(draftId, cancellationToken).ConfigureAwait(false);
            attempted = true;
            if (providerDraftId is not null)
                await provider.SendDraftAsync(account, mailbox, providerDraftId, cancellationToken).ConfigureAwait(false);
            else
                await provider.SendAsync(account, mailbox, message, cancellationToken).ConfigureAwait(false);
            await store.MarkOutboxSendAcceptedAsync(draftId).ConfigureAwait(false);
            await store.DeleteLocalDraftAsync(draftId).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (attempted && IsDefiniteRejection(error))
                await store.MarkSendRejectedAsync(draftId, error.Message).ConfigureAwait(false);
            else
                await store.FailMailActionAsync(actionId, error.Message).ConfigureAwait(false);
        }
    }

    private static bool IsDefiniteRejection(Exception error) => error is HttpRequestException
    {
        StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
            HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.RequestEntityTooLarge or
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.TooManyRequests
    };
}
