using System.Net;

namespace BetterMail.Core;

/// <summary>Identity checks and guarded recovery; a failed lookup never discards mail or authorizes a resend.</summary>
public sealed class MailActionDiagnostics(EncryptedMailStore store, IMailProvider provider)
{
    public async Task<string> CheckAsync(MailAccount account, Mailbox mailbox, string actionId, CancellationToken token = default)
    {
        var action = await store.GetMailActionAsync(actionId, token)
            ?? throw new InvalidOperationException("Action is no longer pending.");
        if (action.AccountId != account.AccountId || action.MailboxId != mailbox.Id || mailbox.AccountId != account.AccountId)
            throw new InvalidOperationException("Action belongs to a different mailbox.");
        if (action.Running) return "This action is currently executing. Check again after it finishes.";
        if (action.NeedsSendReview) return "Delivery is unconfirmed. Check Sent in your provider before returning this to drafts or confirming delivery. No retry has been authorized.";
        if (action.Kind is not (MailActionKind.Move or MailActionKind.UpdateState))
            return "Check Drafts and Sent in your provider. This check does not send, delete, or recreate drafts. " + action.RecoveryGuidance;
        if (action.ProviderId is null) return "No server message ID is saved. Reconnect and sync the account before attempting recovery.";
        var cached = await store.GetMessageAsync(mailbox.Id, action.ProviderId, token);
        foreach (var id in new[] { action.ProviderId }.Concat(action.PreviousProviderIds ?? []).Distinct().Take(10))
        {
            try
            {
                var message = await provider.GetMessageAsync(account, mailbox, id, token);
                if (message.MailboxId != mailbox.Id) throw new InvalidOperationException("Provider returned a different mailbox.");
                if (id == action.ProviderId || (cached?.InternetMessageId is { Length: > 0 } identity && message.InternetMessageId == identity))
                    return Describe(action, message);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        }
        if (string.IsNullOrWhiteSpace(cached?.InternetMessageId))
            return "The saved server IDs were not found. No stable message identity is cached, so a subject match cannot safely identify it. Check the provider mailbox and destination. The pending action has been kept.";
        var results = await provider.SearchMessagesAsync(account, mailbox, cached.Subject, 100, token);
        var matches = results.Where(message => message.MailboxId == mailbox.Id && message.InternetMessageId == cached.InternetMessageId)
            .DistinctBy(message => message.ProviderId).ToArray();
        if (results.Count < 100 && matches.Length == 1) return Describe(action, matches[0]);
        return matches.Length > 1 || results.Count >= 100
            ? "Server search is ambiguous or incomplete. No action was changed. Inspect the message and destination in your provider before deciding what to do."
            : "No exact identity match was found in this bounded server search. That does not prove deletion or delivery. Check the provider mailbox and destination; the pending action is kept.";
    }

    public async Task<bool> TryAutomaticRecoveryAsync(MailAccount account, Mailbox mailbox, string actionId, CancellationToken token = default)
    {
        var candidate = await store.GetMailActionAsync(actionId, token);
        if (candidate?.AccountId != account.AccountId || candidate.MailboxId != mailbox.Id || mailbox.AccountId != account.AccountId) return false;
        var claimed = await store.ClaimAutomaticRecoveryAsync(actionId, token);
        if (claimed is null) return false;
        try
        {
            var result = await RecoverAsync(account, mailbox, actionId, token, snapshot: claimed);
            await store.RecordAutomaticRecoveryAsync(actionId, "Automatic recovery: " + result, token);
            return true;
        }
        catch (Exception error)
        {
            await store.RecordAutomaticRecoveryAsync(actionId, "Automatic recovery could not complete: " + error.Message);
            return false;
        }
    }

    public async Task<string> RecoverAsync(MailAccount account, Mailbox mailbox, string actionId,
        CancellationToken token = default, Action? authorize = null, MailAction? snapshot = null)
    {
        var expected = snapshot ?? await store.GetMailActionAsync(actionId, token)
            ?? throw new InvalidOperationException("Action is no longer pending.");
        if (!expected.CanRecover || expected.AccountId != account.AccountId || expected.MailboxId != mailbox.Id || mailbox.AccountId != account.AccountId)
            throw new InvalidOperationException("Only a failed move or state action in this mailbox can be recovered.");
        var cached = await store.GetMessageAsync(mailbox.Id, expected.ProviderId!, token);
        if (string.IsNullOrWhiteSpace(cached?.InternetMessageId))
            throw new InvalidOperationException("No stable message identity is cached. Recovery cannot use a subject match alone.");
        MailMessage? verified = null;
        try { verified = await provider.GetMessageAsync(account, mailbox, expected.ProviderId!, token); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
        if (verified is null)
        {
            var results = await provider.SearchMessagesAsync(account, mailbox, cached.Subject, 100, token);
            var matches = results.Where(message => message.MailboxId == mailbox.Id && !message.IsDeleted && message.InternetMessageId == cached.InternetMessageId)
                .DistinctBy(message => message.ProviderId).ToArray();
            if (results.Count >= 100 || matches.Length != 1)
                throw new InvalidOperationException("No unique exact identity match in the bounded search. Nothing was changed.");
            verified = await provider.GetMessageAsync(account, mailbox, matches[0].ProviderId, token);
            if (verified.ProviderId != matches[0].ProviderId)
                throw new InvalidOperationException("The message changed during lookup. Check status again.");
        }
        if (verified.MailboxId != mailbox.Id || verified.IsDeleted || verified.InternetMessageId != cached.InternetMessageId)
            throw new InvalidOperationException("The server message does not match the cached identity. Nothing was changed.");
        authorize?.Invoke();
        if (!await store.RecoverMailActionAsync(expected, verified with { Body = verified.Body ?? cached.Body }, cached.InternetMessageId, token))
            throw new InvalidOperationException("The queued action changed or an earlier action must finish. Check status again.");
        return expected.Kind == MailActionKind.Move && verified.FolderId == expected.DestinationId
            ? "The message is already at the destination. The move was confirmed without moving it again."
            : "The server ID was verified and repaired. Retry queued with the original destination and failure history kept.";
    }

    private static string Describe(MailAction action, MailMessage message) =>
        message.FolderId == action.DestinationId && action.Kind == MailActionKind.Move
            ? "The message was found in the requested destination. No further move appears necessary. Use Recover and retry to verify and confirm this move without moving it again."
            : message.ProviderId != action.ProviderId
                ? "The same message was found under a different server ID. Retrying the old ID will still fail. Use Recover and retry to verify its identity again and repair the queued action. No mail was deleted or changed by this check."
                : "The message is still available under its saved server ID. Check that the destination exists and the account has permission, then retry. No action was changed by this check.";
}
