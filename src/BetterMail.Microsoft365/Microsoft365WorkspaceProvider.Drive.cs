using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BetterMail.Core;

namespace BetterMail.Microsoft365;

public sealed partial class Microsoft365WorkspaceProvider
{
    public async Task<byte[]?> GetThumbnailAsync(MailAccount account, CloudDriveItem item, CancellationToken cancellationToken = default)
    {
        if (item.IsFolder) return null;
        using var request = await CreateRequestAsync(account, HttpMethod.Get,
            DriveItemEndpoint(account, item) + "/thumbnails?$select=medium", FileScopes, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("value", out var values)) return null;
        foreach (var thumbnail in values.EnumerateArray())
        {
            if (thumbnail.TryGetProperty("medium", out var medium) && medium.TryGetProperty("url", out var url) &&
                Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
                return await PublicImageHttp.GetAsync(uri, 1024 * 1024, cancellationToken);
        }
        return null;
    }

    public async Task<CloudDriveItem> GetDriveItemAsync(MailAccount account, string itemId, CancellationToken cancellationToken = default)
    {
        var endpoint = itemId == "root" ? "me/drive/root" : "me/drive/items/" + Uri.EscapeDataString(itemId);
        using var request = await CreateRequestAsync(account, HttpMethod.Get, endpoint + "?$select=" + DriveItemSelect, FileScopes, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return MapDriveItem(document.RootElement, account);
    }

    public async Task<CloudDriveItem> MoveDriveItemAsync(MailAccount account, CloudDriveItem item, CloudDriveItem? parent, CancellationToken cancellationToken = default)
    {
        parent ??= await GetDriveItemAsync(account, "root", cancellationToken);
        EnsureDriveOwned(account, parent);
        if (!parent.IsFolder) throw new InvalidOperationException("Choose a destination folder.");
        using var document = await SendJsonForResponseAsync(account, HttpMethod.Patch, DriveItemEndpoint(account, item),
            new { parentReference = new { id = parent.ProviderId } }, FileScopes, cancellationToken);
        return MapDriveItem(document.RootElement, account);
    }

    public async Task<CloudDriveItem> UpdateDriveFileAsync(MailAccount account, CloudDriveItem item, Stream content, long length,
        string contentType, string expectedETag, CancellationToken cancellationToken = default)
    {
        if (item.IsFolder || length < 0 || length > DraftAttachment.MaximumSizeBytes || string.IsNullOrWhiteSpace(expectedETag))
            throw new InvalidOperationException("Choose a file with its current ETag and at most 150 MiB of content.");
        using var request = await CreateRequestAsync(account, HttpMethod.Put, DriveItemEndpoint(account, item) + "/content", FileScopes, cancellationToken);
        request.Headers.TryAddWithoutValidation("If-Match", expectedETag);
        request.Content = new StreamContent(new NonDisposingStream(content));
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return MapDriveItem(document.RootElement, account);
    }

    public async Task<DriveDownloadChunk> ReadDriveChunkAsync(MailAccount account, CloudDriveItem item, long offset, int length,
        string expectedETag, CancellationToken cancellationToken = default)
    {
        if (item.IsFolder || offset < 0 || offset > item.Size || length is < 1 or > 262144 ||
            string.IsNullOrWhiteSpace(expectedETag) || item.ETag != expectedETag)
            throw new InvalidOperationException("File changed or byte range is invalid. Read the file metadata again.");
        if (offset == item.Size) return new([], offset, null, item.Size, item.ETag);
        using var request = await CreateRequestAsync(account, HttpMethod.Get, DriveItemEndpoint(account, item) + "/content", FileScopes, cancellationToken);
        request.Headers.Range = new RangeHeaderValue(offset, Math.Min(item.Size - 1, offset + length - 1));
        request.Headers.TryAddWithoutValidation("If-Match", expectedETag);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var range = response.Content.Headers.ContentRange;
        if (response.StatusCode == HttpStatusCode.PartialContent ? range?.From != offset || range.Length != item.Size : offset != 0 || item.Size > length)
            throw new InvalidOperationException("The provider did not honor the requested byte range.");
        var expected = checked((int)Math.Min(length, item.Size - offset));
        var bytes = new byte[expected];
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (await stream.ReadAsync(new byte[1], cancellationToken) != 0) throw new InvalidOperationException("Unexpected download length.");
        var latest = await GetDriveItemAsync(account, item.ProviderId, cancellationToken);
        if (latest.ETag != expectedETag) throw new InvalidOperationException("File changed during download. Restart with the latest ETag.");
        return new(bytes, offset, offset + expected < item.Size ? offset + expected : null, item.Size, item.ETag);
    }

    internal static object ReadOnlyLinkPayload(DateTimeOffset expiresAt, string scope) => new
    {
        type = "view", scope, expirationDateTime = expiresAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"), retainInheritedPermissions = true
    };

    internal static DriveShareLink ValidateReadOnlyLink(JsonElement permission, string scope, DateTimeOffset expiresAt)
    {
        var link = permission.GetProperty("link");
        if (OptionalString(link, "type") != "view" || OptionalString(link, "scope") != scope ||
            !DateTimeOffset.TryParse(OptionalString(permission, "expirationDateTime"), out var actualExpiration) ||
            actualExpiration <= DateTimeOffset.UtcNow || actualExpiration > expiresAt.AddSeconds(1) ||
            !Uri.TryCreate(OptionalString(link, "webUrl"), UriKind.Absolute, out var url) || url.Scheme != "https")
            throw new InvalidOperationException("The account policy did not provide the requested expiring read-only link. No link was added to the draft.");
        return new(RequiredString(permission, "id"), url, actualExpiration, scope);
    }

    public async Task<DriveShareLink> CreateReadOnlyLinkAsync(MailAccount account, CloudDriveItem item, DateTimeOffset expiresAt,
        string scope, IReadOnlyList<string> recipients, CancellationToken cancellationToken = default)
    {
        if (scope is not ("anonymous" or "organization" or "users") || expiresAt <= DateTimeOffset.UtcNow || expiresAt > DateTimeOffset.UtcNow.AddYears(1).AddMinutes(1) ||
            scope == "users" && (recipients.Count == 0 || recipients.Any(email => !System.Net.Mail.MailAddress.TryCreate(email, out _))))
            throw new InvalidOperationException("Choose a sharing audience, valid recipients, and an expiration within one year.");
        using var request = await CreateRequestAsync(account, HttpMethod.Post, DriveItemEndpoint(account, item) + "/createLink", FileScopes, cancellationToken);
        request.Content = JsonContent.Create(ReadOnlyLinkPayload(expiresAt, scope));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var permission = document.RootElement;
        var permissionId = RequiredString(permission, "id");
        try
        {
            var result = ValidateReadOnlyLink(permission, scope, expiresAt);
            var url = result.Url;
            if (scope == "users")
            {
                var share = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url.AbsoluteUri)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                using var granted = await SendJsonForResponseAsync(account, HttpMethod.Post, $"shares/{share}/permission/grant",
                    new { recipients = recipients.Distinct(StringComparer.OrdinalIgnoreCase).Select(email => new { email }), roles = new[] { "read" } }, FileScopes, cancellationToken);
                if (granted.RootElement.TryGetProperty("value", out var results) && results.EnumerateArray().Any(result => result.TryGetProperty("error", out _)))
                    throw new InvalidOperationException("The provider could not grant every recipient access.");
            }
            return result;
        }
        catch
        {
            if (response.StatusCode == HttpStatusCode.Created)
            {
                try { await DeleteAsync(account, DriveItemEndpoint(account, item) + "/permissions/" + Uri.EscapeDataString(permissionId), FileScopes, CancellationToken.None); }
                catch { throw new InvalidOperationException("Sharing did not meet the requested policy, and permission cleanup failed. Check this file’s sharing permissions in Drive; access may still exist. No link was added to the draft."); }
            }
            throw;
        }
    }
}
