using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using BetterMail.Core;
using SkiaSharp;
using Svg.Skia;

namespace BetterMail.App;

public sealed record ImageRequest(string Key, Func<CancellationToken, Task<byte[]?>> Load);

internal static class BackgroundImages
{
    private sealed record Cached(byte[]? Bytes, DateTimeOffset Expires);
    private static readonly Dictionary<string, Cached> Cache = new();
    private static readonly SemaphoreSlim Slots = new(4);
    private static readonly object Gate = new();

    public static async Task<byte[]?> GetAsync(ImageRequest request, CancellationToken token, bool background = false)
    {
        lock (Gate)
            if (Cache.TryGetValue(request.Key, out var hit) && hit.Expires > DateTimeOffset.UtcNow) return hit.Bytes;
        // Prefetch never queues ahead of interactive artwork requests.
        if (background)
        {
            if (!await Slots.WaitAsync(0, token)) return null;
        }
        else await Slots.WaitAsync(token);
        try
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(request.Key, out var hit) && hit.Expires > DateTimeOffset.UtcNow) return hit.Bytes;
                if (background)
                {
                    foreach (var key in Cache.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray())
                        Cache.Remove(key);
                    // Do not churn the bounded foreground cache by sweeping a large address book.
                    if (Cache.Count >= 256) return null;
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var bytes = await request.Load(timeout.Token);
            token.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (Cache.Count >= 256) Cache.Remove(Cache.Keys.First());
                Cache[request.Key] = new(bytes, request.Key.StartsWith("contact:", StringComparison.Ordinal)
                    ? bytes is null ? DateTimeOffset.UtcNow.AddDays(1) : DateTimeOffset.MaxValue
                    : DateTimeOffset.UtcNow.AddMinutes(bytes is null ? 15 : 60));
            }
            return bytes;
        }
        finally { Slots.Release(); }
    }

    internal static async Task PrefetchAsync(IEnumerable<ImageRequest> requests, CancellationToken token)
    {
        foreach (var request in requests.DistinctBy(request => request.Key))
        {
            token.ThrowIfCancellationRequested();
            lock (Gate)
            {
                if (Cache.TryGetValue(request.Key, out var hit) && hit.Expires > DateTimeOffset.UtcNow) continue;
                if (Cache.Count >= 256 && Cache.Values.All(value => value.Expires > DateTimeOffset.UtcNow)) return;
            }
            try { await GetAsync(request, token, background: true); }
            catch (Exception) when (!token.IsCancellationRequested) { /* Optional artwork must not fail sync. */ }
            await Task.Delay(100, token);
        }
    }

    // Exact mailbox domains only: custom domains hosted by these providers still
    // have their own identity. Extend this list when another shared service is found.
    private static readonly HashSet<string> SharedMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com",
        "outlook.com", "hotmail.com", "hotmail.co.uk", "hotmail.fr", "hotmail.de",
        "hotmail.it", "hotmail.es", "live.com", "live.co.uk", "live.com.au", "msn.com",
        "yahoo.com", "yahoo.co.uk", "yahoo.co.in", "yahoo.in", "yahoo.ca", "yahoo.com.au",
        "yahoo.fr", "yahoo.de", "yahoo.it", "yahoo.es", "yahoo.co.jp", "ymail.com", "rocketmail.com",
        "icloud.com", "me.com", "mac.com", "aol.com", "aim.com",
        "proton.me", "protonmail.com", "protonmail.ch", "pm.me",
        "fastmail.com", "fastmail.fm", "hey.com", "tuta.com", "tuta.io", "tutanota.com", "tutanota.de", "tutamail.com", "keemail.me",
        "gmx.com", "gmx.net", "gmx.de", "gmx.at", "gmx.ch", "web.de", "mail.com", "email.com",
        "zoho.com", "zohomail.com", "yandex.com", "yandex.ru", "ya.ru", "mail.ru", "inbox.ru", "list.ru", "bk.ru",
        "qq.com", "foxmail.com", "163.com", "126.com", "naver.com", "daum.net", "hanmail.net"
    };

    public static ImageRequest Contact(string email) => new("contact:" + email.Trim().ToLowerInvariant(), token => ContactAsync(email, token, PublicImageHttp.GetAsync));

    internal static async Task<byte[]?> ContactAsync(string email, CancellationToken token,
        Func<Uri, int, CancellationToken, string?, Task<byte[]?>> download)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(email, out var address)) return null;
        var domain = new System.Globalization.IdnMapping().GetAscii(address.Host).ToLowerInvariant();
        if (!domain.Contains('.') || new[] { ".example", ".test", ".invalid", ".localhost" }.Any(domain.EndsWith)) return null;
        var sharedMailService = SharedMailDomains.Contains(domain.TrimEnd('.'));
        var logo = sharedMailService ? null : await DomainArtworkAsync("bimi:" + domain, async () =>
        {
            var dns = await download(new Uri("https://cloudflare-dns.com/dns-query?name=" +
                Uri.EscapeDataString("default._bimi." + domain) + "&type=TXT"), 32768, token, "application/dns-json");
            if (dns is null) return null;
            using var document = JsonDocument.Parse(dns);
            var uri = BimiLogo(document.RootElement);
            return uri is null ? null : Normalize(await download(uri, 256 * 1024, token, null), allowSvg: true);
        }, token);
        if (logo is not null) return logo;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.Address.Trim().ToLowerInvariant()))).ToLowerInvariant();
        var gravatar = await AttemptAsync(async () => Normalize(await download(
            new Uri($"https://www.gravatar.com/avatar/{hash}?s=128&d=404&r=g"), 1024 * 1024, token, null)), token);
        if (sharedMailService) return gravatar;
        return gravatar ?? await DomainArtworkAsync("favicon:" + domain, async () => Normalize(await download(
            new Uri($"https://{domain}/favicon.ico"), 256 * 1024, token, null)), token);
    }

    private static async Task<byte[]?> DomainArtworkAsync(string key, Func<Task<byte[]?>> load, CancellationToken token)
    {
        lock (Gate)
            if (Cache.TryGetValue(key, out var hit) && hit.Expires > DateTimeOffset.UtcNow) return hit.Bytes;
        var bytes = await AttemptAsync(load, token);
        token.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (Cache.Count >= 256) Cache.Remove(Cache.Keys.First());
            Cache[key] = new(bytes, DateTimeOffset.UtcNow.AddMinutes(bytes is null ? 15 : 60));
        }
        return bytes;
    }

    private static async Task<byte[]?> AttemptAsync(Func<Task<byte[]?>> load, CancellationToken token)
    {
        try { return await load(); }
        catch (Exception) when (!token.IsCancellationRequested) { return null; }
    }

    internal static Uri? BimiLogo(JsonElement document)
    {
        if (!document.TryGetProperty("Answer", out var answers)) return null;
        foreach (var answer in answers.EnumerateArray())
        {
            if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 16 ||
                !answer.TryGetProperty("data", out var data)) continue;
            var record = (data.GetString() ?? "").Replace("\" \"", "", StringComparison.Ordinal).Trim('"');
            var tags = record.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tags.FirstOrDefault() != "v=BIMI1") continue;
            var location = tags.FirstOrDefault(tag => tag.StartsWith("l=", StringComparison.Ordinal))?[2..];
            if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
                return uri;
        }
        return null;
    }

    // Decode once on a worker and cache only a small raster image, never remote markup.
    internal static byte[]? Normalize(byte[]? bytes, bool allowSvg = false)
    {
        if (bytes is null || bytes.Length == 0 || bytes.Length > 1024 * 1024) return null;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        using var surface = SKSurface.Create(new SKImageInfo(128, 128));
        if (surface is null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        if (codec is not null)
        {
            if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 16_000_000) return null;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap is null) return null;
            var scale = Math.Min(128f / bitmap.Width, 128f / bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            surface.Canvas.DrawBitmap(bitmap, SKRect.Create((128 - w) / 2, (128 - h) / 2, w, h));
        }
        else if (allowSvg)
        {
            var xml = SafeSvg(bytes);
            if (xml is null) return null;
            using var svg = new SKSvg();
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var picture = svg.Load(input);
            if (picture is null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0) return null;
            var scale = Math.Min(128 / picture.CullRect.Width, 128 / picture.CullRect.Height);
            surface.Canvas.Translate((128 - picture.CullRect.Width * scale) / 2, (128 - picture.CullRect.Height * scale) / 2);
            surface.Canvas.Scale(scale);
            surface.Canvas.Translate(-picture.CullRect.Left, -picture.CullRect.Top);
            surface.Canvas.DrawPicture(picture);
        }
        else return null;
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        return png.ToArray();
    }

    internal static string? SafeSvg(byte[] bytes)
    {
        // BIMI logos are static SVG Tiny PS. Restrict to geometry/presentation; no
        // scripts, fonts, filters, style sheets, references, external images or entities.
        using var input = new MemoryStream(bytes);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 262144 });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != XName.Get("svg", "http://www.w3.org/2000/svg")) return null;
        var allowed = new HashSet<string>(["svg", "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "defs", "linearGradient", "radialGradient", "stop", "title", "desc", "clipPath"]);
        if (document.Nodes().OfType<XProcessingInstruction>().Any()) return null;
        foreach (var element in document.Descendants())
        {
            if (element.Name.NamespaceName != "http://www.w3.org/2000/svg" || !allowed.Contains(element.Name.LocalName)) return null;
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName;
                var value = attribute.Value;
                if (name is "href" or "style" || name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    (value.Contains("url", StringComparison.OrdinalIgnoreCase) && !System.Text.RegularExpressions.Regex.IsMatch(value, @"^url\(#[a-zA-Z0-9_-]+\)$"))) return null;
            }
        }
        return document.ToString(SaveOptions.DisableFormatting);
    }
}
