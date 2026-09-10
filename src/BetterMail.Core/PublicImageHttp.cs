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

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 198 && b[1] is 18 or 19);
    }

    public static async Task<byte[]?> GetAsync(Uri uri, int maximumBytes, CancellationToken token, string? accept = null)
    {
        for (var redirect = 0; redirect < 4; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
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
