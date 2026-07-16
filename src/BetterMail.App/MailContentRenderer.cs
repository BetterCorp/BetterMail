using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using BetterMail.Core;
using Ganss.Xss;

namespace BetterMail.App;

public sealed class MailContentRenderer
{
    private static readonly Regex UnsafeStyleUrl = new(
        @"(?:background|background-image)\s*:[^;]*url\s*\([^)]*\)\s*;?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FunctionalColor = new(
        @"rgba?\(\s*(?<red>\d+)\s*,\s*(?<green>\d+)\s*,\s*(?<blue>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> SafeInlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"
    };

    private readonly HtmlSanitizer _sanitizer;
    private readonly HtmlParser _htmlParser = new();
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
            "class", "data-bettermail-signature", "height", "href", "lang", "rowspan", "src", "style", "title", "valign", "width"
        ]);
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedCssProperties.UnionWith(
        [
            "background", "background-color", "border", "border-bottom", "border-bottom-left-radius",
            "border-bottom-right-radius", "border-collapse", "border-color", "border-left", "border-radius",
            "border-right", "border-spacing", "border-style", "border-top", "border-top-left-radius", "border-top-right-radius",
            "border-width", "box-sizing", "clear", "color", "direction", "display", "float",
            "font", "font-family", "font-size", "font-style", "font-weight", "height", "line-height", "list-style",
            "margin", "margin-bottom", "margin-left", "margin-right", "margin-top", "max-width", "min-width",
            "overflow", "overflow-wrap", "padding", "padding-bottom", "padding-left", "padding-right", "padding-top",
            "table-layout", "text-align", "text-decoration", "text-decoration-color", "text-decoration-line",
            "text-decoration-style", "text-indent", "text-transform", "vertical-align",
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

    public string PrepareQuotedMessageHtml(MailMessage message)
    {
        lock (_sanitizer)
        {
            var hasBody = !string.IsNullOrWhiteSpace(message.Body);
            var original = hasBody && IsHtmlContent(message.Body, message.IsHtml)
                ? SanitizeHtml(message.Body ?? "", [], allowRemoteContent: false)
                : System.Net.WebUtility.HtmlEncode(message.Body ?? message.Preview)
                    .Replace("\r\n", "<br>", StringComparison.Ordinal)
                    .Replace("\n", "<br>", StringComparison.Ordinal);
            Func<string?, string?> encode = System.Net.WebUtility.HtmlEncode;
            var cc = message.Cc is { Count: > 0 }
                ? $"<br><b>Cc:</b> {encode(string.Join(", ", message.Cc))}"
                : "";
            return $"<div class=\"bettermail-quoted-message\"><div><b>From:</b> {encode(message.From.ToString())}<br><b>Sent:</b> {encode(message.ReceivedAt.ToLocalTime().ToString("f"))}<br><b>To:</b> {encode(string.Join(", ", message.To))}{cc}<br><b>Subject:</b> {encode(message.Subject)}</div><blockquote style=\"margin:12px 0 0 0;padding-left:12px;border-left:2px solid #a6a6a6\">{original}</blockquote></div>";
        }
    }

    public string SanitizeComposeHtml(string? content)
    {
        lock (_sanitizer)
        {
            return SanitizeHtml(content ?? "", [], allowRemoteContent: true);
        }
    }

    public (string Html, IReadOnlyList<DraftAttachment> Attachments) PrepareOutgoingHtml(
        string? content,
        IReadOnlyList<DraftAttachment> attachments)
    {
        lock (_sanitizer)
        {
            var document = _sanitizer.SanitizeDom(SanitizeHtml(content ?? "", [], allowRemoteContent: true));
            var outgoing = attachments.ToList();
            var imageNumber = 0;
            foreach (var image in document.QuerySelectorAll("img[src]"))
            {
                var source = image.GetAttribute("src")?.Trim();
                if (!TryReadDataImage(source, out var contentType, out var bytes))
                {
                    if (source?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ReplaceImageWithPlaceholder(document, image, "Signature picture unavailable");
                    }
                    continue;
                }
                var contentId = $"signature-{Guid.NewGuid():N}@bettermail";
                var extension = contentType switch
                {
                    "image/jpeg" => "jpg",
                    "image/gif" => "gif",
                    "image/webp" => "webp",
                    "image/bmp" => "bmp",
                    _ => "png"
                };
                outgoing.Add(new DraftAttachment(
                    $"signature-image-{++imageNumber}.{extension}",
                    contentType,
                    bytes,
                    IsInline: true,
                    ContentId: contentId));
                image.SetAttribute("src", $"cid:{contentId}");
            }
            return (document.Body?.InnerHtml ?? document.DocumentElement?.InnerHtml ?? "", outgoing);
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
            ? AdaptDarkText(SanitizeHtml(content ?? "", attachments ?? [], allowRemoteContent))
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
        "body a { color: #75baff; } " +
        ".mail-dark-text { color: #f5f5f5 !important; } " +
        "a.mail-dark-text { color: #75baff !important; } " +
        ".mail-light-content { color: #1b1b1b; filter: invert(88%) hue-rotate(180deg); } " +
        ".mail-light-content img { filter: invert(100%) hue-rotate(180deg); } " +
        "blockquote, .gmail_quote, .yahoo_quoted, .moz-cite-prefix, .gmail_signature, .moz-signature { color: #b8b8b8; } " +
        ".gmail_signature, .moz-signature { border-top-color: #555; } " +
        ".mail-image-placeholder { border-color: #666; background: #2d2d2d !important; color: #ccc !important; } " +
        "::selection { color: #fff; background: #0f6cbd; } " +
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
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "mailto"))
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

        foreach (var element in document.QuerySelectorAll("[style]"))
        {
            var style = element.GetAttribute("style") ?? "";
            var safeStyle = UnsafeStyleUrl.Replace(style, "");
            if (safeStyle != style)
            {
                element.SetAttribute("style", safeStyle);
            }
        }

        PreserveBodyPresentation(document);

        return document.Body?.InnerHtml ?? document.DocumentElement?.InnerHtml ?? "";
    }

    private static void PreserveBodyPresentation(IDocument document)
    {
        var body = document.Body;
        var style = body?.GetAttribute("style")?.Trim();
        var background = body?.GetAttribute("bgcolor")?.Trim();
        var bodyClass = body?.GetAttribute("class")?.Trim();
        if (body is null || string.IsNullOrWhiteSpace(style) && string.IsNullOrWhiteSpace(background) &&
            string.IsNullOrWhiteSpace(bodyClass))
        {
            return;
        }

        var wrapper = document.CreateElement("div");
        wrapper.ClassName = string.IsNullOrWhiteSpace(bodyClass)
            ? "mail-original-body"
            : $"mail-original-body {bodyClass}";
        if (!string.IsNullOrWhiteSpace(background) &&
            (string.IsNullOrWhiteSpace(style) ||
             !style.Contains("background", StringComparison.OrdinalIgnoreCase)))
        {
            style = $"{style?.TrimEnd(';')};background-color:{background}";
        }
        if (!string.IsNullOrWhiteSpace(style))
        {
            wrapper.SetAttribute("style", style);
        }
        if (HasLightBackground(style))
        {
            wrapper.ClassList.Add("mail-light-content");
        }
        foreach (var child in body.ChildNodes.ToArray())
        {
            wrapper.AppendChild(child);
        }
        body.AppendChild(wrapper);
    }

    private string AdaptDarkText(string content)
    {
        var document = _htmlParser.ParseDocument($"<html><body>{content}</body></html>");
        var body = document.Body;
        if (body is null)
        {
            return content;
        }
        foreach (var element in body.QuerySelectorAll("[style], [color]"))
        {
            if (HasDarkForeground(element) && !HasLightBackgroundAtOrAbove(element))
            {
                element.SetAttribute(
                    "class",
                    $"{element.GetAttribute("class")} mail-dark-text".Trim());
            }
        }
        return body.InnerHtml;
    }

    private static bool HasDarkForeground(IElement element)
        => IsDarkColor(StyleValue(element.GetAttribute("style"), "color")) ||
            IsDarkColor(element.GetAttribute("color"));

    private static bool HasLightBackgroundAtOrAbove(IElement element)
    {
        for (IElement? current = element; current is not null; current = current.ParentElement)
        {
            if (current.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            var style = current.GetAttribute("style");
            var background = current.GetAttribute("bgcolor");
            if (HasLightBackground($"{style};background-color:{background}"))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasLightBackground(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return false;
        }
        if (style.Contains("background:white", StringComparison.OrdinalIgnoreCase) ||
            style.Contains("background-color:white", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var background = StyleValue(style, "background-color") ?? StyleValue(style, "background");
        if (background is null)
        {
            return false;
        }
        return TryReadColor(background, out var red, out var green, out var blue) &&
            red * 299 + green * 587 + blue * 114 >= 190_000;
    }

    private static string? StyleValue(string? style, string property)
    {
        foreach (var declaration in (style ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator > 0 &&
                declaration[..separator].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(separator + 1)..].Trim();
            }
        }
        return null;
    }

    private static bool IsDarkColor(string? value) =>
        TryReadColor(value, out var red, out var green, out var blue) &&
        red * 299 + green * 587 + blue * 114 <= 125_000;

    private static bool TryReadColor(string? value, out int red, out int green, out int blue)
    {
        red = green = blue = 0;
        value = value?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (value.Equals("black", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("windowtext", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (value.Equals("white", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("snow", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("ivory", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("whitesmoke", StringComparison.OrdinalIgnoreCase))
        {
            red = green = blue = 255;
            return true;
        }
        if (value[0] != '#')
        {
            var functional = FunctionalColor.Match(value);
            return functional.Success &&
                int.TryParse(functional.Groups["red"].Value, out red) &&
                int.TryParse(functional.Groups["green"].Value, out green) &&
                int.TryParse(functional.Groups["blue"].Value, out blue) &&
                red <= 255 && green <= 255 && blue <= 255;
        }
        var hex = value[1..];
        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Take(3).SelectMany(character => new[] { character, character }));
        }
        if (hex.Length < 6 || !int.TryParse(
                hex[..6],
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var color))
        {
            return false;
        }
        red = color >> 16 & 255;
        green = color >> 8 & 255;
        blue = color & 255;
        return true;
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

    private static bool TryReadDataImage(string? source, out string contentType, out byte[] bytes)
    {
        contentType = "";
        bytes = [];
        if (!IsSafeDataImage(source))
        {
            return false;
        }
        var comma = source!.IndexOf(',');
        if (comma < 0)
        {
            return false;
        }
        contentType = source[5..source.IndexOf(';')].ToLowerInvariant();
        try
        {
            bytes = Convert.FromBase64String(source[(comma + 1)..]);
            return bytes.Length <= 2 * 1024 * 1024;
        }
        catch (FormatException)
        {
            return false;
        }
    }

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
