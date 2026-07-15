using Avalonia.Controls;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MailHeadersWindow : Window
{
    public MailHeadersWindow()
    {
        InitializeComponent();
    }

    public MailHeadersWindow(MailHeadersDocument document) : this() => DataContext = document;
}

public sealed record FriendlyMailHeader(string Title, string Value, string Explanation);

public sealed class MailHeadersDocument
{
    private static readonly IReadOnlyDictionary<string, (string Title, string Explanation)> Names =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["From"] = ("Sender", "The address the message says it came from."),
            ["To"] = ("Recipients", "The primary recipients declared by the sender."),
            ["Cc"] = ("Copied recipients", "Recipients who were copied on the message."),
            ["Subject"] = ("Subject", "The subject supplied by the sender."),
            ["Date"] = ("Sent date", "When the sender says the message was sent."),
            ["Message-ID"] = ("Message ID", "The message's globally unique mail identifier."),
            ["Received"] = ("Delivery hop", "One server-to-server step taken while delivering this message."),
            ["Return-Path"] = ("Bounce address", "Where delivery failures are sent."),
            ["Reply-To"] = ("Reply address", "The address replies should be sent to."),
            ["Authentication-Results"] = ("Authentication checks", "SPF, DKIM and DMARC results reported by the receiving service."),
            ["ARC-Authentication-Results"] = ("Forwarded authentication checks", "Authentication evidence preserved across forwarding services."),
            ["X-MS-Exchange-Organization-SCL"] = ("Microsoft spam confidence", "Microsoft's spam score; higher values are more likely to be spam."),
            ["Content-Type"] = ("Message format", "The MIME format and character encoding used by the message."),
            ["MIME-Version"] = ("MIME version", "The mail-format standard used for attachments and rich content.")
        };

    public MailHeadersDocument(string subject, IReadOnlyList<MailHeader> headers)
    {
        Subject = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject;
        RawText = string.Join(Environment.NewLine, headers.Select(header => $"{header.Name}: {header.Value}"));
        FriendlyHeaders = headers.Select(ToFriendly).ToArray();
    }

    public string Subject { get; }
    public string RawText { get; }
    public IReadOnlyList<FriendlyMailHeader> FriendlyHeaders { get; }

    private static FriendlyMailHeader ToFriendly(MailHeader header)
    {
        if (Names.TryGetValue(header.Name, out var known))
        {
            return new(known.Title, header.Value, known.Explanation);
        }
        var title = string.Join(' ', header.Name.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        return new(title, header.Value, "Additional technical information supplied by a mail server or client.");
    }
}
