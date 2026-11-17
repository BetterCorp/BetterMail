using BetterMail.Core;
using MimeKit;

namespace BetterMail.App;

internal sealed record EmlMessagePreview(
    string Subject,
    string From,
    string To,
    string Cc,
    string Date,
    string Body,
    bool IsHtml,
    IReadOnlyList<MailAttachment> InlineParts,
    IReadOnlyList<MailAttachment> Attachments)
{
    internal static EmlMessagePreview Parse(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var message = MimeMessage.Load(stream);
        if (message.Headers.Count == 0 ||
            (message.From.Count == 0 && message.To.Count == 0 &&
             string.IsNullOrWhiteSpace(message.Subject) && message.Date == DateTimeOffset.MinValue))
        {
            throw new FormatException("The file does not contain email headers.");
        }

        var inlineParts = message.BodyParts.OfType<MimePart>()
            .Where(part => !part.IsAttachment && !string.IsNullOrWhiteSpace(part.ContentId))
            .DistinctBy(part => part.ContentId, StringComparer.OrdinalIgnoreCase)
            .Select(ToAttachment).ToArray();
        var attachments = message.Attachments.Select(ToAttachment).ToArray();
        var html = message.HtmlBody;
        return new EmlMessagePreview(
            string.IsNullOrWhiteSpace(message.Subject) ? "(No subject)" : message.Subject,
            message.From.ToString(), message.To.ToString(), message.Cc.ToString(),
            message.Date == DateTimeOffset.MinValue ? "" : message.Date.ToLocalTime().ToString("f"),
            html ?? message.TextBody ?? "", html is not null, inlineParts, attachments);
    }

    private static MailAttachment ToAttachment(MimeEntity entity)
    {
        using var output = new MemoryStream();
        if (entity is MessagePart nested)
        {
            nested.Message?.WriteTo(output);
        }
        else if (entity is MimePart part)
        {
            part.Content?.DecodeTo(output);
        }
        var name = entity.ContentDisposition?.FileName ?? entity.ContentType.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = entity is MessagePart ? "message.eml" : "attachment";
        var type = entity is MessagePart ? "message/rfc822" : entity.ContentType.MimeType;
        return new MailAttachment("", name, type, output.Length, !entity.IsAttachment,
            entity.ContentId, output.ToArray());
    }
}
