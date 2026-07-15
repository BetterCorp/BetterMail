using System.Text;
using AngleSharp.Dom;
using BetterMail.Core;
using Ganss.Xss;

namespace BetterMail.App;

public sealed class MailContentRenderer
{
    private static readonly HashSet<string> SafeInlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"
    };

    private readonly HtmlSanitizer _sanitizer;
    private string _themeMode = "System";

    public string ThemeMode
    {
        get => _themeMode;
        set => _themeMode = value is "Light" or "Dark" ? value : "System";
    }

    public MailContentRenderer()
    {
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.IntersectWith(
        [
            "a", "abbr", "address", "article", "b", "blockquote", "body", "br", "caption", "center",
            "cite", "code", "col", "colgroup", "dd", "del", "div", "dl", "dt", "em", "figure",
            "figcaption", "font", "footer", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header",
            "hr", "html", "i", "img", "ins", "kbd", "li", "main", "ol", "p", "pre", "q", "s",
            "section", "small", "span", "strike", "strong", "sub", "sup", "table", "tbody", "td",
            "tfoot", "th", "thead", "tr", "tt", "u", "ul", "var", "wbr"
        ]);
        _sanitizer.AllowedAttributes.IntersectWith(
        [
            "align", "alt", "bgcolor", "border", "cellpadding", "cellspacing", "color", "colspan", "dir",
            "class", "height", "href", "lang", "rowspan", "src", "style", "title", "valign", "width"
        ]);
        _sanitizer.AllowedCssProperties.IntersectWith(
        [
            "background-color", "border", "border-collapse", "border-color", "border-radius", "border-spacing",
            "border-style", "border-width", "box-sizing", "clear", "color", "direction", "display", "float",
            "font", "font-family", "font-size", "font-style", "font-weight", "height", "line-height", "list-style",
            "margin", "margin-bottom", "margin-left", "margin-right", "margin-top", "max-width", "min-width",
            "overflow-wrap", "padding", "padding-bottom", "padding-left", "padding-right", "padding-top",
            "table-layout", "text-align", "text-decoration", "text-indent", "text-transform", "vertical-align",
            "white-space", "width", "word-break", "word-spacing", "word-wrap"
        ]);
        _sanitizer.AllowedAtRules.Clear();
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedSchemes.UnionWith(["http", "https", "cid", "data"]);
    }

    public Uri Render(
        string? content,
        bool isHtml,
        IReadOnlyList<MailAttachment>? attachments = null,
        bool allowRemoteContent = false)
    {
        lock (_sanitizer)
        {
            return RenderCore(content, isHtml, attachments, allowRemoteContent);
        }
    }

    public string PrepareComposeHtml(string? content, bool isHtml)
    {
        lock (_sanitizer)
        {
            if (IsHtmlContent(content, isHtml) || LooksLikeComposeHtml(content))
            {
                return SanitizeHtml(content ?? "", [], allowRemoteContent: true);
            }

            return System.Net.WebUtility.HtmlEncode(content ?? "")
                .Replace("\r\n", "<br>", StringComparison.Ordinal)
                .Replace("\n", "<br>", StringComparison.Ordinal);
        }
    }

    public string SanitizeComposeHtml(string? content)
    {
        lock (_sanitizer)
        {
            return SanitizeHtml(content ?? "", [], allowRemoteContent: true);
        }
    }

    private Uri RenderCore(
        string? content,
        bool isHtml,
        IReadOnlyList<MailAttachment>? attachments = null,
        bool allowRemoteContent = false)
    {
        var renderAsHtml = IsHtmlContent(content, isHtml);
        var body = renderAsHtml
            ? SanitizeHtml(content ?? "", attachments ?? [], allowRemoteContent)
            : $"<pre>{System.Net.WebUtility.HtmlEncode(content ?? "")}</pre>";
        var imagePolicy = allowRemoteContent ? "data: http: https:" : "data:";
        var colorScheme = ThemeMode switch
        {
            "Light" => "light",
            "Dark" => "dark",
            _ => "light dark"
        };
        var canvasStyle = ThemeMode switch
        {
            "Light" => "color: #1b1b1b; background: #ffffff;",
            "Dark" => "color: #f5f5f5; background: #202020;",
            _ => "color: CanvasText; background: Canvas;"
        };
        var darkOverrides = ThemeMode switch
        {
            "Light" => "",
            "Dark" => DarkContentOverrides,
            _ => $"@media (prefers-color-scheme: dark) {{ {DarkContentOverrides} }}"
        };
        var document = $$"""
            <!doctype html>
            <html>
              <head>
                <meta charset="utf-8">
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src {{imagePolicy}}; style-src 'unsafe-inline'">
                <meta name="color-scheme" content="{{colorScheme}}">
                <style>
                  html { font: 14px/1.5 "Segoe UI", system-ui, sans-serif; {{canvasStyle}} }
                  body { margin: 0; padding: 20px 24px 48px; color: inherit; background: inherit; overflow-wrap: anywhere; }
                  img { display: block; max-width: 100%; height: auto; margin: 12px 0; }
                  table { max-width: 100%; border-collapse: collapse; }
                  pre { white-space: pre-wrap; font: inherit; }
                  blockquote { margin: 18px 0 0; padding: 4px 0 4px 14px; border-left: 2px solid #a6a6a6; color: #666; }
                  .gmail_quote, .yahoo_quoted, .moz-cite-prefix { margin-top: 18px; padding-left: 14px; border-left: 2px solid #a6a6a6; color: #666; }
                  .gmail_signature, .moz-signature { margin-top: 18px; padding-top: 10px; border-top: 1px solid #ddd; color: #666; }
                  .mail-image-placeholder { display: inline-block; box-sizing: border-box; max-width: 100%; margin: 4px 0; padding: 3px 7px; border: 1px solid #c8c8c8; border-radius: 2px; background: #f5f5f5; color: #616161; font-size: 12px; line-height: 1.35; }
                  ::-webkit-scrollbar { width: 11px; height: 11px; }
                  ::-webkit-scrollbar-thumb { background: #b5b5b5; border: 3px solid Canvas; border-radius: 6px; }
                  ::-webkit-scrollbar-track { background: Canvas; }
                  {{darkOverrides}}
                </style>
              </head>
              <body>{{body}}</body>
            </html>
            """;
        return new Uri($"data:text/html;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(document))}");
    }

    private const string DarkContentOverrides =
        "body, body * { color: #f5f5f5 !important; background-color: transparent !important; } " +
        "body a, body a * { color: #75baff !important; } " +
        "blockquote, .gmail_quote, .yahoo_quoted, .moz-cite-prefix, .gmail_signature, .moz-signature { color: #b8b8b8 !important; } " +
        ".gmail_signature, .moz-signature { border-top-color: #555; } " +
        ".mail-image-placeholder { border-color: #666; background: #2d2d2d !important; color: #ccc !important; } " +
        "::-webkit-scrollbar-thumb { background: #666; }";

    public bool HasCidImages(string? content, bool isHtml)
    {
        lock (_sanitizer)
        {
            return HasImageSource(content, IsHtmlContent(content, isHtml), static source => source.StartsWith("cid:", StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool HasRemoteImages(string? content, bool isHtml)
    {
        lock (_sanitizer)
        {
            return HasImageSource(content, IsHtmlContent(content, isHtml), IsRemoteSource);
        }
    }

    private string SanitizeHtml(
        string content,
        IReadOnlyList<MailAttachment> attachments,
        bool allowRemoteContent)
    {
        var document = _sanitizer.SanitizeDom(content);
        var inlineImages = attachments
            .Where(static attachment => attachment.IsInline && !string.IsNullOrWhiteSpace(attachment.ContentId))
            .ToDictionary(static attachment => NormalizeContentId(attachment.ContentId!), StringComparer.OrdinalIgnoreCase);

        foreach (var link in document.QuerySelectorAll("a[href]"))
        {
            var href = link.GetAttribute("href")?.Trim();
            if (href?.StartsWith("//", StringComparison.Ordinal) == true)
            {
                href = $"https:{href}";
            }

            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                link.RemoveAttribute("href");
                continue;
            }

            link.SetAttribute("href", uri.AbsoluteUri);
        }

        foreach (var image in document.QuerySelectorAll("img").ToArray())
        {
            var source = image.GetAttribute("src")?.Trim();
            if (IsRemoteSource(source))
            {
                if (!allowRemoteContent)
                {
                    ReplaceImageWithPlaceholder(document, image, "Blocked picture");
                }
                else if (source!.StartsWith("//", StringComparison.Ordinal))
                {
                    image.SetAttribute("src", $"https:{source}");
                }
                continue;
            }

            if (source?.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) == true)
            {
                var contentId = NormalizeContentId(Uri.UnescapeDataString(source[4..]));
                if (inlineImages.TryGetValue(contentId, out var attachment) &&
                    attachment.ContentBytes is { Length: > 0 and <= 10485760 } bytes &&
                    SafeInlineImageTypes.Contains(attachment.ContentType))
                {
                    image.SetAttribute("src", $"data:{attachment.ContentType};base64,{Convert.ToBase64String(bytes)}");
                }
                else
                {
                    ReplaceImageWithPlaceholder(document, image, "Inline picture unavailable");
                }
                continue;
            }

            if (IsSafeDataImage(source))
            {
                continue;
            }

            ReplaceImageWithPlaceholder(document, image, "Picture unavailable");
        }

        return document.Body?.InnerHtml ?? document.DocumentElement?.InnerHtml ?? "";
    }

    private bool HasImageSource(string? content, bool isHtml, Func<string, bool> predicate)
    {
        if (!isHtml || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return _sanitizer.SanitizeDom(content)
            .QuerySelectorAll("img")
            .Select(static image => image.GetAttribute("src")?.Trim())
            .Any(source => source is not null && predicate(source));
    }

    private static bool IsRemoteSource(string? source) => source is not null &&
        (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         source.StartsWith("//", StringComparison.Ordinal));

    private static bool IsSafeDataImage(string? source) => source is not null &&
        source.Length <= 14_000_000 &&
        SafeInlineImageTypes.Any(type =>
            source.StartsWith($"data:{type};base64,", StringComparison.OrdinalIgnoreCase));

    private static void ReplaceImageWithPlaceholder(IDocument document, IElement image, string prefix)
    {
        var label = image.GetAttribute("alt")?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            image.Parent?.RemoveChild(image);
            return;
        }

        var placeholder = document.CreateElement("span");
        placeholder.ClassName = "mail-image-placeholder";
        placeholder.TextContent = $"{prefix}: {label}";
        image.Parent?.ReplaceChild(placeholder, image);
    }

    private static bool IsHtmlContent(string? content, bool providerSaysHtml)
    {
        if (providerSaysHtml || string.IsNullOrWhiteSpace(content))
        {
            return providerSaysHtml;
        }

        var candidate = content.AsSpan().TrimStart();
        return candidate.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith("<head", StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith("<body", StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith("<table", StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith("<div", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeComposeHtml(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return new[] { "<br", "<p", "<div", "<strong", "<b", "<em", "<i", "<u", "<a", "<ul", "<ol", "<li", "<blockquote" }
            .Any(tag => content.Contains(tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeContentId(string contentId) => contentId.Trim().Trim('<', '>');
}
