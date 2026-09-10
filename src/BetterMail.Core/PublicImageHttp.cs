using System.Net;
using System.Net.Sockets;

namespace BetterMail.Core;

/// <summary>Unauthenticated, bounded public image requests. Never forwards account credentials.</summary>
public static class PublicImageHttp
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
            if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
                throw new HttpRequestException("Image host is not public.");
            // Connect to the checked address, preventing a second DNS lookup/rebinding.
            foreach (var address in addresses)
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("Image host could not be reached.");
        }
    }) { Timeout = TimeSpan.FromSeconds(8) };

    // Conservative policy based on the IANA special-purpose registries (2026-09-10):
    // https://www.iana.org/assignments/iana-ipv4-special-registry/
    // https://www.iana.org/assignments/iana-ipv6-special-registry/
    // Block entire special-purpose parent ranges, including any globally reachable
    // exceptions within them. Artwork does not need protocol/transition endpoints.
    private static readonly IPNetwork[] BlockedV4 = new[]
    {
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8",
        "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24",
        "192.31.196.0/24", "192.52.193.0/24", "192.88.99.0/24", "192.168.0.0/16",
        "192.175.48.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
        "224.0.0.0/4", "240.0.0.0/4"
    }.Select(IPNetwork.Parse).ToArray();
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] BlockedV6 = new[]
    {
        "2001::/23", "2001:db8::/32", "2002::/16", "2620:4f:8000::/48", "3fff::/20"
    }.Select(IPNetwork.Parse).ToArray();

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !BlockedV4.Any(network => network.Contains(address)),
            AddressFamily.InterNetworkV6 => address.ScopeId == 0 && GlobalV6.Contains(address) &&
                !BlockedV6.Any(network => network.Contains(address)),
            _ => false
        };
    }

    public static bool IsAllowedUri(Uri uri) => uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0;

    public static async Task<byte[]?> GetAsync(Uri uri, int maximumBytes, CancellationToken token, string? accept = null)
    {
        for (var redirect = 0; redirect < 4; redirect++)
        {
            // Validate the initial URL and every redirect before sending anything.
            if (!IsAllowedUri(uri))
                return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (accept is not null) request.Headers.Accept.ParseAdd(accept);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > maximumBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (output.Length + read > maximumBytes) return null;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        return null;
    }
}
