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
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BetterMail.Tests;

public sealed class McpTests
{
    [Fact]
    public async Task PrivatePathMigratesOnceAndSurvivesRestartSettingsAndKeyRotation()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-mcp-path-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "mail.db");
        var databaseKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var originalAccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            await using (var initial = new EncryptedMailStore(path, databaseKey)) await initial.InitializeAsync(token);
            // Seed the v0.2.41 schema, before installation-specific paths existed.
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Password = databaseKey, Pooling = false }.ToString()))
            {
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE mcp_settings(id INTEGER PRIMARY KEY CHECK(id = 1), configuration_json TEXT NOT NULL, access_key TEXT NOT NULL);
                    INSERT INTO mcp_settings VALUES(1, $configuration, $key);
                    """;
                command.Parameters.AddWithValue("$configuration", JsonSerializer.Serialize(new McpConfiguration()));
                command.Parameters.AddWithValue("$key", originalAccessKey);
                await command.ExecuteNonQueryAsync(token);
            }
            string endpointPath;
            await using (var store = new EncryptedMailStore(path, databaseKey))
            {
                await store.InitializeAsync(token);
                var saved = await store.GetMcpConfigurationAsync(token);
                endpointPath = saved.EndpointPath;
                Assert.Matches("^/bm/[0-9a-f]{64}$", endpointPath);
                Assert.Equal(originalAccessKey, saved.AccessKey);
                await store.RotateMcpAccessKeyAsync(token);
                await store.SaveMcpConfigurationAsync(saved.Configuration with { Port = 47832, AllowWrites = true }, token);
                var changed = await store.GetMcpConfigurationAsync(token);
                Assert.Equal(endpointPath, changed.EndpointPath);
                Assert.NotEqual(originalAccessKey, changed.AccessKey);
                Assert.Equal(new BetterTunnelsConfiguration(), await store.GetBetterTunnelsConfigurationAsync(token));
                await store.SaveBetterTunnelsConfigurationAsync(new(true, "test-device-token", "test@example.com"), token);
            }
            await using (var restarted = new EncryptedMailStore(path, databaseKey))
            {
                await restarted.InitializeAsync(token);
                var saved = await restarted.GetMcpConfigurationAsync(token);
                Assert.Equal(endpointPath, saved.EndpointPath);
                Assert.Equal(47832, saved.Configuration.Port);
                Assert.Equal(new BetterTunnelsConfiguration(true, "test-device-token", "test@example.com"), await restarted.GetBetterTunnelsConfigurationAsync(token));
            }
            await WithStore(async independent => Assert.NotEqual(endpointPath, (await independent.GetMcpConfigurationAsync()).EndpointPath));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task BetterTunnelsSignInRelayReconnectAndEntitlementPreserveMcpSecurity()
    {
        await WithStore(async store =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            var ct = timeout.Token;
            var saved = await store.GetMcpConfigurationAsync(ct);
            var key = saved.AccessKey;
            var tools = new McpMailTools(store, () => new(Enabled: true), () => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
            await using var endpoint = new McpEndpoint(tools, 0, saved.EndpointPath, () => true, () => key);
            await endpoint.StartAsync(ct);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var server = builder.Build();
            server.UseWebSockets();
            var sockets = Channel.CreateUnbounded<(WebSocket Socket, TaskCompletionSource Done)>();
            var package = "junior";
            server.MapPost("/api/client/auth/start", () => Results.Json(new { sessionId = "test-session", pollSecret = "poll-secret", browserUrl = "https://betterportal.dev/sign-in", expiresAt = DateTimeOffset.UtcNow.AddMinutes(10) }));
            server.MapGet("/api/client/auth/status", (HttpContext context) =>
                context.Request.Headers.Authorization == "Bearer poll-secret" && context.Request.Query["sessionId"] == "test-session"
                    ? Results.Json(new { status = "approved", token = "test-device-token", bpUserEmail = "test@example.com" }) : Results.Unauthorized());
            server.MapGet("/api/client/profile", (HttpContext context) =>
                context.Request.Headers.Authorization == "Bearer test-device-token" ? Results.Json(new { package }) : Results.Unauthorized());
            server.Map("/api/client/ws", async (HttpContext context) =>
            {
                Assert.Equal("true", context.Request.Query["authenticated"]);
                Assert.Equal("test-device-token", context.Request.Query["token"]);
                Assert.Equal("127.0.0.1", context.Request.Query["targetHost"]);
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "tunnel.ready", validation = "none", publicUrl = "https://test.tunnels.betterportal.dev" }).AsMemory(), WebSocketMessageType.Text, true, ct);
                await sockets.Writer.WriteAsync((socket, done), ct);
                await done.Task.WaitAsync(ct);
            });
            await server.StartAsync(ct);
            using var tunnel = new BetterTunnelsClient(new Uri(server.Urls.Single()));
            var opened = false;
            var account = await tunnel.SignInAsync(uri => { Assert.Equal("https", uri.Scheme); opened = true; }, ct);
            Assert.True(opened);
            Assert.Equal("test-device-token", account.Token);
            Assert.Equal("junior", await tunnel.GetPackageAsync(account.Token, ct));
            var statuses = Channel.CreateUnbounded<(string Status, string Url)>();
            await tunnel.RunAsync(account.Token, new(endpoint.Address), (status, url) => statuses.Writer.TryWrite((status, url)), ct);
            Assert.False(sockets.Reader.TryRead(out _));
            Assert.Contains("Senior", (await statuses.Reader.ReadAsync(ct)).Status);
            Assert.Contains("require", (await statuses.Reader.ReadAsync(ct)).Status);
            package = "senior";
            var running = tunnel.RunAsync(account.Token, new(endpoint.Address), (status, url) => statuses.Writer.TryWrite((status, url)), ct);
            var connection = await sockets.Reader.ReadAsync(ct);
            try
            {
                (string Status, string Url) status;
                do { status = await statuses.Reader.ReadAsync(ct); } while (status.Url.Length == 0);
                Assert.Equal("https://test.tunnels.betterportal.dev" + saved.EndpointPath, status.Url);
                async Task<(int Status, string Body, string? Error)> Request(string? accessKey, string? origin = null, string? path = null)
                {
                    var headers = new Dictionary<string, string> { ["host"] = "test.tunnels.betterportal.dev", ["accept"] = "application/json, text/event-stream", ["content-type"] = "application/json" };
                    if (accessKey is not null) headers["authorization"] = "Bearer " + accessKey;
                    if (origin is not null) headers["origin"] = origin;
                    var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}"""));
                    await connection.Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "request.start", requestId = Guid.NewGuid().ToString(), method = "POST", path = path ?? saved.EndpointPath, headers, body }).AsMemory(), WebSocketMessageType.Text, true, ct);
                    var status = 0;
                    var output = new StringBuilder();
                    while (true)
                    {
                        using var stream = new MemoryStream();
                        var bytes = new byte[4096];
                        ValueWebSocketReceiveResult received;
                        do { received = await connection.Socket.ReceiveAsync(bytes.AsMemory(), ct); stream.Write(bytes, 0, received.Count); } while (!received.EndOfMessage);
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
                Assert.Equal(401, (await Request(null)).Status);
                Assert.Equal(401, (await Request("wrong")).Status);
                Assert.Equal(403, (await Request(key, "https://attacker.example")).Status);
                var initialized = await Request(key);
                Assert.Equal(200, initialized.Status);
                Assert.Contains("protocolVersion", initialized.Body);
                Assert.NotNull((await Request(key, path: "/mcp")).Error);
                Assert.NotNull((await Request(key, path: "//attacker.example/")).Error);
                var oldKey = key;
                key = await store.RotateMcpAccessKeyAsync(ct);
                Assert.Equal(401, (await Request(oldKey)).Status);
                Assert.Equal(200, (await Request(key)).Status);
                await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test reconnect", ct);
            }
            finally { connection.Done.TrySetResult(); }
            connection = await sockets.Reader.ReadAsync(ct);
            package = "junior";
            await connection.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "recheck entitlement", ct);
            connection.Done.TrySetResult();
            await running.WaitAsync(ct);
            (string Status, string Url) last = ("", "missing");
            while (statuses.Reader.TryRead(out var next)) last = next;
            Assert.Contains("require", last.Status);
            Assert.Empty(last.Url);
            Assert.False(sockets.Reader.TryRead(out _));
            package = "senior";
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var restarted = tunnel.RunAsync(account.Token, new(endpoint.Address), (_, _) => { }, stopped.Token);
            connection = await sockets.Reader.ReadAsync(ct);
            try
            {
                await stopped.CancelAsync();
                await restarted.WaitAsync(ct);
                var bytes = new byte[1];
                // Stop/app exit tears down the actual server connection, not just the displayed URL.
                await Assert.ThrowsAsync<WebSocketException>(async () => await connection.Socket.ReceiveAsync(bytes.AsMemory(), ct));
            }
            finally { connection.Done.TrySetResult(); }
            await server.StopAsync(ct);
        });
    }

    [Fact]
    public async Task SettingsRequireExplicitEnablementAndPersistPermissionsAndKey()
    {
        await WithStore(async store =>
        {
            var saved = await store.GetMcpConfigurationAsync();
            Assert.False(saved.Configuration.Enabled);
            Assert.False(saved.Configuration.AllowWrites);
            Assert.False(saved.Configuration.AllowSending);
            Assert.Empty(saved.Configuration.MailboxIds ?? []);
            Assert.Equal(64, saved.AccessKey.Length);
            await using var settings = new McpSettingsViewModel(store, () => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
            Assert.False(settings.IsAvailable);
            await settings.InitializeAsync();
            Assert.True(settings.IsAvailable);
            Assert.EndsWith(saved.EndpointPath, settings.EndpointUrl);
            Assert.Contains("Disabled", settings.Status);

            // An occupied port proves that toggling the staged setting alone never starts a listener.
            using var reserved = new TcpListener(IPAddress.Loopback, 0);
            reserved.Start();
            settings.Port = ((IPEndPoint)reserved.LocalEndpoint).Port;
            settings.Enabled = true;
            Assert.Contains("Disabled", settings.Status);
            Assert.False((await store.GetMcpConfigurationAsync()).Configuration.Enabled);
            await settings.StartTunnelCommand.ExecuteAsync();
            Assert.Contains("Enable MCP", settings.TunnelStatus);
            Assert.False((await store.GetBetterTunnelsConfigurationAsync()).Enabled);
            await settings.ApplyCommand.ExecuteAsync();
            Assert.Contains("MCP stopped", settings.Status);
            reserved.Stop();
            await settings.ApplyCommand.ExecuteAsync();
            Assert.Contains("Listening", settings.Status);
            Assert.True((await store.GetMcpConfigurationAsync()).Configuration.Enabled);
            using var http = new HttpClient();
            using var unauthorized = await http.GetAsync(settings.EndpointUrl, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            await store.SaveBetterTunnelsConfigurationAsync(new(true, "test-device-token", "test@example.com"));
            await settings.StopTunnelCommand.ExecuteAsync();
            Assert.Equal(new BetterTunnelsConfiguration(false, "test-device-token", "test@example.com"), await store.GetBetterTunnelsConfigurationAsync());
            using var stillLocal = await http.GetAsync(settings.EndpointUrl, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, stillLocal.StatusCode);
            await settings.SignOutTunnelCommand.ExecuteAsync();
            Assert.Equal(new BetterTunnelsConfiguration(), await store.GetBetterTunnelsConfigurationAsync());

            await settings.RotateKeyCommand.ExecuteAsync();
            var rotated = await store.GetMcpConfigurationAsync();
            Assert.NotEqual(saved.AccessKey, rotated.AccessKey);
            Assert.Equal(settings.AccessKey, rotated.AccessKey);
            Assert.Equal(saved.EndpointPath, rotated.EndpointPath);
            Assert.EndsWith(saved.EndpointPath, settings.EndpointUrl);
            var mailbox = new Mailbox("account", "me@example.com", "Me");
            await store.SaveMailboxAsync(mailbox);
            await settings.RefreshMailboxesAsync();
            Assert.False(Assert.Single(settings.Mailboxes).IsSelected);
            await settings.RefreshMailboxesAsync([mailbox.Id]);
            Assert.True(Assert.Single(settings.Mailboxes).IsSelected);
            settings.AllowWrites = true;
            settings.AllowSending = true;
            settings.Enabled = false;
            await settings.ApplyCommand.ExecuteAsync();
            var disabled = (await store.GetMcpConfigurationAsync()).Configuration;
            Assert.False(disabled.Enabled);
            Assert.True(disabled.AllowWrites);
            Assert.True(disabled.AllowSending);
            Assert.Equal(mailbox.Id, Assert.Single(disabled.MailboxIds!));
            using var connection = new TcpClient();
            await Assert.ThrowsAsync<SocketException>(async () => await connection.ConnectAsync(IPAddress.Loopback, settings.Port, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task HttpEndpointAuthenticatesRealMcpClientAndRevokesAccess()
    {
        await WithStore(async store =>
        {
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            await store.SaveAccountAsync(account);
            await store.SaveMailboxAsync(mailbox);
            var configuration = new McpConfiguration(Enabled: true, MailboxIds: [mailbox.Id]);
            var saved = await store.GetMcpConfigurationAsync();
            var key = saved.AccessKey;
            var tools = new McpMailTools(store, () => configuration, () => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
            await using var endpoint = new McpEndpoint(tools, 0, saved.EndpointPath, () => configuration.Enabled, () => key);
            await endpoint.StartAsync(TestContext.Current.CancellationToken);
            using var http = new HttpClient();
            foreach (var wrongPath in new[] { "/mcp", "/bm/" + new string('0', 64), saved.EndpointPath + "extra" })
            {
                using var unknown = await http.GetAsync(new Uri(new Uri(endpoint.Address), wrongPath), TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
                Assert.Empty(unknown.Headers.WwwAuthenticate);
            }
            async Task<HttpStatusCode> Status(string? bearer = null, string? host = null, string? origin = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Address);
                if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                if (host is not null) request.Headers.Host = host;
                if (origin is not null) request.Headers.Add("Origin", origin);
                using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
                return response.StatusCode;
            }
            Assert.Equal(HttpStatusCode.Unauthorized, await Status());
            Assert.Equal(HttpStatusCode.Unauthorized, await Status("wrong"));
            Assert.Equal(HttpStatusCode.Forbidden, await Status(key, "attacker.example"));
            Assert.Equal(HttpStatusCode.Forbidden, await Status(key, origin: "https://attacker.example"));
            Assert.Equal(HttpStatusCode.Forbidden, await Status(key, origin: "null"));

            await using var transport = new HttpClientTransport(new()
            {
                Endpoint = new(endpoint.Address), TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
            var available = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(available, tool => tool.Name == "search_mail");
            Assert.Contains(available, tool => tool.Name == "send_draft");
            var result = await client.CallToolAsync("list_mailboxes", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.IsError == true);
            Assert.Contains(mailbox.Id, Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            var denied = await client.CallToolAsync("list_folders", new Dictionary<string, object?> { ["mailboxId"] = "forbidden" }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(denied.IsError);
            var nullMailbox = await client.CallToolAsync("search_mail", new Dictionary<string, object?> { ["mailboxId"] = null }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(nullMailbox.IsError);
            configuration = configuration with { AllowWrites = true };
            var created = await client.CallToolAsync("create_draft", new Dictionary<string, object?>
            {
                ["mailboxId"] = mailbox.Id, ["to"] = "Person <person@example.com>", ["subject"] = "MCP draft",
                ["body"] = "Body", ["importance"] = "High", ["isFlagged"] = true
            }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(created.IsError == true);
            Assert.Equal(MailImportance.High, Assert.Single(await store.GetLocalDraftSummariesAsync()).Importance);
            var oldKey = key;
            key = await store.RotateMcpAccessKeyAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, await Status(oldKey));
            Assert.NotEqual(HttpStatusCode.Unauthorized, await Status(key));
            key = "";
            Assert.Equal(HttpStatusCode.Unauthorized, await Status());
            configuration = configuration with { Enabled = false };
            Assert.Equal(HttpStatusCode.Forbidden, await Status(key));
        });
    }

    [Fact]
    public async Task ToolsScopeCachedMailAndQueueAuthorizedActionsWithoutSending()
    {
        await WithStore(async store =>
        {
            var account = new MailAccount("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);
            var mailbox = new Mailbox(account.AccountId, account.EmailAddress, "Me");
            var hidden = mailbox with { Address = "private@example.com" };
            await store.SaveAccountAsync(account);
            await store.SaveMailboxAsync(mailbox);
            await store.SaveMailboxAsync(hidden);
            var inbox = new MailFolder(mailbox.Id, "inbox", "Inbox", 0, 0, "inbox");
            var archive = inbox with { ProviderId = "archive", DisplayName = "Archive", WellKnownName = "archive" };
            await store.SaveFoldersAsync(mailbox.Id, [inbox, archive]);
            var message = new MailMessage(mailbox.Id, "message", "thread", "<test@example.com>", "inbox", "Unique needle",
                new("Sender", "sender@example.com"), [], DateTimeOffset.UtcNow.AddMinutes(-1), "Body", "Body", false, false, false, MailImportance.Normal, [], null);
            await store.ApplySyncPageAsync("seed", new([message, message with { MailboxId = hidden.Id, ProviderId = "hidden", ReceivedAt = DateTimeOffset.UtcNow }], null, false));
            var configuration = new McpConfiguration();
            var sends = 0;
            var refreshes = 0;
            var tools = new McpMailTools(store, () => configuration, () => { refreshes++; return Task.CompletedTask; }, async (sender, id, draft) =>
            {
                sends++;
                await store.SaveLocalDraftAsync(new(id, sender.Account.AccountId, sender.Mailbox.Id, string.Join("; ", draft.To), "", "", draft.Subject,
                    draft.Body, draft.Attachments ?? [], DateTimeOffset.UtcNow, IsHtml: true, IsQueued: true, Importance: draft.Importance, IsFlagged: draft.IsFlagged));
            });
            await Assert.ThrowsAsync<McpException>(() => tools.ListMailboxes());
            configuration = configuration with { Enabled = true };
            Assert.Empty(await tools.ListMailboxes());
            configuration = configuration with { MailboxIds = [mailbox.Id] };
            Assert.Equal(mailbox.Id, Assert.Single(await tools.ListMailboxes()).Id);
            Assert.Equal("message", Assert.Single(await tools.SearchMail(mailbox.Id, "needle", 1)).ProviderId);
            await Assert.ThrowsAsync<McpException>(() => tools.SearchMail(null!));
            await Assert.ThrowsAsync<McpException>(() => tools.ListFolders(null!));
            await Assert.ThrowsAsync<McpException>(() => tools.ReadMail(hidden.Id, "hidden"));
            Assert.DoesNotContain(hidden.Id, JsonSerializer.Serialize(await tools.ReadThread(mailbox.Id, "message")));
            await Assert.ThrowsAsync<McpException>(() => tools.CreateDraft(mailbox.Id, "to@example.com", "Subject", "Body"));
            await Assert.ThrowsAsync<McpException>(() => tools.MoveMail(mailbox.Id, "message", "archive"));
            await Assert.ThrowsAsync<McpException>(() => tools.SyncMail(mailbox.Id));
            configuration = configuration with { AllowWrites = true };
            await tools.MoveMail(mailbox.Id, "message", "archive");
            Assert.Equal("archive", (await store.GetMessageAsync(mailbox.Id, "message"))!.FolderId);
            Assert.Equal(MailActionKind.Move, Assert.Single(await tools.ListBusy(mailbox.Id)).Kind);
            await tools.CreateDraft(mailbox.Id, "\"Doe, Jane\" <jane@example.com>", "Subject", "<script>bad()</script><p>Hello</p>", isHtml: true, importance: MailImportance.High, isFlagged: true);
            var local = Assert.Single(await store.GetLocalDraftSummariesAsync());
            Assert.Equal("Doe, Jane", Assert.Single(MailAddressList.Parse(local.To)).Name);
            Assert.DoesNotContain("<script", (await store.GetLocalDraftAsync(local.Id))!.Body);
            await Assert.ThrowsAsync<McpException>(() => tools.SendDraft(mailbox.Id, local.Id));
            await Assert.ThrowsAsync<McpException>(() => tools.DeleteDraft(hidden.Id, local.Id));
            configuration = configuration with { AllowSending = true };
            await tools.SendDraft(mailbox.Id, local.Id);
            await tools.SendDraft(mailbox.Id, local.Id);
            Assert.Equal(1, sends);
            Assert.True((await store.GetLocalDraftAsync(local.Id))!.IsQueued);
            Assert.Equal(MailImportance.High, (await store.GetLocalDraftAsync(local.Id))!.Importance);
            await store.MarkOutboxSendAcceptedAsync(local.Id);
            await store.DeleteLocalDraftAsync(local.Id);
            await tools.SendDraft(mailbox.Id, local.Id);
            Assert.Equal(1, sends);
            await tools.CreateDraft(mailbox.Id, "to@example.com", "Delete me", "Body");
            var deletion = Assert.Single(await store.GetLocalDraftSummariesAsync());
            await tools.DeleteDraft(mailbox.Id, deletion.Id);
            await tools.DeleteDraft(mailbox.Id, deletion.Id);
            Assert.Empty(await store.GetLocalDraftSummariesAsync());
            Assert.NotNull(await store.GetMailActionAsync("delete:" + deletion.Id));
            Assert.True(refreshes >= 4);
        });
    }

    private static async Task WithStore(Func<EncryptedMailStore, Task> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-mcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await test(store);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
