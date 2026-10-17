using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BetterMail.App;
using BetterMail.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BetterMail.Tests;

public sealed class MailWebhookTests
{
    [Fact]
    public async Task PendingVersionsSurviveRestartAndDoNotClearAnEventReceivedDuringSync()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-webhook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "mail.db");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await using (var store = new EncryptedMailStore(path, key))
            {
                await store.InitializeAsync(ct);
                await store.SaveMailWebhookConfigurationAsync("microsoft365", "a", new(Enabled: true), ct);
                await store.MarkMailWebhookPendingAsync("microsoft365", "a", ct);
                var version = await store.GetPendingMailWebhookVersionAsync("microsoft365", "a", ct);
                await store.MarkMailWebhookPendingAsync("microsoft365", "a", ct);
                await store.CompleteMailWebhookVersionAsync("microsoft365", "a", version, ct);
                Assert.True(await store.GetPendingMailWebhookVersionAsync("microsoft365", "a", ct) > version);
                // Subscription renewal writes must not overwrite pending work.
                await store.SaveMailWebhookConfigurationAsync("microsoft365", "a", new(Enabled: true, SubscriptionId: "renewed"), ct);
            }
            await using (var restarted = new EncryptedMailStore(path, key))
            {
                await restarted.InitializeAsync(ct);
                var version = await restarted.GetPendingMailWebhookVersionAsync("microsoft365", "a", ct);
                Assert.True(version > 0);
                await restarted.CompleteMailWebhookVersionAsync("microsoft365", "a", version, ct);
                Assert.Equal(0, await restarted.GetPendingMailWebhookVersionAsync("microsoft365", "a", ct));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PublicMcpAndWebhooksAreIndependentAndDisconnectPreservesChoices(bool publicMcp, bool webhooks)
    {
        await WithStore(async store =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var ct = timeout.Token;
            await using var relay = await RelayServer.StartAsync(ct);
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            await store.SaveAccountAsync(account);
            var provider = new WebhookProvider();
            var completed = Channel.CreateUnbounded<string>();
            var canSync = false;
            await store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, new(Enabled: webhooks));
            using var reserved = new TcpListener(IPAddress.Loopback, 0);
            reserved.Start();
            var port = ((IPEndPoint)reserved.LocalEndpoint).Port;
            reserved.Stop();
            var saved = await store.GetMcpConfigurationAsync();
            await store.SaveMcpConfigurationAsync(new(Enabled: true, Port: port, PublicEnabled: publicMcp));
            await store.SaveBetterTunnelsConfigurationAsync(new(true, "test-token", "senior@example.com", port));
            await using var tunnels = new TunnelsSettingsViewModel(store, () => provider, item =>
            {
                if (!canSync) return Task.FromResult(false);
                completed.Writer.TryWrite(item.AccountId);
                return Task.FromResult(true);
            }, relay.Address);
            await tunnels.InitializeAsync();
            await using var mcp = new McpSettingsViewModel(store, () => Task.CompletedTask, (_, _, _) => Task.CompletedTask, tunnels: tunnels);
            await mcp.InitializeAsync();
            var connection = await relay.Connections.Reader.ReadAsync(ct);
            await UntilAsync(() => tunnels.PublicBaseUrl.Length > 0, ct);
            var mcpBody = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";
            var mcpResult = await connection.RequestAsync(saved.EndpointPath, mcpBody, saved.AccessKey, ct);
            if (publicMcp) Assert.Equal(200, mcpResult.Status);
            else Assert.NotNull(mcpResult.Error);
            var webhookPath = "/webhooks/" + saved.EndpointPath[4..] + "/microsoft365";
            var validation = await connection.RequestAsync(webhookPath + "?validationToken=hello%2Bworld", "", null, ct);
            Assert.Equal(webhooks ? 200 : 403, validation.Status);
            if (webhooks) Assert.Equal("hello+world", validation.Body);
            Assert.NotNull((await connection.RequestAsync(webhookPath + "?bad=token", "", null, ct)).Error);
            Assert.NotNull((await connection.RequestAsync(webhookPath + "/../mcp", "", null, ct)).Error);
            if (webhooks) await UntilAsync(async () =>
                (await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId)).SubscriptionId == "sub", ct);
            var config = await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId);
            var body = JsonSerializer.Serialize(new { value = new[] { new { subscriptionId = config.SubscriptionId, clientState = config.ClientState } } });
            Assert.Equal(webhooks ? 202 : 403, (await connection.RequestAsync(webhookPath, body, null, ct)).Status);
            Assert.Equal(403, (await connection.RequestAsync(webhookPath,
                """{"value":[{"subscriptionId":"sub","clientState":"wrong"}]}""", null, ct)).Status);
            Assert.Equal(403, (await connection.RequestAsync(webhookPath,
                JsonSerializer.Serialize(new { value = new[] { new { subscriptionId = "foreign", clientState = config.ClientState } } }), null, ct)).Status);

            // Public settings do not weaken the independent local MCP bearer check.
            using var http = new HttpClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync(mcp.EndpointUrl, ct)).StatusCode);
            if (webhooks)
            {
                Assert.True(await store.GetPendingMailWebhookVersionAsync(account.ProviderId, account.AccountId) > 0);
                // Lifecycle warnings renew; removal recreates and rotates the validation secret.
                var warning = JsonSerializer.Serialize(new { value = new[] { new { subscriptionId = config.SubscriptionId, clientState = config.ClientState, lifecycleEvent = "reauthorizationRequired" } } });
                Assert.Equal(202, (await connection.RequestAsync(webhookPath, warning, null, ct)).Status);
                await provider.Renewed.Task.WaitAsync(ct);
                warning = JsonSerializer.Serialize(new { value = new[] { new { subscriptionId = config.SubscriptionId, clientState = config.ClientState, lifecycleEvent = "subscriptionRemoved" } } });
                Assert.Equal(202, (await connection.RequestAsync(webhookPath, warning, null, ct)).Status);
                await UntilAsync(async () =>
                {
                    var next = await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId);
                    return next.SubscriptionId == "sub" && next.ClientState != config.ClientState;
                }, ct);
                config = await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId);
                body = JsonSerializer.Serialize(new { value = new[] { new { subscriptionId = config.SubscriptionId, clientState = config.ClientState } } });
                var choice = Assert.Single(tunnels.Accounts);
                choice.Enabled = false;
                await UntilAsync(async () =>
                {
                    var next = await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId);
                    return !next.Enabled && next.SubscriptionId.Length == 0;
                }, ct);
                Assert.Equal(403, (await connection.RequestAsync(webhookPath, body, null, ct)).Status);
                if (publicMcp) Assert.Equal(200, (await connection.RequestAsync(saved.EndpointPath, mcpBody, saved.AccessKey, ct)).Status);
                choice.Enabled = true;
                await UntilAsync(async () =>
                {
                    var next = await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId);
                    return next.Enabled && next.SubscriptionId == "sub";
                }, ct);
                config = await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId);
                body = JsonSerializer.Serialize(new { value = new[] { new { subscriptionId = config.SubscriptionId, clientState = config.ClientState } } });
                canSync = true;
                // Public settings, including the local listener, can be disabled while webhooks keep flowing.
                mcp.Enabled = false;
                mcp.PublicEnabled = false;
                await mcp.ApplyCommand.ExecuteAsync();
                Assert.NotNull((await connection.RequestAsync(saved.EndpointPath, mcpBody, saved.AccessKey, ct)).Error);
                Assert.Equal(202, (await connection.RequestAsync(webhookPath, body, null, ct)).Status);
                Assert.Equal(account.AccountId, await completed.Reader.ReadAsync(ct));
                Assert.Equal(1, relay.ConnectionCount);
            }
            await tunnels.DisconnectCommand.ExecuteAsync();
            Assert.Empty(tunnels.PublicBaseUrl);
            Assert.False((await store.GetBetterTunnelsConfigurationAsync()).Enabled);
            Assert.Equal(webhooks, (await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId)).Enabled);
            Assert.Equal(mcp.PublicEnabled, (await store.GetMcpConfigurationAsync()).Configuration.PublicEnabled);
        });
    }

    [Fact]
    public async Task LegacyPublicMcpChoiceMigratesOnceAndRemainsEnabledAfterDisconnect()
    {
        await WithStore(async store =>
        {
            // No token: migration must not require a network connection.
            await store.SaveBetterTunnelsConfigurationAsync(new(Enabled: true));
            await using (var settings = new McpSettingsViewModel(store, () => Task.CompletedTask, (_, _, _) => Task.CompletedTask))
            {
                await settings.InitializeAsync();
                Assert.True(settings.PublicEnabled);
                await settings.Tunnels.DisconnectCommand.ExecuteAsync();
                Assert.True((await store.GetMcpConfigurationAsync()).Configuration.PublicEnabled);
            }
            await using var restarted = new McpSettingsViewModel(store, () => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
            await restarted.InitializeAsync();
            Assert.True(restarted.PublicEnabled);
        });
    }

    [Fact]
    public async Task AccountWebhookSupportUpdatesAfterProviderRecoveryWithoutRestart()
    {
        await WithStore(async store =>
        {
            var ct = TestContext.Current.CancellationToken;
            var account = new MailAccount("microsoft365", "recovered", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            await store.SaveAccountAsync(account, ct);
            IMailWebhookProvider? provider = null;
            await using var tunnels = new TunnelsSettingsViewModel(store, () => provider);
            await tunnels.InitializeAsync();
            var choice = Assert.Single(tunnels.Accounts);
            Assert.False(choice.CanEnable);
            provider = new WebhookProvider();
            await tunnels.RefreshAccountsAsync();
            Assert.Same(choice, Assert.Single(tunnels.Accounts));
            Assert.True(choice.CanEnable);
        });
    }

    [Fact]
    public async Task SubscriptionCreationRetryPreservesThePersistedValidationSecret()
    {
        await WithStore(async store =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;
            await using var relay = await RelayServer.StartAsync(ct);
            var account = new MailAccount("microsoft365", "lost-create", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            await store.SaveAccountAsync(account, ct);
            await store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, new(Enabled: true), ct);
            await store.SaveBetterTunnelsConfigurationAsync(new(true, "test-token", "senior@example.com"), ct);
            var provider = new WebhookProvider { LoseFirstCreation = true };
            await using var tunnels = new TunnelsSettingsViewModel(store, () => provider, server: relay.Address);
            await tunnels.InitializeAsync();
            await provider.Created.Task.WaitAsync(ct);
            tunnels.NotifySyncIdle();
            await UntilAsync(async () =>
                (await store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, ct)).SubscriptionId == "sub", ct);
            Assert.Equal(2, provider.CreationStates.Count);
            Assert.Equal(provider.CreationStates[0], provider.CreationStates[1]);
        });
    }

    [Fact]
    public async Task FailedSubscriptionMaintenanceDoesNotBlockAcceptedRefreshWork()
    {
        await WithStore(async store =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;
            var account = new MailAccount("microsoft365", "maintenance-failure", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            await store.SaveAccountAsync(account, ct);
            await store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId,
                new(Enabled: true, SubscriptionId: "orphan"), ct);
            await store.MarkMailWebhookPendingAsync(account.ProviderId, account.AccountId, ct);
            var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new WebhookProvider { FailDeletion = true };
            await using var tunnels = new TunnelsSettingsViewModel(store, () => provider, _ =>
            { synced.TrySetResult(); return Task.FromResult(true); });
            await tunnels.InitializeAsync();
            await synced.Task.WaitAsync(ct);
            await UntilAsync(async () => await store.GetPendingMailWebhookVersionAsync(account.ProviderId, account.AccountId, ct) == 0, ct);
        });
    }

    [Fact]
    public async Task ConcurrentAndRepeatedDisposalWaitsForOneCleanup()
    {
        await WithStore(async store =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;
            var account = new MailAccount("microsoft365", "disposal", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            await store.SaveAccountAsync(account, ct);
            await store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, new(Enabled: true), ct);
            await store.MarkMailWebhookPendingAsync(account.ProviderId, account.AccountId, ct);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var tunnels = new TunnelsSettingsViewModel(store, () => new WebhookProvider(), _ =>
            { started.TrySetResult(); return release.Task; });
            await tunnels.InitializeAsync();
            await started.Task.WaitAsync(ct);
            try
            {
                var first = tunnels.DisposeAsync().AsTask();
                var second = tunnels.DisposeAsync().AsTask();
                Assert.False(second.IsCompleted);
                release.TrySetResult(true);
                await Task.WhenAll(first, second).WaitAsync(ct);
                await tunnels.DisposeAsync();
            }
            finally { release.TrySetResult(true); }
        });
    }

    [Theory]
    [InlineData("/hooks/private?validationToken=a&validationToken=b")]
    [InlineData("/hooks/private?validationToken=a&other=b")]
    [InlineData("//attacker.example/hooks/private")]
    [InlineData("/hooks/private#fragment")]
    [InlineData("/hooks/private/child")]
    [InlineData("/hooks/private/../private")]
    public void WebhookForwardingRejectsUnexpectedTargets(string path)
    {
        Assert.Throws<InvalidDataException>(() => MailWebhookEndpoint.CreateLocalRequest(
            new("request.start", Method: "POST", Path: path), new("http://127.0.0.1:1234/hooks/private")));
    }

    private static async Task WithStore(Func<EncryptedMailStore, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-webhook-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync();
            await action(store);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Task UntilAsync(Func<bool> condition, CancellationToken ct) => UntilAsync(() => Task.FromResult(condition()), ct);
    private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    { while (!await condition()) await Task.Delay(20, ct); }

    private sealed class WebhookProvider : IMailWebhookProvider
    {
        public bool FailDeletion { get; init; }
        public bool LoseFirstCreation { get; init; }
        public List<string> CreationStates { get; } = [];
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsMailWebhooks(MailAccount account) => account.ProviderId == "microsoft365";
        public Task<MailWebhookSubscription> CreateMailWebhookAsync(MailAccount account, string notificationUrl, string clientState, CancellationToken cancellationToken = default)
        {
            CreationStates.Add(clientState);
            Created.TrySetResult();
            if (LoseFirstCreation && CreationStates.Count == 1) throw new HttpRequestException("Simulated lost creation response.");
            return Task.FromResult(new MailWebhookSubscription("sub", DateTimeOffset.UtcNow.AddDays(6)));
        }
        public Task<MailWebhookSubscription> RenewMailWebhookAsync(MailAccount account, string subscriptionId, CancellationToken cancellationToken = default)
        { Renewed.TrySetResult(); return Task.FromResult(new MailWebhookSubscription(subscriptionId, DateTimeOffset.UtcNow.AddDays(6))); }
        public Task DeleteMailWebhookAsync(MailAccount account, string subscriptionId, CancellationToken cancellationToken = default)
        {
            if (FailDeletion) throw new HttpRequestException("Simulated maintenance failure.");
            Deleted.TrySetResult(); return Task.CompletedTask;
        }
    }

    private sealed class RelayServer(WebApplication app) : IAsyncDisposable
    {
        public Uri Address => new(app.Urls.Single());
        public Channel<RelayConnection> Connections { get; } = Channel.CreateUnbounded<RelayConnection>();
        private readonly List<RelayConnection> _connections = [];
        public int ConnectionCount => _connections.Count;
        public static async Task<RelayServer> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var app = builder.Build();
            var server = new RelayServer(app);
            app.UseWebSockets();
            app.MapGet("/api/client/profile", () => Results.Json(new { package = "senior" }));
            app.Map("/api/client/ws", async context =>
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var connection = new RelayConnection(socket);
                server._connections.Add(connection);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "tunnel.ready", validation = "none", publicUrl = "https://test.tunnels.betterportal.dev" }).AsMemory(), WebSocketMessageType.Text, true, ct);
                await server.Connections.Writer.WriteAsync(connection, ct);
                await connection.Done.Task.WaitAsync(ct);
            });
            await app.StartAsync(ct);
            return server;
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var connection in _connections) connection.Done.TrySetResult();
            await app.DisposeAsync();
        }
    }

    private sealed class RelayConnection(WebSocket socket)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<(int Status, string Body, string? Error)> RequestAsync(string path, string body, string? key, CancellationToken ct)
        {
            var headers = new Dictionary<string, string> { ["accept"] = "application/json, text/event-stream", ["content-type"] = "application/json" };
            if (key is not null) headers["authorization"] = "Bearer " + key;
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "request.start", requestId = Guid.NewGuid().ToString(), method = "POST", path, headers, body = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)) }).AsMemory(), WebSocketMessageType.Text, true, ct);
            var status = 0;
            var output = new StringBuilder();
            while (true)
            {
                using var stream = new MemoryStream();
                var bytes = new byte[4096];
                ValueWebSocketReceiveResult received;
                do { received = await socket.ReceiveAsync(bytes.AsMemory(), ct); stream.Write(bytes, 0, received.Count); } while (!received.EndOfMessage);
                using var frame = JsonDocument.Parse(stream.ToArray());
                var root = frame.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "response.start": status = root.GetProperty("status").GetInt32(); break;
                    case "response.body": output.Append(Encoding.UTF8.GetString(Convert.FromBase64String(root.GetProperty("body").GetString()!))); break;
                    case "response.end": return (status, output.ToString(), null);
                    case "error": return (status, output.ToString(), root.GetProperty("message").GetString());
                }
            }
        }
    }
}
