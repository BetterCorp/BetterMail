using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;

namespace BetterMail.App;

internal sealed class McpEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Address => _app.Urls.Single() + "/mcp";

    public McpEndpoint(McpMailTools tools, int port, Func<bool> enabled, Func<string> accessKey)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = 1024 * 1024;
        });
        builder.Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithTools(tools);
        _app = builder.Build();
        _app.Use(async (context, next) =>
        {
            var request = context.Request;
            var expectedPort = context.Connection.LocalPort;
            if (!enabled() || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
                request.Host.Host is not ("127.0.0.1" or "localhost") || request.Host.Port != expectedPort ||
                (request.Headers.TryGetValue("Origin", out var origin) &&
                 origin.ToString() != $"http://{request.Host}"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var authorization = request.Headers.Authorization.ToString();
            var supplied = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
            var expectedKey = accessKey();
            if (expectedKey.Length != 64 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expectedKey)))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        _app.MapMcp("/mcp");
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => _app.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _app.StopAsync(timeout.Token); }
        finally { await _app.DisposeAsync(); }
    }
}
