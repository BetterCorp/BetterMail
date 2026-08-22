using System.Text;
using System.Text.Json;
using BetterMail.Core;
using BetterMail.Google;

namespace BetterMail.Tests;

public sealed class GoogleProviderTests
{
    [Fact]
    public void UsesEnvironmentCredentialOverrides()
    {
        const string clientIdVariable = "BETTERMAIL_GOOGLE_CLIENT_ID";
        const string clientSecretVariable = "BETTERMAIL_GOOGLE_CLIENT_SECRET";
        var originalClientId = Environment.GetEnvironmentVariable(clientIdVariable);
        var originalClientSecret = Environment.GetEnvironmentVariable(clientSecretVariable);
        try
        {
            Environment.SetEnvironmentVariable(clientIdVariable, " developer-client-id ");
            Environment.SetEnvironmentVariable(clientSecretVariable, " developer-client-secret ");

            var options = GoogleOptions.Create();

            Assert.Equal("developer-client-id", options.ClientId);
            Assert.Equal("developer-client-secret", options.ClientSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable(clientIdVariable, originalClientId);
            Environment.SetEnvironmentVariable(clientSecretVariable, originalClientSecret);
        }
    }

    [Fact]
    public void BuildsPkceAuthorizationRequestForDesktopLoopback()
    {
        const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleAuthService.CodeChallenge(verifier));

        var uri = GoogleAuthService.AuthorizationUri(
            "client.apps.googleusercontent.com",
            "http://127.0.0.1:4567/",
            "state-value",
            "challenge",
            "google-subject");
        Assert.Equal("accounts.google.com", uri.Host);
        Assert.Contains("code_challenge_method=S256", uri.Query);
        Assert.Contains(Uri.EscapeDataString(GoogleAuthService.GmailScope), uri.Query);
        Assert.Contains("login_hint=google-subject", uri.Query);
        Assert.DoesNotContain("client_secret", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SendsDesktopCredentialOnlyToTokenEndpoint()
    {
        var options = new GoogleOptions("client-id", "client-secret");
        var authorization = GoogleAuthService.AuthorizationCodeTokenFields(
            options, "code", "verifier", "http://127.0.0.1:4567/");
        var refresh = GoogleAuthService.RefreshTokenFields(options, "refresh-token");

        Assert.Equal("client-secret", authorization["client_secret"]);
        Assert.Equal("client-secret", refresh["client_secret"]);
        Assert.Equal("verifier", authorization["code_verifier"]);
        Assert.Equal("refresh-token", refresh["refresh_token"]);
    }

    [Fact]
    public void RendersBrandedBrowserCompletionPages()
    {
        var success = GoogleAuthService.BrowserResponseHtml(true);
        var failure = GoogleAuthService.BrowserResponseHtml(false);

        Assert.Contains("You're connected", success);
        Assert.Contains("BetterMail", success);
        Assert.Contains("prefers-color-scheme", success);
        Assert.Contains("<link rel=\"icon\" type=\"image/png\" href=\"data:image/png;base64,", success);
        Assert.Contains("<img class=\"logo\" src=\"data:image/png;base64,", success);
        Assert.DoesNotContain(".logo::after", success);
        Assert.Contains("Connection unsuccessful", failure);
    }

    [Fact]
    public void MapsGmailPayloadAndPrimaryFolder()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>Hello</p>"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var document = JsonDocument.Parse($$$"""
            {
              "id":"message-1","threadId":"thread-1","historyId":"42","internalDate":"1723636800000",
              "labelIds":["INBOX","UNREAD","STARRED","Label_1"],"snippet":"Hello",
              "payload":{
                "mimeType":"multipart/mixed",
                "headers":[
                  {"name":"Subject","value":"Status"},
                  {"name":"From","value":"Sender <sender@example.com>"},
                  {"name":"To","value":"Person <person@example.com>"},
                  {"name":"Message-ID","value":"<internet-id>"}
                ],
                "parts":[
                  {"mimeType":"text/html","filename":"","body":{"data":"{{{body}}}","size":12}},
                  {"partId":"2","mimeType":"application/pdf","filename":"report.pdf",
                   "headers":[{"name":"Content-Disposition","value":"attachment"}],
                   "body":{"attachmentId":"attachment-1","size":123}}
                ]
              }
            }
            """);
        var mailbox = new Mailbox("google-account", "person@example.com", "Person");

        var message = GoogleGmailProvider.MapMessage(
            mailbox, document.RootElement, new Dictionary<string, string> { ["Label_1"] = "Projects" });

        Assert.Equal("INBOX", message.FolderId);
        Assert.Equal("<p>Hello</p>", message.Body);
        Assert.True(message.IsHtml);
        Assert.False(message.IsRead);
        Assert.True(message.IsFlagged);
        Assert.True(message.HasAttachments);
        Assert.Equal(["Projects"], message.Categories);
        Assert.Equal("sender@example.com", message.From.Address);
    }

    [Fact]
    public void BuildsMimeWithRecipientsBodyAndAttachment()
    {
        var mime = GoogleGmailProvider.BuildMime(
            new Mailbox("account", "person@example.com", "Person"),
            new DraftMessage(
                "Quarterly café",
                [new MailAddress("Recipient", "recipient@example.com")],
                "<p>Attached</p>",
                true,
                Attachments: [new DraftAttachment("report.txt", "text/plain", Encoding.UTF8.GetBytes("content"))]));

        Assert.Contains("From: Person <person@example.com>\r\n", mime);
        Assert.Contains("To: Recipient <recipient@example.com>\r\n", mime);
        Assert.Contains("Subject: =?UTF-8?B?", mime);
        Assert.Contains("Content-Type: multipart/mixed", mime);
        Assert.Contains("filename=\"report.txt\"", mime);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("content")), mime);
    }

    [Fact]
    public async Task RoutesMailCallsByAccountProvider()
    {
        var microsoft = new RecordingMailProvider("microsoft");
        var google = new RecordingMailProvider("google");
        var router = new MailProviderRouter([
            ("microsoft365", microsoft),
            (GoogleAuthService.Id, google)
        ]);
        var account = new MailAccount(
            GoogleAuthService.Id, "google-account", "", "person@example.com", "Person", ProviderCapabilities.Mail);
        var mailbox = new Mailbox(account.AccountId, account.EmailAddress, account.DisplayName);

        await router.GetMessageAsync(account, mailbox, "message", TestContext.Current.CancellationToken);

        Assert.Null(microsoft.LastMessageId);
        Assert.Equal("message", google.LastMessageId);
        Assert.True(router.SupportsCloudDraftsFor(account));
    }

    private sealed class RecordingMailProvider(string name) : IMailProvider
    {
        public bool SupportsCloudDrafts => name == "google";
        public string? LastMessageId { get; private set; }
        public Task<IReadOnlyList<MailFolder>> GetFoldersAsync(MailAccount account, Mailbox mailbox, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailFolder>>([]);
        public Task<MailSyncPage> SyncFolderAsync(MailAccount account, Mailbox mailbox, string folderId, string? cursor, CancellationToken cancellationToken = default) => Task.FromResult(new MailSyncPage([], null, false));
        public Task MarkReadAsync(MailAccount account, Mailbox mailbox, string messageId, bool isRead, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MailMessage> GetMessageAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default)
        {
            LastMessageId = messageId;
            return Task.FromResult(new MailMessage(
                mailbox.Id, messageId, null, null, "INBOX", "", new MailAddress("", ""), [],
                DateTimeOffset.UtcNow, "", null, false, true, false, MailImportance.Normal, [], null));
        }
        public Task MoveMessageAsync(MailAccount account, Mailbox mailbox, string messageId, string destinationFolderId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetFlaggedAsync(MailAccount account, Mailbox mailbox, string messageId, bool isFlagged, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(MailAccount account, Mailbox mailbox, string messageId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MailAttachment>>([]);
        public Task SendAsync(MailAccount account, Mailbox mailbox, DraftMessage draft, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
