using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class MailHeadersDocumentTests
{
    [Fact]
    public void KeepsRawHeadersAndExplainsKnownAndUnknownFields()
    {
        var document = new MailHeadersDocument("Planning", [
            new MailHeader("Authentication-Results", "spf=pass; dkim=pass; dmarc=pass"),
            new MailHeader("X-Custom-Route", "edge-01")
        ]);

        Assert.Contains("Authentication-Results: spf=pass", document.RawText);
        Assert.Equal("Authentication checks", document.FriendlyHeaders[0].Title);
        Assert.Equal("X Custom Route", document.FriendlyHeaders[1].Title);
    }
}
