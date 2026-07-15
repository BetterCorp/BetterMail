using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class MailReplyRecipientsTests
{
    [Fact]
    public void IncludesCcWithoutLeakingLinkedOrInvalidRecipients()
    {
        var message = Message() with
        {
            From = new MailAddress("Sender", "sender@example.com"),
            To =
            [
                new("Me", "me@example.com"),
                new("Duplicate sender", "SENDER@example.com"),
                new("To", "to@example.com")
            ],
            Cc =
            [
                new("Shared", "shared@example.com"),
                new("Duplicate To", "TO@example.com"),
                new("Cc", "cc@example.com"),
                new("Invalid", "not an address")
            ]
        };

        var recipients = MailReplyRecipients.ReplyAll(
            message,
            ["me@example.com", "shared@example.com"]);

        Assert.Equal(
            ["sender@example.com", "to@example.com", "cc@example.com"],
            recipients);
    }

    private static MailMessage Message() => new(
        "mailbox",
        "message",
        null,
        null,
        "inbox",
        "Subject",
        new MailAddress("", ""),
        [],
        DateTimeOffset.UtcNow,
        "",
        null,
        false,
        true,
        false,
        MailImportance.Normal,
        [],
        null);
}
