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
    public void UsesBrandedBrowserCompletionPages()
    {
        var options = Microsoft365AuthService.CreateSystemWebViewOptions();
        var error = string.Format(options.HtmlMessageError, "error", "details");

        Assert.Contains("Microsoft 365 is now connected", options.HtmlMessageSuccess);
        Assert.Contains("Microsoft 365 could not finish connecting", error);
        Assert.Contains("<link rel=\"icon\" type=\"image/png\" href=\"data:image/png;base64,", options.HtmlMessageSuccess);
        Assert.Contains("<img class=\"logo\" src=\"data:image/png;base64,", error);
    }
}
