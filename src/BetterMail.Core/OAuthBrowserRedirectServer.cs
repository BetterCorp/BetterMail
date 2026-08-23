using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BetterMail.Core;

public sealed class OAuthBrowserRedirectServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(10));
    private readonly Task _response;

    private OAuthBrowserRedirectServer(string providerName)
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        SuccessUri = new Uri($"http://127.0.0.1:{port}/success");
        ErrorUri = new Uri($"http://127.0.0.1:{port}/error");
        _response = RespondAsync(providerName, _timeout.Token);
    }

    public Uri SuccessUri { get; }
    public Uri ErrorUri { get; }

    public static OAuthBrowserRedirectServer Start(string providerName) => new(providerName);

    public async ValueTask DisposeAsync()
    {
        _timeout.Cancel();
        _listener.Stop();
        try
        {
            await _response.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _timeout.Dispose();
        }
    }

    private async Task RespondAsync(string providerName, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
                {
                }
                var path = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1);
                if (path is not ("/success" or "/error"))
                {
                    continue;
                }

                var body = Encoding.UTF8.GetBytes(OAuthBrowserPage.Html(providerName, path == "/success"));
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await client.GetStream().WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await client.GetStream().WriteAsync(body, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            _listener.Stop();
        }
    }
}
