using BetterMail.App;
using BetterMail.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;

namespace BetterMail.Tests;

// The working directory and environment are process-wide; keep this regression isolated.
[CollectionDefinition("MCP host configuration", DisableParallelization = true)]
public sealed class McpHostConfigurationCollection;

[Collection("MCP host configuration")]
public sealed class McpHostConfigurationTests
{
    [Fact]
    public async Task HostDoesNotLoadConfigurationOrEnableFileWatchingFromItsWorkingDirectory()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-mcp-no-watch-" + Guid.NewGuid());
        var workingDirectory = Environment.CurrentDirectory;
        const string variable = "DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE";
        var reloadSetting = Environment.GetEnvironmentVariable(variable);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "projects", "node_modules", "nested"));
        // A default builder tries to parse this before callers can clear its config sources.
        await File.WriteAllTextAsync(Path.Combine(directory, "appsettings.json"), "invalid JSON", token);
        try
        {
            Environment.CurrentDirectory = directory;
            Environment.SetEnvironmentVariable(variable, "true");
            var builder = McpEndpoint.CreateHostBuilder();
            Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), Path.GetFullPath(builder.Environment.ContentRootPath));
            Assert.NotEqual(directory, builder.Environment.ContentRootPath);
            Assert.IsType<NullFileProvider>(builder.Environment.ContentRootFileProvider);
            Assert.IsType<NullFileProvider>(builder.Environment.WebRootFileProvider);
            Assert.DoesNotContain(builder.Configuration.Sources, source => source is FileConfigurationSource);
            Assert.DoesNotContain(((IConfigurationRoot)builder.Configuration).Providers, provider => provider is FileConfigurationProvider);
            foreach (var provider in new[] { builder.Environment.ContentRootFileProvider, builder.Environment.WebRootFileProvider })
            {
                var change = provider.Watch("**/*");
                Assert.False(change.ActiveChangeCallbacks);
                Assert.False(change.HasChanged);
            }
            await using var app = builder.Build();
            // MCP host creation does not change the application's global reload preference.
            Assert.Equal("true", Environment.GetEnvironmentVariable(variable));
        }
        finally
        {
            Environment.CurrentDirectory = workingDirectory;
            Environment.SetEnvironmentVariable(variable, reloadSetting);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task EndpointStartsWithNoConfigurationFilesAndKeepsBearerAuthentication()
    {
        var token = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-mcp-empty-host-" + Guid.NewGuid());
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), new string('D', 64));
            await store.InitializeAsync(token);
            var settings = await store.GetMcpConfigurationAsync(token);
            var tools = new McpMailTools(store, () => new(Enabled: true), () => Task.CompletedTask, (_, _, _) => Task.CompletedTask);
            await using var endpoint = new McpEndpoint(tools, 0, settings.EndpointPath, () => true, () => settings.AccessKey);
            await endpoint.StartAsync(token);
            using var client = new HttpClient();
            using var response = await client.GetAsync(endpoint.Address, token);
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
