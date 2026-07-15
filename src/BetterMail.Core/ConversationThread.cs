namespace BetterMail.Core;

public sealed record ConversationThreadMessage(string Identity, MailMessage Message);

public sealed record ConversationThread(
    string Identity,
    string Subject,
    IReadOnlyList<ConversationThreadMessage> Messages)
{
    public DateTimeOffset LatestAt => Messages[^1].Message.ReceivedAt;

    public static IReadOnlyList<ConversationThread> Project(IEnumerable<MailMessage> source) =>
        source
            .Select(message => (ThreadIdentity(message), Item: new ConversationThreadMessage(
                MessageIdentity(message), message)))
            .GroupBy(item => item.Item1, StringComparer.Ordinal)
            .Select(group =>
            {
                var messages = group.Select(item => item.Item)
                    .OrderBy(item => item.Message.ReceivedAt)
                    .ThenBy(item => item.Identity, StringComparer.Ordinal)
                    .ToArray();
                var subject = messages.Last().Message.Subject;
                return new ConversationThread(
                    group.Key,
                    string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject,
                    messages);
            })
            .OrderByDescending(thread => thread.LatestAt)
            .ThenBy(thread => thread.Identity, StringComparer.Ordinal)
            .ToArray();

    public static string ThreadIdentity(MailMessage message)
    {
        var mailbox = message.MailboxId.Trim();
        if (!string.IsNullOrWhiteSpace(message.ConversationId))
        {
            return $"{mailbox}:conversation:{message.ConversationId.Trim()}";
        }
        if (!string.IsNullOrWhiteSpace(message.InternetMessageId))
        {
            return $"{mailbox}:internet:{NormalizeInternetId(message.InternetMessageId)}";
        }
        return $"{mailbox}:message:{MessageIdentity(message)}";
    }

    public static string MessageIdentity(MailMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ProviderId))
        {
            return $"{message.MailboxId}:{message.ProviderId.Trim()}";
        }
        if (!string.IsNullOrWhiteSpace(message.InternetMessageId))
        {
            return $"{message.MailboxId}:{NormalizeInternetId(message.InternetMessageId)}";
        }
        return $"{message.MailboxId}:{message.ReceivedAt.UtcTicks}:{message.From.Address}:{message.Subject}";
    }

    private static string NormalizeInternetId(string value) =>
        value.Trim().Trim('<', '>').ToLowerInvariant();
}
