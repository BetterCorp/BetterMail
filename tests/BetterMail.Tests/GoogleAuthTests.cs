using System.Net;
using System.Text;
using BetterMail.Core;
using BetterMail.Google;

namespace BetterMail.Tests;

public sealed class GoogleAuthTests
{
    private static ProviderToken Token(bool expired = false) => new(GoogleAuthService.Id, "account", "old-access", "old-refresh",
        DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1), GoogleAuthService.GmailScope);
    private static GoogleAuthService Auth(TokenStore store, HttpClient client) => new(new("client", "secret"), store, client);
    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CachedAccessTokenDoesNotContactGoogle()
    {
        var store = new TokenStore(Token());
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected network request.")));
        Assert.Equal("old-access", await Auth(store, http).GetAccessTokenAsync("account", TestContext.Current.CancellationToken));
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RefreshPersistsRotatedTokenOrPreservesExistingTokenWhenOmitted(bool rotates)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new TokenStore(Token(expired: true));
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal("oauth2.googleapis.com", request.RequestUri!.Host);
            Assert.Contains("refresh_token=old-refresh", await request.Content!.ReadAsStringAsync(token));
            return Reply(HttpStatusCode.OK, rotates
                ? """{"access_token":"new-access","expires_in":3600,"refresh_token":"new-refresh"}"""
                : """{"access_token":"new-access","expires_in":3600}""");
        }));
        Assert.Equal("new-access", await Auth(store, http).GetAccessTokenAsync("account", ct));
        Assert.Equal(rotates ? "new-refresh" : "old-refresh", store.Value!.RefreshToken);
        Assert.Equal(GoogleAuthService.GmailScope, store.Value.Scopes);
        Assert.True(store.Value.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "invalid_client")]
    [InlineData(HttpStatusCode.Unauthorized, "invalid_client")]
    [InlineData(HttpStatusCode.BadRequest, "invalid_request")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "temporarily_unavailable")]
    public async Task NonGrantFailuresDoNotAskForAnotherSignInOrDiscardCredentials(HttpStatusCode status, string error)
    {
        var ct = TestContext.Current.CancellationToken;
        var original = Token(expired: true);
        var store = new TokenStore(original);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(status, "{\"error\":\"" + error + "\"}"))));
        var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(() => Auth(store, http).GetAccessTokenAsync("account", ct));
        Assert.Equal(status, failure.StatusCode);
        Assert.DoesNotContain("Re-authenticate", failure.Message);
        Assert.Equal(original, store.Value);
    }

    [Fact]
    public async Task RejectedRefreshGrantExplainsThatReauthenticationIsRequired()
    {
        var store = new TokenStore(Token(expired: true));
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""))));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Auth(store, http).GetAccessTokenAsync("account", TestContext.Current.CancellationToken));
        Assert.Contains("Re-authenticate", failure.Message);
        Assert.NotNull(store.Value);
    }

    [Fact]
    public async Task GmailRebuildsRejectedRequestOnceWithASilentlyRefreshedToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new TokenStore(Token());
        var refreshes = 0;
        using var tokenHttp = new HttpClient(new Handler((_, _) =>
        {
            refreshes++;
            return Task.FromResult(Reply(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}"""));
        }));
        var requests = new List<(string? Token, string Body)>();
        using var apiHttp = new HttpClient(new Handler(async (request, token) =>
        {
            requests.Add((request.Headers.Authorization?.Parameter, await request.Content!.ReadAsStringAsync(token)));
            return requests.Count == 1 ? Reply(HttpStatusCode.Unauthorized, "{}") : Reply(HttpStatusCode.OK, "{}");
        }));
        var account = new MailAccount(GoogleAuthService.Id, "account", "", "me@example.com", "Me", ProviderCapabilities.Mail);
        var provider = new GoogleGmailProvider(Auth(store, tokenHttp), apiHttp);
        await provider.SendDraftAsync(account, new("account", "me@example.com", "Me"), "draft", ct);
        Assert.Equal(1, refreshes);
        Assert.Equal(2, requests.Count);
        Assert.Equal("old-access", requests[0].Token);
        Assert.Equal("new-access", requests[1].Token);
        Assert.Equal(requests[0].Body, requests[1].Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 2, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1, 0)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1, 0)]
    public async Task RecoveryIsBoundedAndDoesNotReplayPermissionOrServiceFailures(HttpStatusCode status, int expectedRequests, int expectedRefreshes)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new TokenStore(Token());
        var refreshes = 0;
        using var tokenHttp = new HttpClient(new Handler((_, _) =>
        {
            refreshes++;
            return Task.FromResult(Reply(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}"""));
        }));
        var requests = 0;
        using var apiHttp = new HttpClient(new Handler((_, _) => { requests++; return Task.FromResult(Reply(status, "{}")); }));
        var account = new MailAccount(GoogleAuthService.Id, "account", "", "me@example.com", "Me", ProviderCapabilities.Mail);
        var provider = new GoogleGmailProvider(Auth(store, tokenHttp), apiHttp);
        var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(() => provider.DeleteDraftAsync(account, new("account", "me@example.com", "Me"), "draft", ct));
        Assert.Equal(status, failure.StatusCode);
        Assert.Equal(expectedRequests, requests);
        Assert.Equal(expectedRefreshes, refreshes);
    }

    [Fact]
    public async Task ConcurrentGmailRejectionsShareOneRefresh()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        var store = new TokenStore(Token());
        var refreshes = 0;
        using var tokenHttp = new HttpClient(new Handler((_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            return Task.FromResult(Reply(HttpStatusCode.OK, """{"access_token":"new-access","expires_in":3600}"""));
        }));
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleRequests = 0;
        using var apiHttp = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Headers.Authorization?.Parameter == "old-access")
            {
                if (Interlocked.Increment(ref staleRequests) == 2) arrived.TrySetResult();
                await arrived.Task.WaitAsync(token);
                return Reply(HttpStatusCode.Unauthorized, "{}");
            }
            Assert.Equal("new-access", request.Headers.Authorization?.Parameter);
            return Reply(HttpStatusCode.OK, "{}");
        }));
        var provider = new GoogleGmailProvider(Auth(store, tokenHttp), apiHttp);
        var account = new MailAccount(GoogleAuthService.Id, "account", "", "me@example.com", "Me", ProviderCapabilities.Mail);
        var mailbox = new Mailbox("account", "me@example.com", "Me");
        await Task.WhenAll(provider.DeleteDraftAsync(account, mailbox, "one", ct), provider.DeleteDraftAsync(account, mailbox, "two", ct));
        Assert.Equal(1, refreshes);
        Assert.Equal(1, store.Saves);
    }

    private sealed class TokenStore(ProviderToken? value) : IProviderTokenStore
    {
        public ProviderToken? Value = value;
        public int Saves;
        public Task<ProviderToken?> GetProviderTokenAsync(string providerId, string accountId, CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveProviderTokenAsync(ProviderToken token, CancellationToken cancellationToken = default)
        { Value = token; Interlocked.Increment(ref Saves); return Task.CompletedTask; }
        public Task DeleteProviderTokenAsync(string providerId, string accountId, CancellationToken cancellationToken = default)
        { Value = null; return Task.CompletedTask; }
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
