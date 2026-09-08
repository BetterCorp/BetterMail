using BetterMail.Core;

namespace BetterMail.App;

public enum ComposeIntent
{
    NewMail,
    Reply,
    ReplyAll,
    Forward
}

public sealed record ComposeRequest(
    string To = "",
    string Subject = "",
    string Body = "",
    string Cc = "",
    string Bcc = "",
    string? DraftId = null,
    string? AccountId = null,
    string? MailboxId = null,
    IReadOnlyList<DraftAttachment>? Attachments = null,
    bool IsHtml = false,
    ComposeIntent Intent = ComposeIntent.NewMail,
    string? ConversationIdentity = null,
    MailImportance Importance = MailImportance.Normal,
    bool IsFlagged = false,
    bool RequestReadReceipt = false,
    bool RequestDeliveryReceipt = false);

public sealed record ComposeSender(MailAccount Account, Mailbox Mailbox)
{
    public string DisplayName => new MailAddress(
        string.IsNullOrWhiteSpace(Mailbox.DisplayName) ? Account.DisplayName : Mailbox.DisplayName,
        Mailbox.Address).ToString() + (Mailbox.IsShared ? " (shared)" : "");
}
