using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterMail.Core;

namespace BetterMail.Google;

public sealed class GoogleAuthService(
    GoogleOptions options,
    IProviderTokenStore tokenStore,
    HttpClient? httpClient = null) : IAccountProvider
{
    public const string Id = "google-workspace";
    internal const string GmailScope = "https://www.googleapis.com/auth/gmail.modify";
    internal static readonly string[] Scopes = ["openid", "email", "profile", GmailScope];
    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshLocks = new(StringComparer.Ordinal);

    public string ProviderId => Id;
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Mail | ProviderCapabilities.ServerSearch;

    public Task<MailAccount> SignInAsync(CancellationToken cancellationToken = default) =>
        AuthorizeAsync(null, cancellationToken);

    public async Task<MailAccount> ReauthenticateAsync(
        string accountId,
        CancellationToken cancellationToken = default) =>
        await AuthorizeAsync(accountId, cancellationToken).ConfigureAwait(false);

    public async Task SignOutAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var token = await tokenStore.GetProviderTokenAsync(Id, accountId, cancellationToken).ConfigureAwait(false);
        try
        {
            if (token is not null)
            {
                using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["token"] = token.RefreshToken
                });
                using var response = await _http.PostAsync(
                    "https://oauth2.googleapis.com/revoke", content, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException)
        {
            // Local removal must still work when Google is unreachable.
        }
        finally
        {
            await tokenStore.DeleteProviderTokenAsync(Id, accountId, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<string> GetAccessTokenAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var token = await tokenStore.GetProviderTokenAsync(Id, accountId, cancellationToken).ConfigureAwait(false)
            ?? throw ReauthenticationRequired();
        if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
        {
            return token.AccessToken;
        }

        var refreshLock = _refreshLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            token = await tokenStore.GetProviderTokenAsync(Id, accountId, cancellationToken).ConfigureAwait(false)
                ?? throw ReauthenticationRequired();
            if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                return token.AccessToken;
            }

            var fields = RefreshTokenFields(options, token.RefreshToken);
            using var response = await _http.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(fields),
                cancellationToken).ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var refreshed = token with
            {
                AccessToken = RequiredString(root, "access_token"),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
                Scopes = root.TryGetProperty("scope", out var scope) ? scope.GetString() ?? token.Scopes : token.Scopes
            };
            await tokenStore.SaveProviderTokenAsync(refreshed, cancellationToken).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw ReauthenticationRequired(exception);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    internal static Uri AuthorizationUri(
        string clientId,
        string redirectUri,
        string state,
        string codeChallenge,
        string? loginHint = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', Scopes),
            ["access_type"] = "offline",
            ["prompt"] = "consent select_account",
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256"
        };
        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            parameters["login_hint"] = loginHint;
        }
        return new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + Form(parameters));
    }

    internal static string CodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private async Task<MailAccount> AuthorizeAsync(
        string? expectedAccountId,
        CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var authorizationUri = AuthorizationUri(options.ClientId, redirectUri, state, CodeChallenge(verifier), expectedAccountId);
        if (Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true }) is null)
        {
            throw new InvalidOperationException("The system browser could not be opened for Google sign-in.");
        }

        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var callback = await ReadCallbackAsync(client, cancellationToken).ConfigureAwait(false);
        try
        {
            if (callback.State != state)
            {
                throw new InvalidOperationException("Google sign-in returned an invalid state value.");
            }
            if (callback.Error is not null)
            {
                throw new InvalidOperationException($"Google sign-in failed: {callback.Error}");
            }
            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                throw new InvalidOperationException("Google sign-in did not return an authorization code.");
            }

            var fields = AuthorizationCodeTokenFields(options, callback.Code, verifier, redirectUri);
            using var tokenResponse = await _http.PostAsync(
                "https://oauth2.googleapis.com/token",
                new FormUrlEncodedContent(fields),
                cancellationToken).ConfigureAwait(false);
            using var tokenDocument = await ReadJsonAsync(tokenResponse, cancellationToken).ConfigureAwait(false);
            var tokenRoot = tokenDocument.RootElement;
            var accessToken = RequiredString(tokenRoot, "access_token");
            var refreshToken = RequiredString(tokenRoot, "refresh_token");
            var scopes = RequiredString(tokenRoot, "scope");
            EnsureScopes(scopes);

            using var profileRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
            profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var profileResponse = await _http.SendAsync(profileRequest, cancellationToken).ConfigureAwait(false);
            using var profileDocument = await ReadJsonAsync(profileResponse, cancellationToken).ConfigureAwait(false);
            var profile = profileDocument.RootElement;
            var accountId = RequiredString(profile, "sub");
            if (expectedAccountId is not null && accountId != expectedAccountId)
            {
                throw new InvalidOperationException("Choose the same Google account when re-authenticating.");
            }
            var email = RequiredString(profile, "email");

            using var gmailRequest = new HttpRequestMessage(
                HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
            gmailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var gmailResponse = await _http.SendAsync(gmailRequest, cancellationToken).ConfigureAwait(false);
            using var gmailDocument = await ReadJsonAsync(gmailResponse, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(RequiredString(gmailDocument.RootElement, "emailAddress"), email, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Google returned a different Gmail account than the signed-in profile.");
            }

            var account = new MailAccount(
                Id,
                accountId,
                profile.TryGetProperty("hd", out var domain) ? domain.GetString() ?? "" : "",
                email,
                profile.TryGetProperty("name", out var name) ? name.GetString() ?? email : email,
                Capabilities);
            await tokenStore.SaveProviderTokenAsync(new ProviderToken(
                Id,
                accountId,
                accessToken,
                refreshToken,
                DateTimeOffset.UtcNow.AddSeconds(tokenRoot.GetProperty("expires_in").GetInt32()),
                scopes), cancellationToken).ConfigureAwait(false);
            await TryWriteBrowserResponseAsync(client, success: true, cancellationToken).ConfigureAwait(false);
            return account;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await TryWriteBrowserResponseAsync(client, success: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<OAuthCallback> ReadCallbackAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Google sign-in returned an empty callback.");
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
        }
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Google sign-in returned an invalid callback.");
        }
        var query = ParseQuery(uri.Query);
        return new(
            query.GetValueOrDefault("code"),
            query.GetValueOrDefault("state"),
            query.GetValueOrDefault("error"));
    }

    private static async Task WriteBrowserResponseAsync(
        TcpClient client,
        bool success,
        CancellationToken cancellationToken)
    {
        var body = BrowserResponseHtml(success);
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await client.GetStream().WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await client.GetStream().WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteBrowserResponseAsync(
        TcpClient client,
        bool success,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteBrowserResponseAsync(client, success, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // The app connection is complete even if the browser closed before rendering the result page.
        }
    }

    internal static string BrowserResponseHtml(bool success) =>
        OAuthBrowserPage.Html("Google Workspace", success);

    internal static Dictionary<string, string> AuthorizationCodeTokenFields(
        GoogleOptions options,
        string code,
        string verifier,
        string redirectUri) => new()
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri
        };

    internal static Dictionary<string, string> RefreshTokenFields(
        GoogleOptions options,
        string refreshToken) => new()
        {
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };

    private static Dictionary<string, string> ParseQuery(string query) => query
        .TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(static pair => pair.Split('=', 2))
        .ToDictionary(
            static pair => Uri.UnescapeDataString(pair[0].Replace('+', ' ')),
            static pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "",
            StringComparer.Ordinal);

    private static string Form(IReadOnlyDictionary<string, string> values) =>
        string.Join('&', values.Select(static pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void EnsureScopes(string scopes)
    {
        if (!scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(GmailScope, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Google did not grant Gmail access.");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google returned {(int)response.StatusCode}: {GoogleError(json)}", null, response.StatusCode);
        }
        return JsonDocument.Parse(json);
    }

    private static string GoogleError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("error_description", out var description))
            {
                return description.GetString() ?? "Unknown error";
            }
            if (root.TryGetProperty("error", out var error))
            {
                return error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? "Unknown error"
                    : error.TryGetProperty("message", out var message) ? message.GetString() ?? "Unknown error" : error.ToString();
            }
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(json) ? "Unknown error" : json;
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidOperationException($"Google did not return '{property}'.");

    private static InvalidOperationException ReauthenticationRequired(Exception? inner = null) =>
        new("Google permissions need to be refreshed. Open Settings > Accounts and choose Re-authenticate for this account.", inner);

    private sealed record OAuthCallback(string? Code, string? State, string? Error);
}
