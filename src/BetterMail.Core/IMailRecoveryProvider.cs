namespace BetterMail.Core;

/// <summary>Provider lookups for recovering a queued action without relying on subject search.</summary>
public interface IMailRecoveryProvider
{
    // Null means this provider has no exact lookup. Empty means the lookup completed without
    // a match; it does not establish permanent deletion. Return two matches to expose ambiguity.
    Task<IReadOnlyList<MailMessage>?> FindMessagesByIdentityAsync(
        MailAccount account, Mailbox mailbox, string internetMessageId, CancellationToken token = default);

    Task<string> ResolveFolderIdAsync(
        MailAccount account, Mailbox mailbox, string folderId, CancellationToken token = default);
}
