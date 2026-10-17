using System.Net;
using System.Text;
using System.Text.Json;
using BetterMail.Core;
using BetterMail.Microsoft365;

namespace BetterMail.Tests;

public sealed class Microsoft365WebhookTests
{
    private static readonly MailAccount Account = new("microsoft365", "account", "tenant", "me@example.com", "Me", ProviderCapabilities.Mail);

    [Fact]
    public async Task CreatesBasicMailboxSubscriptionWithLifecycleAndExistingUserToken()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/subscriptions", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-access-token", request.Headers.Authorization?.Parameter);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var root = document.RootElement;
            Assert.Equal("me/messages", root.GetProperty("resource").GetString());
            Assert.Equal("created,updated,deleted", root.GetProperty("changeType").GetString());
            Assert.Equal("https://test.example/hooks", root.GetProperty("notificationUrl").GetString());
            Assert.Equal(root.GetProperty("notificationUrl").GetString(), root.GetProperty("lifecycleNotificationUrl").GetString());
            Assert.Equal("secret-state", root.GetProperty("clientState").GetString());
            Assert.False(root.GetProperty("includeResourceData").GetBoolean());
            Assert.InRange(root.GetProperty("expirationDateTime").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddDays(5), DateTimeOffset.UtcNow.AddDays(7));
            return Reply(HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://graph.microsoft.com/v1.0/") };
        var provider = new Microsoft365MailProvider((_, _) => Task.FromResult("test-access-token"), http);
        var subscription = await provider.CreateMailWebhookAsync(Account, "https://test.example/hooks", "secret-state", ct);
        Assert.Equal("subscription-id", subscription.Id);
    }

    [Fact]
    public async Task RenewsByIdAndTreatsAnAlreadyDeletedSubscriptionAsRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var count = 0;
        using var handler = new Handler((request, _) =>
        {
            count++;
            Assert.Equal("/v1.0/subscriptions/subscription-id", request.RequestUri!.AbsolutePath);
            if (count == 1) { Assert.Equal(HttpMethod.Patch, request.Method); return Task.FromResult(Reply(HttpStatusCode.OK)); }
            Assert.Equal(HttpMethod.Delete, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://graph.microsoft.com/v1.0/") };
        var provider = new Microsoft365MailProvider((_, _) => Task.FromResult("test-access-token"), http);
        Assert.Equal("subscription-id", (await provider.RenewMailWebhookAsync(Account, "subscription-id", ct)).Id);
        await provider.DeleteMailWebhookAsync(Account, "subscription-id", ct);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task LostCreationResponseRecoversOnlyTheMatchingInstallationSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var posts = 0;
        var lists = 0;
        var deleted = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            switch (request.Method.Method)
            {
                case "POST":
                    posts++;
                    if (posts == 1) throw new HttpRequestException("Graph created the subscription but its response was lost.");
                    return Task.FromResult(posts == 2 ? new HttpResponseMessage(HttpStatusCode.Conflict) : Reply(HttpStatusCode.Created));
                case "GET":
                    lists++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(lists == 1
                            ? """{"value":[{"id":"other-installation","resource":"me/messages","changeType":"created,updated,deleted","notificationUrl":"https://other.example/hooks"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/subscriptions?$skiptoken=page2"}"""
                            : """{"value":[{"id":"orphan","resource":"me/messages","changeType":"deleted,updated,created","notificationUrl":"https://test.example/hooks","clientState":null}]}""")
                    });
                case "DELETE":
                    deleted.Add(request.RequestUri!.AbsolutePath);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                default: throw new InvalidOperationException("Unexpected request.");
            }
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://graph.microsoft.com/v1.0/") };
        var provider = new Microsoft365MailProvider((_, _) => Task.FromResult("test-access-token"), http);
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CreateMailWebhookAsync(Account, "https://test.example/hooks", "persisted-state", ct));
        var recovered = await provider.CreateMailWebhookAsync(Account, "https://test.example/hooks", "persisted-state", ct);
        Assert.Equal("subscription-id", recovered.Id);
        Assert.Equal(3, posts);
        Assert.Equal(2, lists);
        Assert.Equal("/v1.0/subscriptions/orphan", Assert.Single(deleted));
    }

    [Theory]
    [InlineData("me/events", "created,updated,deleted", "https://test.example/hooks")]
    [InlineData("me/messages", "created", "https://test.example/hooks")]
    [InlineData("me/messages", "created,updated,deleted", "https://other.example/hooks")]
    public async Task CreationConflictDoesNotRemoveUnrelatedSubscriptions(string resource, string changeType, string url)
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { value = new[] { new { id = "unrelated", resource, changeType, notificationUrl = url } } }))
            });
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://graph.microsoft.com/v1.0/") };
        var provider = new Microsoft365MailProvider((_, _) => Task.FromResult("test-access-token"), http);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CreateMailWebhookAsync(Account, "https://test.example/hooks", "persisted-state", ct));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { id = "subscription-id", expirationDateTime = DateTimeOffset.UtcNow.AddDays(6) }), Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
