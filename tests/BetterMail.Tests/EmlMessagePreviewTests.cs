using System.Text;
using BetterMail.App;

namespace BetterMail.Tests;

public sealed class EmlMessagePreviewTests
{
    [Fact]
    public void ParsesMimeHeadersBodyAndNestedMessage()
    {
        var eml = """
            From: =?UTF-8?B?Sm9zw6kgQWx2YXJleg==?= <jose@example.com>
            To: Taylor <taylor@example.com>
            Subject: =?UTF-8?B?UmV2aXNpw7Nu?=
            Date: Tue, 17 Nov 2026 12:00:00 +0000
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="outer"

            --outer
            Content-Type: text/plain; charset=utf-8
            Content-Transfer-Encoding: quoted-printable

            Hello, =C3=A9mail
            --outer
            Content-Type: message/rfc822
            Content-Disposition: attachment; filename="forwarded.eml"

            From: sender@example.com
            To: recipient@example.com
            Subject: Forwarded
            Content-Type: text/plain; charset=utf-8

            Nested body
            --outer--
            """;

        var parsed = EmlMessagePreview.Parse(Encoding.UTF8.GetBytes(eml.Replace("\n", "\r\n")));

        Assert.Equal("Revisión", parsed.Subject);
        Assert.Contains("José Alvarez", parsed.From);
        Assert.Contains("taylor@example.com", parsed.To);
        Assert.Contains("Hello, émail", parsed.Body);
        Assert.False(parsed.IsHtml);
        var attached = Assert.Single(parsed.Attachments);
        Assert.Equal("forwarded.eml", attached.Name);
        Assert.Equal("message/rfc822", attached.ContentType);
        Assert.Equal("Forwarded", EmlMessagePreview.Parse(attached.ContentBytes!).Subject);
    }

    [Fact]
    public void EmailDetectionAcceptsExtensionOrMimeTypeAndRejectsUnrelatedFiles()
    {
        Assert.Equal(FilePreviewKind.Email, FilePreviewWindow.PreviewKindFor("forwarded.EML", "application/octet-stream"));
        Assert.Equal(FilePreviewKind.Email, FilePreviewWindow.PreviewKindFor("attachment", "message/rfc822; name=message.eml"));
        Assert.Equal(FilePreviewKind.Unsupported, FilePreviewWindow.PreviewKindFor("message.msg", "application/octet-stream"));
        Assert.Throws<FormatException>(() => EmlMessagePreview.Parse("not an email"u8.ToArray()));
    }

    [Theory]
    [InlineData("/tmp/forwarded.eml", "/tmp/forwarded.eml")]
    [InlineData("file:///tmp/forwarded%20mail.EML", "/tmp/forwarded mail.EML")]
    public void LocalFileActivationFindsEmailFiles(string value, string expected)
    {
        Assert.True(EmlFileActivation.TryGetPath(value, out var path));
        Assert.Equal(expected, path);
        Assert.False(EmlFileActivation.TryGetPath("https://example.com/forwarded.eml", out _));
    }
}
