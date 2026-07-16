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
    ComposeIntent Intent = ComposeIntent.NewMail);

public sealed record ComposeSender(MailAccount Account, Mailbox Mailbox)
{
    public string DisplayName => Mailbox.IsShared ? $"{Mailbox.Address} (shared)" : Account.EmailAddress;
}
