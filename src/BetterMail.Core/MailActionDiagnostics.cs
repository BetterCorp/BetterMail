using System.Net;

namespace BetterMail.Core;

/// <summary>Read-only investigation; a failed lookup never discards mail or authorizes a resend.</summary>
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

    private static string Describe(MailAction action, MailMessage message) =>
        message.FolderId == action.DestinationId && action.Kind == MailActionKind.Move
            ? "The message was found in the requested destination. No further move appears necessary. Verify in your provider before cancelling the obsolete pending action."
            : message.ProviderId != action.ProviderId
                ? "The same message was found under a different server ID. Retrying the old ID will still fail. Locate this message in your provider to perform the intended action, then cancel the obsolete pending action. No mail was deleted or changed by this check."
                : "The message is still available under its saved server ID. Check that the destination exists and the account has permission, then retry. No action was changed by this check.";
}
