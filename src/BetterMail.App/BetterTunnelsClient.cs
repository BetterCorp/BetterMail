using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BetterMail.Core;

namespace BetterMail.App;

// BetterTunnels v1 HTTP frames; MCP uses Streamable HTTP, so no general-purpose port or WebSocket forwarding.
internal sealed class BetterTunnelsClient : IDisposable
{
    private readonly Uri _server;
    private readonly HttpClient _api = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _local = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal BetterTunnelsClient(Uri? server = null)
    {
        _server = server ?? new("https://connect.tunnels.betterportal.dev");
        _api.DefaultRequestHeaders.UserAgent.ParseAdd("BetterMail/1.0 BetterTunnels/1.0.21");
    }

    internal async Task<BetterTunnelsConfiguration> SignInAsync(Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(_server, "/api/client/auth/start")) { Content = JsonContent.Create(new { deviceName = "BetterMail" }) };
        var start = await ReadJsonAsync(startRequest, timeout.Token);
        var browser = new Uri(start.GetProperty("browserUrl").GetString()!);
        if (browser.Scheme != "https" || !string.IsNullOrEmpty(browser.UserInfo)) throw new InvalidOperationException("BetterTunnels returned an invalid sign-in link.");
        var session = start.GetProperty("sessionId").GetString()!;
        var secret = start.GetProperty("pollSecret").GetString()!;
        openBrowser(browser);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            using var poll = AuthorizedRequest("/api/client/auth/status?sessionId=" + Uri.EscapeDataString(session), secret);
            var result = await ReadJsonAsync(poll, timeout.Token);
            switch (result.GetProperty("status").GetString())
            {
                case "pending": continue;
                case "approved":
                    var token = result.GetProperty("token").GetString();
                    if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("BetterTunnels did not return an account token.");
                    return new(false, token, result.TryGetProperty("bpUserEmail", out var email) ? email.GetString() ?? "" : "");
                default: throw new InvalidOperationException("BetterTunnels sign-in expired or was declined. Sign in again.");
            }
        }
    }

    internal async Task<string> GetPackageAsync(string token, CancellationToken cancellationToken)
    {
        using var request = AuthorizedRequest("/api/client/profile", token);
        var profile = await ReadJsonAsync(request, cancellationToken);
        return profile.GetProperty("package").GetString() ?? "unknown";
    }

    private async Task RequireSeniorAsync(string token, CancellationToken cancellationToken)
    {
        if (await GetPackageAsync(token, cancellationToken) != "senior")
            throw new UnauthorizedAccessException("Public MCP links require the BetterTunnels Senior package.");
    }

    private HttpRequestMessage AuthorizedRequest(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<JsonElement> ReadJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await _api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Sign in to BetterTunnels again; the account session is no longer valid on this network.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("BetterTunnels is temporarily unavailable.");
        await response.Content.LoadIntoBufferAsync(64 * 1024, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        return document.RootElement.Clone();
    }

    internal async Task RunAsync(string token, Uri endpoint, Action<string, string> changed, CancellationToken cancellationToken)
    {
        var session = Guid.NewGuid().ToString();
        var delay = 1;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    changed("Checking Senior access…", "");
                    await RequireSeniorAsync(token, cancellationToken);
                    var socketUrl = new UriBuilder(_server)
                    {
                        Scheme = _server.Scheme == "https" ? "wss" : "ws", Path = "/api/client/ws",
                        Query = $"sessionId={session}&targetHost=127.0.0.1&targetPort={endpoint.Port}&clientVersion=1.0.21&authenticated=true&token={Uri.EscapeDataString(token)}"
                    };
                    using var socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                    using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        connect.CancelAfter(TimeSpan.FromSeconds(20));
                        await socket.ConnectAsync(socketUrl.Uri, connect.Token);
                    }
                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var relay = RelayAsync(socket, endpoint, (status, url) => { delay = 1; changed(status, url); }, lifetime.Token);
                    var entitlement = MonitorEntitlementAsync(token, lifetime.Token);
                    try { await await Task.WhenAny(relay, entitlement); }
                    finally
                    {
                        await lifetime.CancelAsync();
                        socket.Abort();
                        try { await Task.WhenAll(relay, entitlement); } catch { /* Observed by the completed task above. */ }
                    }
                }
                catch (UnauthorizedAccessException exception) { changed(exception.Message, ""); return; }
                catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* Never expose token-bearing WebSocket exception URLs. */ }
                cancellationToken.ThrowIfCancellationRequested();
                changed("Public link disconnected. Retrying automatically…", "");
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                delay = Math.Min(delay * 2, 30);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task MonitorEntitlementAsync(string token, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            await RequireSeniorAsync(token, cancellationToken);
        }
    }

    internal async Task RelayAsync(WebSocket socket, Uri endpoint, Action<string, string> changed, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        readiness.CancelAfter(TimeSpan.FromSeconds(20));
        using var writer = new SemaphoreSlim(1, 1);
        var requests = new Dictionary<string, CancellationTokenSource>();
        var tasks = new List<Task>();
        var buffer = new byte[32 * 1024];
        var ready = false;
        async Task SendAsync(object frame, CancellationToken ct)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
            await writer.WaitAsync(ct);
            try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, ct); }
            finally { writer.Release(); }
        }
        try
        {
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), ready ? lifetime.Token : readiness.Token);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        if (socket.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                            throw new UnauthorizedAccessException("BetterTunnels rejected the connection. Check your account and sign in again.");
                        return;
                    }
                    if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 2 * 1024 * 1024)
                        throw new InvalidDataException("Invalid BetterTunnels frame.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                var frame = JsonSerializer.Deserialize<TunnelFrame>(message.ToArray(), Json) ?? throw new InvalidDataException();
                switch (frame.Type)
                {
                    case "tunnel.ready":
                        if (frame.Validation != "none") throw new UnauthorizedAccessException("Public MCP links require Senior access without visitor validation.");
                        if (!Uri.TryCreate(frame.PublicUrl, UriKind.Absolute, out var publicUrl) || publicUrl.Scheme != "https" ||
                            !publicUrl.Host.EndsWith(".tunnels.betterportal.dev", StringComparison.OrdinalIgnoreCase) ||
                            publicUrl.UserInfo.Length != 0 || !publicUrl.IsDefaultPort || publicUrl.AbsolutePath != "/" || publicUrl.Query.Length != 0 || publicUrl.Fragment.Length != 0)
                            throw new InvalidDataException("Invalid public tunnel URL.");
                        ready = true;
                        changed("Public link connected · Senior", publicUrl.GetLeftPart(UriPartial.Authority) + endpoint.AbsolutePath);
                        break;
                    case "request.start" when ready && !string.IsNullOrEmpty(frame.RequestId):
                        var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        requestLifetime.CancelAfter(TimeSpan.FromMinutes(5));
                        lock (requests)
                        {
                            if (requests.Count >= 32 || requests.ContainsKey(frame.RequestId))
                            {
                                requestLifetime.Dispose();
                                throw new InvalidDataException("Too many tunnel requests.");
                            }
                            requests[frame.RequestId] = requestLifetime;
                        }
                        tasks.RemoveAll(task => task.IsCompleted);
                        tasks.Add(ForwardAsync(frame, requestLifetime));
                        break;
                    case "request.cancel":
                        lock (requests)
                            if (requests.TryGetValue(frame.RequestId ?? "", out var pending)) pending.Cancel();
                        break;
                    case "tunnel.closed": return;
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(tasks);
        }

        async Task ForwardAsync(TunnelFrame frame, CancellationTokenSource requestLifetime)
        {
            try
            {
                using var request = CreateLocalRequest(frame, endpoint);
                using var response = await _local.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestLifetime.Token);
                var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(pair => pair.Key.ToLowerInvariant(), pair => string.Join(", ", pair.Value));
                await SendAsync(new { type = "response.start", frame.RequestId, status = (int)response.StatusCode, headers }, requestLifetime.Token);
                using var body = await response.Content.ReadAsStreamAsync(requestLifetime.Token);
                var chunk = new byte[32 * 1024];
                int count;
                while ((count = await body.ReadAsync(chunk, requestLifetime.Token)) != 0)
                    await SendAsync(new { type = "response.body", frame.RequestId, body = Convert.ToBase64String(chunk, 0, count) }, requestLifetime.Token);
                await SendAsync(new { type = "response.end", frame.RequestId }, requestLifetime.Token);
            }
            catch (Exception)
            {
                try { await SendAsync(new { type = "error", frame.RequestId, message = "BetterMail could not handle this request." }, lifetime.Token); }
                catch { socket.Abort(); }
            }
            finally
            {
                lock (requests)
                {
                    requests.Remove(frame.RequestId!);
                    requestLifetime.Dispose();
                }
            }
        }
    }

    internal static HttpRequestMessage CreateLocalRequest(TunnelFrame frame, Uri endpoint)
    {
        // Exact private endpoint only. Never resolve attacker-provided paths into a local URL.
        if (frame.Path != endpoint.AbsolutePath || frame.Method is not ("GET" or "POST" or "DELETE"))
            throw new InvalidDataException("Invalid MCP request target.");
        var body = Convert.FromBase64String(frame.Body ?? "");
        if (body.Length > 1024 * 1024) throw new InvalidDataException("MCP request is too large.");
        var request = new HttpRequestMessage(new HttpMethod(frame.Method), endpoint) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in frame.Headers ?? [])
        {
            if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("Accept", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Origin", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase) || name.Equals("Last-Event-ID", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(name, value);
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.Content.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.Host = endpoint.Authority;
        return request;
    }

    public void Dispose() { _api.Dispose(); _local.Dispose(); }
}

internal sealed record TunnelFrame(string Type, string? RequestId = null, string? Method = null, string? Path = null,
    Dictionary<string, string>? Headers = null, string? Body = null, string? PublicUrl = null, string? Validation = null);
