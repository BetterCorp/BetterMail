using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BetterMail.Microsoft365;

namespace BetterMail.Tests;

public sealed class Microsoft365MailProviderTests
{
    [Fact]
    public void HydratesSparseDeltaMessages()
    {
        using var sparse = JsonDocument.Parse("""{"id":"one","isRead":true}""");
        using var complete = JsonDocument.Parse(
            """{"id":"one","subject":"Hello","from":{},"toRecipients":[],"ccRecipients":[],"receivedDateTime":"2026-07-14T12:00:00Z","parentFolderId":"inbox"}""");
        using var missingCc = JsonDocument.Parse(
            """{"id":"one","subject":"Hello","from":{},"toRecipients":[],"receivedDateTime":"2026-07-14T12:00:00Z","parentFolderId":"inbox"}""");
        using var removed = JsonDocument.Parse("""{"id":"one","@removed":{"reason":"deleted"}}""");

        Assert.True(Microsoft365MailProvider.NeedsHydration(sparse.RootElement));
        Assert.False(Microsoft365MailProvider.NeedsHydration(complete.RootElement));
        Assert.True(Microsoft365MailProvider.NeedsHydration(missingCc.RootElement));
        Assert.False(Microsoft365MailProvider.NeedsHydration(removed.RootElement));
    }

    [Fact]
    public void UsesAnUncappedPagedListingForBoundedHistoryBackfill()
    {
        var account = Account();
        var mailbox = new BetterMail.Core.Mailbox(account.AccountId, account.EmailAddress, "Primary");

        var endpoint = Microsoft365MailProvider.HistoryEndpoint(
            account, mailbox, "inbox", new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero));

        Assert.Contains("/messages?", endpoint);
        Assert.DoesNotContain("/messages/delta", endpoint);
        Assert.Contains("$filter=receivedDateTime+ge+2026-04-16T00:00:00Z", endpoint);
        Assert.Contains("$orderby=receivedDateTime+desc", endpoint);
        Assert.Contains("$top=50", endpoint);
    }

    [Fact]
    public void SwitchesCompletedHistoryBackfillToDeltaTracking()
    {
        using var finalHistoryPage = JsonDocument.Parse("""{"value":[]}""");
        const string deltaEndpoint = "me/mailFolders/inbox/messages/delta?$filter=receivedDateTime+ge+cutoff";

        var continuation = Microsoft365MailProvider.SyncContinuation(
            finalHistoryPage.RootElement,
            "last-history-page",
            deltaEndpoint);

        Assert.True(continuation.HasMore);
        Assert.Equal(deltaEndpoint, continuation.NextCursor);
    }

    [Fact]
    public void DeltaSyncUsesPreferPagingInsteadOfTruncatingTheInitialRoundWithTop()
    {
        var account = Account();
        var mailbox = new BetterMail.Core.Mailbox(account.AccountId, account.EmailAddress, "Primary");

        var endpoint = Microsoft365MailProvider.SyncEndpoint(account, mailbox, "inbox", null);

        Assert.DoesNotContain("$top", endpoint);
    }

    [Fact]
    public void SearchEndpointEscapesTheQueryAndTargetsTheWholeMailbox()
    {
        var account = Account();
        var mailbox = new BetterMail.Core.Mailbox(account.AccountId, account.EmailAddress, "Primary");

        var endpoint = Microsoft365MailProvider.SearchEndpoint(account, mailbox, "quarterly \"report\"", 250);

        Assert.StartsWith("me/messages?$search=", endpoint);
        Assert.Contains("%22quarterly%20%5C%22report%5C%22%22", endpoint);
        Assert.EndsWith("$top=250", endpoint);
    }

    [Fact]
    public void KeepsOldMessageBodiesForCompleteLocalSearch()
    {
        using var document = JsonDocument.Parse(
            """
            {"id":"old","subject":"Archive","parentFolderId":"archive","receivedDateTime":"2016-01-01T00:00:00Z","from":{},"toRecipients":[],"ccRecipients":[],"bodyPreview":"preview","body":{"contentType":"html","content":"<p>historic narwhal</p>"}}
            """);
        var mailbox = new BetterMail.Core.Mailbox("account", "person@example.com", "Person");

        var message = Microsoft365MailProvider.MapMessage(mailbox, document.RootElement);

        Assert.Equal("<p>historic narwhal</p>", message.Body);
    }

    [Fact]
    public void ListsAttachmentDerivedPropertiesWithoutInvalidBaseSelect()
    {
        var account = Account();
        var primary = new BetterMail.Core.Mailbox(account.AccountId, account.EmailAddress, "Primary");
        var shared = new BetterMail.Core.Mailbox(account.AccountId, "shared+ops@example.com", "Shared", IsShared: true);

        Assert.Equal("me/messages/message%2Fid/attachments?$top=100",
            Microsoft365MailProvider.AttachmentEndpoint(account, primary, "message/id"));
        Assert.Equal("users/shared%2Bops%40example.com/messages/message%2Fid/attachments?$top=100",
            Microsoft365MailProvider.AttachmentEndpoint(account, shared, "message/id"));
        Assert.DoesNotContain("$select", Microsoft365MailProvider.AttachmentEndpoint(account, primary, "message/id"));
    }

    [Fact]
    public void MapsInlineFileAttachmentContentForCidRendering()
    {
        using var document = JsonDocument.Parse(
            """{"id":"attachment-id","name":"logo.png","contentType":"image/png","size":3,"isInline":true,"contentId":"<logo@cid>","contentBytes":"AQID"}""");

        var attachment = Microsoft365MailProvider.MapAttachment(document.RootElement);

        Assert.True(attachment.IsInline);
        Assert.Equal("<logo@cid>", attachment.ContentId);
        Assert.Equal([1, 2, 3], attachment.ContentBytes);
    }

    [Fact]
    public void MapsCcRecipientsWithoutRequestingOrPersistingBcc()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "id":"message",
              "subject":"Planning",
              "parentFolderId":"inbox",
              "receivedDateTime":"2026-07-14T12:00:00Z",
              "from":{"emailAddress":{"name":"Sender","address":"sender@example.com"}},
              "toRecipients":[{"emailAddress":{"name":"To","address":"to@example.com"}}],
              "ccRecipients":[{"emailAddress":{"name":"Cc","address":"cc@example.com"}}],
              "bccRecipients":[{"emailAddress":{"name":"Secret","address":"secret@example.com"}}]
            }
            """);
        var mailbox = new BetterMail.Core.Mailbox("account", "person@example.com", "Person");

        var message = Microsoft365MailProvider.MapMessage(mailbox, document.RootElement);

        Assert.Contains("ccRecipients", Microsoft365MailProvider.MessageSelect);
        Assert.DoesNotContain("bccRecipients", Microsoft365MailProvider.MessageSelect);
        Assert.Equal("to@example.com", Assert.Single(message.To).Address);
        Assert.Equal("cc@example.com", Assert.Single(message.Cc ?? []).Address);
        Assert.DoesNotContain(
            message.To.Concat(message.Cc ?? []),
            recipient => recipient.Address == "secret@example.com");
    }

    [Fact]
    public void RejectsSharedMailboxBelongingToAnotherAccount()
    {
        var account = Account();
        var mailbox = new BetterMail.Core.Mailbox("another-account", "shared@example.com", "Shared", IsShared: true);

        Assert.Throws<InvalidOperationException>(() => Microsoft365MailProvider.MailboxPath(account, mailbox));
    }

    [Fact]
    public async Task UploadsLargeAttachmentsInChunksWithoutAuthorizationAndRetriesTransientFailures()
    {
        var content = new byte[Microsoft365MailProvider.UploadChunkSizeBytes + 17];
        Random.Shared.NextBytes(content);
        var handler = new UploadHandler();
        using var client = new HttpClient(handler);

        await Microsoft365MailProvider.UploadLargeAttachmentAsync(
            client,
            new Uri("https://outlook.office.com/upload?token=opaque"),
            content,
            TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Range, handler.Requests[1].Range);
        Assert.Equal(
            $"bytes 0-{Microsoft365MailProvider.UploadChunkSizeBytes - 1}/{content.Length}",
            handler.Requests[0].Range);
        Assert.Equal(
            $"bytes {Microsoft365MailProvider.UploadChunkSizeBytes}-{content.Length - 1}/{content.Length}",
            handler.Requests[2].Range);
        Assert.All(handler.Requests, request => Assert.False(request.HasAuthorization));
        Assert.Equal(Microsoft365MailProvider.UploadChunkSizeBytes, handler.Requests[1].Length);
        Assert.Equal(17, handler.Requests[2].Length);
    }

    [Theory]
    [InlineData("""{"nextExpectedRanges":["3932160-"]}""", 3932160)]
    [InlineData("""{"nextExpectedRanges":[]}""", 12)]
    [InlineData("{}", 12)]
    public void ReadsUploadResumeOffset(string json, long expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(expected, Microsoft365MailProvider.NextExpectedOffset(document.RootElement, 12));
    }

    [Theory]
    [InlineData(3 * 1024 * 1024 - 1, false)]
    [InlineData(3 * 1024 * 1024, true)]
    public void SelectsTheDocumentedAttachmentUploadPath(long size, bool expected) =>
        Assert.Equal(expected, Microsoft365MailProvider.RequiresUploadSession(size));

    [Fact]
    public void BuildsOwnedDraftEndpointsAndCompletePayloads()
    {
        var account = Account();
        var primary = new BetterMail.Core.Mailbox(account.AccountId, account.EmailAddress, "Primary");
        var shared = new BetterMail.Core.Mailbox(
            account.AccountId, "shared+ops@example.com", "Shared", IsShared: true, CanSendAs: true);
        var draft = new BetterMail.Core.DraftMessage(
            "Planning",
            [new("To", "to@example.com")],
            "<p>Hello</p>",
            true,
            [new("Cc", "cc@example.com")],
            [new("Bcc", "bcc@example.com")]);

        Assert.Equal(
            $"me/mailFolders/drafts/messages?$select={Microsoft365MailProvider.DraftSelect}&$top=50",
            Microsoft365MailProvider.DraftsEndpoint(account, primary));
        Assert.Equal(
            "users/shared%2Bops%40example.com/messages/draft%2Fid",
            Microsoft365MailProvider.DraftEndpoint(account, shared, "draft/id"));
        using var payload = JsonDocument.Parse(
            JsonSerializer.Serialize(Microsoft365MailProvider.BuildMessagePayload(shared, draft)));
        Assert.Equal("HTML", payload.RootElement.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("to@example.com", payload.RootElement.GetProperty("toRecipients")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("cc@example.com", payload.RootElement.GetProperty("ccRecipients")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("bcc@example.com", payload.RootElement.GetProperty("bccRecipients")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal("shared+ops@example.com", payload.RootElement.GetProperty("from")
            .GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public void MapsCloudDraftBodyRecipientsAttachmentsAndVersion()
    {
        var account = Account();
        var mailbox = new BetterMail.Core.Mailbox(account.AccountId, account.EmailAddress, "Primary");
        using var document = JsonDocument.Parse(
            """
            {
              "id":"draft-id",
              "@odata.etag":"version-2",
              "subject":"Planning",
              "lastModifiedDateTime":"2026-07-14T12:30:00Z",
              "body":{"contentType":"html","content":"<p>Hello</p>"},
              "toRecipients":[{"emailAddress":{"name":"To","address":"to@example.com"}}],
              "ccRecipients":[{"emailAddress":{"name":"Cc","address":"cc@example.com"}}],
              "bccRecipients":[{"emailAddress":{"name":"Bcc","address":"bcc@example.com"}}]
            }
            """);
        var attachment = new BetterMail.Core.DraftAttachment("plan.txt", "text/plain", "plan"u8.ToArray());

        var draft = Microsoft365MailProvider.MapDraft(
            account, mailbox, document.RootElement, [attachment]);

        Assert.Equal("draft-id", draft.ProviderId);
        Assert.Equal(account.AccountId, draft.AccountId);
        Assert.Equal(mailbox.Id, draft.MailboxId);
        Assert.True(draft.Message.IsHtml);
        Assert.Equal("to@example.com", Assert.Single(draft.Message.To).Address);
        Assert.Equal("cc@example.com", Assert.Single(draft.Message.Cc!).Address);
        Assert.Equal("bcc@example.com", Assert.Single(draft.Message.Bcc!).Address);
        Assert.Equal("plan.txt", Assert.Single(draft.Message.Attachments!).Name);
        Assert.Equal("version-2", draft.ETag);
        Assert.Equal(DateTimeOffset.Parse("2026-07-14T12:30:00Z"), draft.UpdatedAt);
    }

    private static BetterMail.Core.MailAccount Account() => new(
        Microsoft365AuthService.Id,
        "account-id",
        "tenant-id",
        "user@example.com",
        "User",
        BetterMail.Core.ProviderCapabilities.Mail);

    private sealed class UploadHandler : HttpMessageHandler
    {
        public List<(string Range, int Length, bool HasAuthorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((
                request.Content.Headers.ContentRange!.ToString(),
                body.Length,
                request.Headers.Authorization is not null));

            if (Requests.Count == 1)
            {
                var transient = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                transient.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return transient;
            }
            if (Requests.Count == 2)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(new
                        {
                            nextExpectedRanges = new[] { $"{Microsoft365MailProvider.UploadChunkSizeBytes}-" }
                        }),
                        Encoding.UTF8,
                        "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
