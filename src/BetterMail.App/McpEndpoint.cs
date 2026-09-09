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
    private readonly string _endpointPath;
    public string Address => _app.Urls.Single() + _endpointPath;

    public McpEndpoint(McpMailTools tools, int port, string endpointPath, Func<bool> enabled, Func<string> accessKey)
    {
        if (endpointPath.Length != 68 || !endpointPath.StartsWith("/bm/", StringComparison.Ordinal) ||
            !endpointPath[4..].All(Uri.IsHexDigit))
            throw new ArgumentException("The saved MCP endpoint path is invalid.", nameof(endpointPath));
        _endpointPath = endpointPath;
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
            if (!request.Path.StartsWithSegments(_endpointPath, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
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
        _app.MapMcp(_endpointPath);
        _app.MapGet(_endpointPath + "/evidence/records/{id}", async (string id, CancellationToken cancellationToken) =>
        {
            try { return Results.Json(await tools.ReadEvidence(id, cancellationToken)); }
            catch (ModelContextProtocol.McpException error) { return Results.Json(new { error = error.Message }, statusCode: 404); }
        });
        foreach (var kind in new[] { "files", "exports" })
        {
            var isExport = kind == "exports";
            _app.MapGet(_endpointPath + "/evidence/" + kind + "/{id}", async (string id, CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await tools.DownloadEvidenceAsync(id, isExport, cancellationToken);
                    return Results.File(result.Bytes, "application/octet-stream", result.Name, enableRangeProcessing: true);
                }
                catch (BetterMail.Core.EvidenceException error) { return Results.Json(new { code = error.Code, error = error.Message }, statusCode: 404); }
                catch (ModelContextProtocol.McpException error) { return Results.Json(new { error = error.Message }, statusCode: 403); }
            });
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default) => _app.StartAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await _app.StopAsync(timeout.Token); }
        finally { await _app.DisposeAsync(); }
    }
}
