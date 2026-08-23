using BetterMail.Core;
using BetterMail.Microsoft365;

namespace BetterMail.Tests;

public sealed class Microsoft365OptionsTests
{
    [Fact]
    public void UsesEnvironmentClientIdOverride()
    {
        const string variable = "BETTERMAIL_MICROSOFT_CLIENT_ID";
        var original = Environment.GetEnvironmentVariable(variable);

        try
        {
            Environment.SetEnvironmentVariable(variable, " developer-client-id ");
            Assert.Equal("developer-client-id", Microsoft365Options.Create("data").ClientId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public void RequestsAllModulePermissionsDuringAccountAuthentication()
    {
        Assert.Equal(
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
        ], Microsoft365AuthService.Scopes);

        Assert.Equal(
        ["Mail.ReadWrite", "Mail.Send", "Mail.ReadWrite.Shared", "Mail.Send.Shared"],
        Microsoft365AuthService.MailScopes);
    }

    [Fact]
    public void MissingConsentHasOneActionableAccountLevelRecovery()
    {
        var exception = Microsoft365AuthService.ReauthenticationRequired();

        Assert.Contains("Settings > Accounts", exception.Message);
        Assert.Contains("Re-authenticate", exception.Message);
    }

    [Fact]
    public void AccountAuthenticationRejectsPartialPermissionGrants()
    {
        var partialGrant = Microsoft365AuthService.Scopes.Where(scope => scope != "Notes.ReadWrite");

        var exception = Assert.Throws<InvalidOperationException>(
            () => Microsoft365AuthService.EnsureAllScopesGranted(partialGrant));

        Assert.Contains("Notes.ReadWrite", exception.Message);
    }

    [Fact]
    public async Task UsesBrandedBrowserCompletionPages()
    {
        await using var browserPage = OAuthBrowserRedirectServer.Start("Microsoft 365");
        var options = Microsoft365AuthService.CreateSystemWebViewOptions(browserPage);
        using var client = new HttpClient();
        using var response = await client.GetAsync(
            options.BrowserRedirectSuccess, TestContext.Current.CancellationToken);
        var success = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains("Microsoft 365 is now connected", success);
        Assert.Contains("<link rel=\"icon\" type=\"image/png\" href=\"data:image/png;base64,", success);
        Assert.Equal(browserPage.ErrorUri, options.BrowserRedirectError);
    }
}
