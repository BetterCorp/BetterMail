using BetterMail.App;

namespace BetterMail.Tests;

public sealed class MailtoActivationTests
{
    [Fact]
    public void ParsesStandardFieldsAndRepeatedRecipients()
    {
        Assert.True(MailtoParser.TryParse(
            "mailto:first@example.com?to=second%40example.com&cc=copy%40example.com&bcc=blind%40example.com&subject=Hello%20there&body=First%20line%0ASecond%20line",
            out var request));

        Assert.Equal("first@example.com; second@example.com", request.To);
        Assert.Equal("copy@example.com", request.Cc);
        Assert.Equal("blind@example.com", request.Bcc);
        Assert.Equal("Hello there", request.Subject);
        Assert.Equal("First line\nSecond line", request.Body.Replace("\r\n", "\n"));
        Assert.False(request.IsHtml);
    }

    [Fact]
    public void RejectsOtherSchemesAndIgnoresUnsupportedHeaders()
    {
        Assert.False(MailtoParser.TryParse("https://example.com", out _));
        Assert.True(MailtoParser.TryParse(
            "mailto:person@example.com?from=attacker%40example.com&subject=Safe%0D%0ASubject",
            out var request));
        Assert.Equal("person@example.com", request.To);
        Assert.Equal("Safe Subject", request.Subject);
    }
}
