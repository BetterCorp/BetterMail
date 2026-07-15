using BetterMail.Core;

namespace BetterMail.App;

internal static class MailReplyRecipients
{
    public static IReadOnlyList<string> ReplyAll(
        MailMessage message,
        IEnumerable<string> linkedAddresses)
    {
        var ownAddresses = linkedAddresses
            .Select(Normalize)
            .Where(static address => address.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recipients = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { message.From }
                     .Concat(message.To)
                     .Concat(message.Cc ?? []))
        {
            if (!System.Net.Mail.MailAddress.TryCreate(candidate.Address, out var parsed))
            {
                continue;
            }
            var address = Normalize(parsed.Address);
            if (ownAddresses.Contains(address) || !seen.Add(address))
            {
                continue;
            }
            recipients.Add(parsed.Address);
        }
        return recipients;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
