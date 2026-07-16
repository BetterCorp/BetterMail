using System.Text;
using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class MailContentRendererTests
{
    [Fact]
    public void ConvertsEmbeddedComposeImagesToSafeInlineAttachments()
    {
        var renderer = new MailContentRenderer();
        var outgoing = renderer.PrepareOutgoingHtml(
            "<p>Hello</p><img alt='Logo' src='data:image/png;base64,AQID'>",
            []);

        var image = Assert.Single(outgoing.Attachments);
        Assert.True(image.IsInline);
        Assert.Equal("image/png", image.ContentType);
        Assert.NotNull(image.ContentId);
        Assert.Contains($"cid:{image.ContentId}", outgoing.Html);
        Assert.DoesNotContain("data:image", outgoing.Html);
    }

    [Fact]
    public void BuildsSafeQuotedReplyContextFromTheFullCachedBody()
    {
        var renderer = new MailContentRenderer();
        var message = new MailMessage(
            "mailbox", "message", null, null, "inbox", "Budget <review>",
            new("Sender & Co", "sender@example.com"), [new("Recipient", "to@example.com")],
            new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero), "Preview only",
            "<p>Full original body</p><img src='https://tracker.example/pixel'>", true, true,
            false, MailImportance.Normal, [], null);

        var html = renderer.PrepareQuotedMessageHtml(message);

        Assert.Contains("Full original body", html);
        Assert.DoesNotContain("tracker.example", html);
        Assert.Contains("Budget &lt;review&gt;", html);
        Assert.Contains("sender@example.com", html);
        Assert.Contains("Preview only", renderer.PrepareQuotedMessageHtml(message with { Body = null }));
    }

    [Fact]
    public void DetectsHtmlWhenCachedContentTypeIsWrong()
    {
        var renderer = new MailContentRenderer();

        var html = Decode(renderer.Render("<html><body><strong>Hello</strong></body></html>", isHtml: false));

        Assert.Contains("<strong>Hello</strong>", html);
        Assert.DoesNotContain("&lt;html", html);
    }

    [Fact]
    public void RemovesExecutableAndRemoteContent()
    {
        var renderer = new MailContentRenderer();
        const string content = "<script>alert(1)</script><img src='https://tracker.example/pixel'><img src='//cdn.example/chart.png' alt='Quarterly chart'><img src='data:image/svg+xml;base64,PHN2Zz4='><a href='javascript:alert(2)'>bad</a><a href='https://example.com/path'>safe link</a><b>safe</b>";
        var uri = renderer.Render(content, isHtml: true);
        var html = Decode(uri);

        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cdn.example", html, StringComparison.OrdinalIgnoreCase);
        Assert.True(renderer.HasRemoteImages(content, isHtml: true));
        Assert.Contains("Blocked picture: Quarterly chart", html);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://example.com/path", html);
        Assert.DoesNotContain("image/svg+xml", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<b>safe</b>", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("max-width: 860px", html);

        var allowedHtml = Decode(renderer.Render(content, isHtml: true, allowRemoteContent: true));
        Assert.Contains("https://tracker.example/pixel", allowedHtml);
        Assert.Contains("https://cdn.example/chart.png", allowedHtml);
        Assert.Contains("img-src data: http: https:", allowedHtml);
    }

    [Fact]
    public void RendersSafeCidImagesAndShowsMissingPlaceholder()
    {
        var attachments = new[]
        {
            new MailAttachment("one", "logo.png", "image/png", 3, true, "logo@example", [1, 2, 3])
        };
        var renderer = new MailContentRenderer();
        const string content = "<img src='cid:logo@example' alt='Logo'><img src='cid:missing@example' alt='Chart'>";
        var uri = renderer.Render(content, isHtml: true, attachments);
        var html = Decode(uri);

        Assert.Contains("data:image/png;base64,AQID", html);
        Assert.True(renderer.HasCidImages(content, isHtml: true));
        Assert.DoesNotContain("missing@example", html);
        Assert.Contains("Inline picture unavailable: Chart", html);
        Assert.DoesNotContain("cid:", html);
    }

    [Fact]
    public void KeepsLinksUsableWhenRemotePicturesAreAllowedAndConstrainSchemes()
    {
        var renderer = new MailContentRenderer();
        const string content = "<a href='https://example.com/read'><img src='https://example.com/chart.png' alt='Chart'>Read report</a><a href='data:text/html,bad'>bad</a><img src='cid:missing' alt='Logo'><img src='data:text/html,bad' alt='Bad'><img src='data:image/png,not-base64' alt='Malformed'>";

        var html = Decode(renderer.Render(content, isHtml: true, allowRemoteContent: true));

        Assert.Contains("https://example.com/read", html);
        Assert.Contains("https://example.com/chart.png", html);
        Assert.DoesNotContain("data:text/html", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cid:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Inline picture unavailable: Logo", html);
        Assert.Contains("Picture unavailable: Bad", html);
        Assert.Contains("Picture unavailable: Malformed", html);
    }

    [Fact]
    public void RendersMalformedHtmlPlainTextQuotesAndSignaturesIntentionally()
    {
        var renderer = new MailContentRenderer();

        var html = Decode(renderer.Render("<div>Hello<div class='gmail_quote'>Earlier<script>bad()</script></div><div class='gmail_signature'>Regards</div>", isHtml: true));
        var plain = Decode(renderer.Render("<script>not markup</script>\nHello & goodbye", isHtml: false));

        Assert.Contains("Hello", html);
        Assert.Contains("gmail_quote", html);
        Assert.Contains("gmail_signature", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;not markup&lt;/script&gt;", plain);
        Assert.Contains("Hello &amp; goodbye", plain);
    }

    [Fact]
    public void UsesTheSelectedApplicationThemeForMailDocuments()
    {
        var renderer = new MailContentRenderer { ThemeMode = "Dark" };
        var dark = Decode(renderer.Render("Hello", isHtml: false));
        renderer.ThemeMode = "Light";
        var light = Decode(renderer.Render("Hello", isHtml: false));

        Assert.Contains("content=" + (char)34 + "dark" + (char)34, dark);
        Assert.Contains("background: #202020", dark);
        Assert.DoesNotContain("body, body *", dark);
        Assert.Contains("body a { color: #75baff; }", dark);
        Assert.Contains("content=" + (char)34 + "light" + (char)34, light);
        Assert.Contains("background: #ffffff", light);
        Assert.DoesNotContain("background-color: transparent !important", light);
        Assert.DoesNotContain("prefers-color-scheme: dark", light);
    }

    [Fact]
    public void PreservesInlineEmailCardsButtonsAndBodyPresentationInDarkMode()
    {
        var renderer = new MailContentRenderer { ThemeMode = "Dark" };
        const string content = """
            <html><body style="font-family:Calibri,'Segoe UI',Arial,sans-serif;color:#333;margin:0;padding:0;background:#f4f6f8">
              <div style="max-width:600px;margin:0 auto;padding:24px">
                <div style="background:#fff;border-radius:10px;overflow:hidden">
                  <div style="background:#19b5fe;padding:22px 28px;color:#fff">Account Statement</div>
                  <a href="https://example.com" style="display:inline-block;background:#19b5fe;color:#fff;padding:12px 28px;border-radius:6px;text-decoration:none;font-weight:bold">View invoices</a>
                </div>
              </div>
            </body></html>
            """;

        var html = Decode(renderer.Render(content, isHtml: true));

        Assert.Contains("mail-original-body", html);
        Assert.Contains("mail-light-content", html);
        Assert.Contains("#f4f6f8", html);
        Assert.Contains("rgba(25, 181, 254, 1)", html);
        Assert.Contains("border-radius: 10px", html);
        Assert.Contains("border-radius: 6px", html);
        Assert.Contains("overflow: hidden", html);
        Assert.Contains("display: inline-block", html);
        Assert.Contains("text-decoration: none", html);
        Assert.Contains("filter: invert(88%) hue-rotate(180deg)", html);
        Assert.DoesNotContain("background-color: transparent !important", html);
    }

    [Fact]
    public void RemovesRemoteImagesHiddenInBackgroundStyles()
    {
        var renderer = new MailContentRenderer();
        var html = Decode(renderer.Render(
            "<div style=\"background:url('https://tracker.example/pixel') #fff;padding:20px\">Safe</div>",
            isHtml: true));

        Assert.DoesNotContain("tracker.example", html);
        Assert.Contains("padding: 20px", html);
    }

    private static string Decode(Uri uri) => Encoding.UTF8.GetString(
        Convert.FromBase64String(uri.OriginalString.Split(',')[1]));
}
