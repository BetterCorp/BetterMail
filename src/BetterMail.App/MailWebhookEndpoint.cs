using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace BetterMail.App;

internal sealed class MailWebhookEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly string _path;
    public Uri Address => new(_app.Urls.Single() + _path);

    public MailWebhookEndpoint(string path, Func<bool> enabled,
        Func<JsonElement, CancellationToken, Task<bool>> receive)
    {
        _path = path;
        var builder = McpEndpoint.CreateHostBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = 1024 * 1024;
        });
        _app = builder.Build();
        _app.MapPost(path, async context =>
        {
            var request = context.Request;
            if (!enabled() || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                request.Host.Host != "127.0.0.1" || request.Host.Port != context.Connection.LocalPort || request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = 403;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (request.Query.Count != 0)
            {
                if (request.Query.Count != 1 || !request.Query.TryGetValue("validationToken", out var token) ||
                    token.Count != 1 || token.ToString().Length is 0 or > 2048)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(token.ToString(), context.RequestAborted);
                return;
            }
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body,
                    new JsonDocumentOptions { MaxDepth = 16 }, context.RequestAborted);
                if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array ||
                    value.GetArrayLength() is 0 or > 100)
                {
                    context.Response.StatusCode = 400;
                    return;
                }
                // Every accepted event is durably marked before acknowledging the delivery.
                context.Response.StatusCode = await receive(value, context.RequestAborted) ? 202 : 403;
            }
            catch (JsonException) { context.Response.StatusCode = 400; }
        });
    }

    internal static HttpRequestMessage CreateLocalRequest(TunnelFrame frame, Uri endpoint)
    {
        var target = frame.Path ?? "";
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[question..];
        if (frame.Method != "POST" || path != endpoint.AbsolutePath || target.Length > 4096 || target.Contains('#'))
            throw new InvalidDataException("Invalid webhook request target.");
        if (query.Length != 0)
        {
            var values = QueryHelpers.ParseQuery(query);
            if (values.Count != 1 || !values.TryGetValue("validationToken", out var token) || token.Count != 1 ||
                token.ToString().Length is 0 or > 2048)
                throw new InvalidDataException("Invalid webhook validation query.");
        }
        var body = Convert.FromBase64String(frame.Body ?? "");
        if (body.Length > 1024 * 1024) throw new InvalidDataException("Webhook request is too large.");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.AbsoluteUri + query))
            { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in frame.Headers ?? [])
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.Content.Headers.TryAddWithoutValidation(name, value);
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation(name, value);
        }
        request.Headers.Host = endpoint.Authority;
        return request;
    }

    public Task StartAsync(CancellationToken ct = default) => _app.StartAsync(ct);
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _app.StopAsync(timeout.Token); }
        finally { await _app.DisposeAsync(); }
    }
}
