using BetterMail.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace BetterMail.Microsoft365;

public sealed class Microsoft365AuthService : IAccountProvider
{
    public const string Id = "microsoft365";

    internal static readonly string[] Scopes =
    [
        "User.Read",
        "Mail.ReadWrite",
        "Mail.Send",
        "Mail.ReadWrite.Shared",
        "Mail.Send.Shared",
        "Calendars.ReadWrite",
        "Contacts.ReadWrite",
        "Tasks.ReadWrite",
        "Files.ReadWrite",
        "Notes.ReadWrite"
    ];

    internal static readonly string[] MailScopes =
    [
        "Mail.ReadWrite",
        "Mail.Send",
        "Mail.ReadWrite.Shared",
        "Mail.Send.Shared"
    ];

    private readonly IPublicClientApplication _client;
    private readonly MsalCacheHelper _cache;

    private Microsoft365AuthService(IPublicClientApplication client, MsalCacheHelper cache)
    {
        _client = client;
        _cache = cache;
    }

    public string ProviderId => Id;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Mail |
        ProviderCapabilities.SharedMailboxes |
        ProviderCapabilities.SendAs |
        ProviderCapabilities.SendOnBehalf |
        ProviderCapabilities.Calendar |
        ProviderCapabilities.Contacts |
        ProviderCapabilities.Tasks |
        ProviderCapabilities.Files |
        ProviderCapabilities.Notes;

    public static async Task<Microsoft365AuthService> CreateAsync(
        Microsoft365Options options,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var client = PublicClientApplicationBuilder
            .Create(options.ClientId)
            .WithAuthority(AadAuthorityAudience.AzureAdMultipleOrgs)
            .WithRedirectUri("http://localhost")
            .Build();

        var storage = new StorageCreationPropertiesBuilder("msal.cache", options.DataDirectory)
            .WithLinuxKeyring(
                "com.bettermail.tokens",
                MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "BetterMail token cache",
                new KeyValuePair<string, string>("application", "BetterMail"),
                new KeyValuePair<string, string>("version", "1"))
            .WithMacKeyChain("com.bettermail.tokens", "BetterMail")
            .Build();

        var cache = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
        cache.VerifyPersistence();
        cache.RegisterCache(client.UserTokenCache);
        cancellationToken.ThrowIfCancellationRequested();
        return new Microsoft365AuthService(client, cache);
    }

    public async Task<MailAccount> SignInAsync(CancellationToken cancellationToken = default)
    {
        var result = await _client.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureAllScopesGranted(result.Scopes);

        return ToMailAccount(result);
    }

    public async Task SignOutAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var account = (await _client.GetAccountsAsync().ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.HomeAccountId.Identifier == accountId);
        if (account is not null)
        {
            await _client.RemoveAsync(account).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task<MailAccount> ReauthenticateAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var account = (await _client.GetAccountsAsync().ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.HomeAccountId.Identifier == accountId);
        var request = _client.AcquireTokenInteractive(Scopes)
            .WithPrompt(Prompt.SelectAccount)
            .WithUseEmbeddedWebView(false);
        if (account is not null)
        {
            request = request.WithAccount(account);
        }

        var result = await request.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        EnsureAllScopesGranted(result.Scopes);
        if (result.Account.HomeAccountId.Identifier != accountId)
        {
            throw new InvalidOperationException("Choose the same Microsoft account when re-authenticating.");
        }

        return ToMailAccount(result);
    }

    internal async Task<string> GetAccessTokenAsync(string accountId, CancellationToken cancellationToken)
        => await GetAccessTokenAsync(accountId, MailScopes, cancellationToken).ConfigureAwait(false);

    internal async Task<string> GetAccessTokenAsync(
        string accountId,
        IEnumerable<string> scopes,
        CancellationToken cancellationToken)
    {
        var account = (await _client.GetAccountsAsync().ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.HomeAccountId.Identifier == accountId)
            ?? throw ReauthenticationRequired();

        try
        {
            var result = await _client.AcquireTokenSilent(scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException exception)
        {
            throw ReauthenticationRequired(exception);
        }
    }

    internal static InvalidOperationException ReauthenticationRequired(Exception? innerException = null) =>
        new("Microsoft permissions need to be refreshed. Open Settings > Accounts and choose Re-authenticate for this account.", innerException);

    internal static void EnsureAllScopesGranted(IEnumerable<string> grantedScopes)
    {
        var granted = grantedScopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = Scopes.Where(scope => !granted.Contains(scope)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Microsoft did not grant all required permissions ({string.Join(", ", missing)}). A tenant administrator may need to approve BetterMail.");
        }
    }

    private MailAccount ToMailAccount(AuthenticationResult result) => new(
        ProviderId,
        result.Account.HomeAccountId.Identifier,
        result.TenantId,
        result.Account.Username,
        result.ClaimsPrincipal?.Identity?.Name ?? result.Account.Username,
        Capabilities);
}
