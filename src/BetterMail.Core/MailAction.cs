namespace BetterMail.Core;

// Values are persisted in mail_actions.
public enum MailActionKind { Move = 0, DeleteDraft = 1, Send = 2 }

public sealed record MailAction(
    string Id,
    string AccountId,
    string MailboxId,
    string ItemId,
    MailActionKind Kind,
    string Subject,
    DateTimeOffset CreatedAt,
    string? ProviderId = null,
    string? DestinationId = null,
    string? DestinationName = null,
    bool Running = false,
    bool Accepted = false,
    string? Error = null,
    string[]? PreviousProviderIds = null,
    string? SourceFolderId = null,
    bool SourceWasUnread = false,
    bool SendAttempted = false)
{
    public bool CanCancel => !Running && !Accepted && !SendAttempted;
    public bool NeedsSendReview => Kind == MailActionKind.Send && SendAttempted && !Running && !Accepted;
    public string DisplaySubject => string.IsNullOrWhiteSpace(Subject) ? "(no subject)" : Subject;
    public string ActionText => Kind switch
    {
        MailActionKind.Send => "Send",
        MailActionKind.DeleteDraft => "Delete draft",
        _ => $"Move to {DestinationName}"
    };
    public string StatusText => NeedsSendReview ? "Delivery unconfirmed — check Sent" : Running ? Kind switch
    {
        MailActionKind.Send => "Sending…",
        MailActionKind.DeleteDraft => "Deleting…",
        _ => "Moving…"
    } : Error is null ? "Waiting for sync" : "Retrying next sync";
    public DateTimeOffset LocalCreatedAt => CreatedAt.ToLocalTime();
    public string MailboxAddress => MailboxId[(MailboxId.LastIndexOf(':') + 1)..];
}
