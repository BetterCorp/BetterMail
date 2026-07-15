using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BetterMail.Core;

namespace BetterMail.Microsoft365;

public sealed class Microsoft365MailProvider(
    Microsoft365AuthService authentication,
    HttpClient? httpClient = null) : IMailProvider, ISharedMailboxProvider
{
    public bool SupportsCloudDrafts => true;

    internal const long SimpleAttachmentLimitBytes = 3L * 1024 * 1024;
    internal const int UploadChunkSizeBytes = 12 * 320 * 1024;
    internal const string MessageSelect =
        "id,conversationId,internetMessageId,parentFolderId,subject,from,toRecipients,ccRecipients,receivedDateTime,bodyPreview,body,isRead,hasAttachments,importance,categories,flag";
    internal const string DraftSelect =
        "id,subject,toRecipients,ccRecipients,bccRecipients,body,lastModifiedDateTime,hasAttachments";

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient
    {
        BaseAddress = new Uri("https://graph.microsoft.com/v1.0/")
    };

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
        MailAccount account,
        Mailbox mailbox,
        CancellationToken cancellationToken = default)
    {
        var folders = new List<MailFolder>();
        var endpoints = new Queue<(string Endpoint, string? ParentId)>();
        endpoints.Enqueue(($"{MailboxPath(account, mailbox)}/mailFolders?$select=id,displayName,unreadItemCount,totalItemCount,childFolderCount&$top=100", null));
        while (endpoints.Count > 0)
        {
            var (endpoint, parentId) = endpoints.Dequeue();
            using var document = await GetJsonAsync(account, endpoint, cancellationToken).ConfigureAwait(false);
            foreach (var folder in document.RootElement.GetProperty("value").EnumerateArray())
            {
                var folderId = RequiredString(folder, "id");
                folders.Add(new MailFolder(
                    mailbox.Id,
                    folderId,
                    RequiredString(folder, "displayName"),
                    folder.GetProperty("unreadItemCount").GetInt32(),
                    folder.GetProperty("totalItemCount").GetInt32(),
                    ParentProviderId: parentId));
                if (folder.TryGetProperty("childFolderCount", out var childCount) && childCount.GetInt32() > 0)
                {
                    endpoints.Enqueue(($"{MailboxPath(account, mailbox)}/mailFolders/{Uri.EscapeDataString(folderId)}/childFolders?$select=id,displayName,unreadItemCount,totalItemCount,childFolderCount&$top=100", folderId));
                }
            }

            if (document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink) && nextLink.GetString() is { } nextEndpoint)
            {
                endpoints.Enqueue((nextEndpoint, parentId));
            }
        }

        using var inboxDocument = await GetJsonAsync(
            account,
            $"{MailboxPath(account, mailbox)}/mailFolders/inbox?$select=id",
            cancellationToken).ConfigureAwait(false);
        var inboxId = RequiredString(inboxDocument.RootElement, "id");
        return folders.Select(folder => folder.ProviderId == inboxId ? folder with { WellKnownName = "inbox" } : folder).ToArray();
    }

    public Task<MailSyncPage> SyncFolderAsync(
        MailAccount account,
        Mailbox mailbox,
        string folderId,
        string? cursor,
        CancellationToken cancellationToken = default) =>
        SyncFolderAsync(account, mailbox, folderId, cursor, null, cancellationToken);

    public async Task<MailSyncPage> SyncFolderAsync(
        MailAccount account,
        Mailbox mailbox,
        string folderId,
        string? cursor,
        DateTimeOffset? receivedSince,
        CancellationToken cancellationToken = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(cursor)
            ? SyncEndpoint(account, mailbox, folderId, receivedSince)
            : cursor;

        using var document = await GetJsonAsync(account, endpoint, cancellationToken).ConfigureAwait(false);
        var messages = new List<MailMessage>();
        foreach (var message in document.RootElement.GetProperty("value").EnumerateArray())
        {
            if (NeedsHydration(message))
            {
                var messageId = RequiredString(message, "id");
                using var hydrated = await GetJsonAsync(
                    account,
                    $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(messageId)}?$select={MessageSelect}",
                    cancellationToken).ConfigureAwait(false);
                messages.Add(MapMessage(mailbox, hydrated.RootElement));
            }
            else
            {
                messages.Add(MapMessage(mailbox, message));
            }
        }

        var root = document.RootElement;
        var hasMore = root.TryGetProperty("@odata.nextLink", out var nextLink);
        var nextCursor = hasMore
            ? nextLink.GetString()
            : root.TryGetProperty("@odata.deltaLink", out var deltaLink) ? deltaLink.GetString() : cursor;

        return new MailSyncPage(messages, nextCursor, hasMore);
    }

    internal static string SyncEndpoint(
        MailAccount account,
        Mailbox mailbox,
        string folderId,
        DateTimeOffset? receivedSince)
    {
        var endpoint = $"{MailboxPath(account, mailbox)}/mailFolders/{Uri.EscapeDataString(folderId)}/messages/delta" +
            $"?$select={MessageSelect}&$top=50";
        if (receivedSince is null)
        {
            return endpoint;
        }
        var cutoff = receivedSince.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        return $"{endpoint}&$filter=receivedDateTime+ge+{cutoff}&$orderby=receivedDateTime+desc";
    }

    internal static bool NeedsHydration(JsonElement message) =>
        !message.TryGetProperty("@removed", out _) &&
        (!message.TryGetProperty("subject", out _) ||
         !message.TryGetProperty("from", out _) ||
         !message.TryGetProperty("toRecipients", out _) ||
         !message.TryGetProperty("ccRecipients", out _) ||
         !message.TryGetProperty("receivedDateTime", out _) ||
         !message.TryGetProperty("parentFolderId", out _));

    public Task MarkReadAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        bool isRead,
        CancellationToken cancellationToken = default) => SendJsonAsync(
            account,
            HttpMethod.Patch,
            $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(messageId)}",
            new { isRead },
            cancellationToken);

    public async Task<MailMessage> GetMessageAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            account,
            $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(messageId)}?$select={MessageSelect}",
            cancellationToken).ConfigureAwait(false);
        return MapMessage(mailbox, document.RootElement);
    }

    public async Task<IReadOnlyList<MailHeader>> GetMessageHeadersAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            account,
            $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(messageId)}?$select=internetMessageHeaders",
            cancellationToken).ConfigureAwait(false);
        return document.RootElement.GetProperty("internetMessageHeaders")
            .EnumerateArray()
            .Select(header => new MailHeader(
                RequiredString(header, "name"),
                RequiredString(header, "value")))
            .ToArray();
    }

    public Task MoveMessageAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        string destinationFolderId,
        CancellationToken cancellationToken = default) => SendJsonAsync(
            account,
            HttpMethod.Post,
            $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(messageId)}/move",
            new { destinationId = destinationFolderId },
            cancellationToken);

    public Task SetFlaggedAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        bool isFlagged,
        CancellationToken cancellationToken = default) => SendJsonAsync(
            account,
            HttpMethod.Patch,
            $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(messageId)}",
            new { flag = new { flagStatus = isFlagged ? "flagged" : "notFlagged" } },
            cancellationToken);

    public async Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var attachments = new List<MailAttachment>();
        string? endpoint = AttachmentEndpoint(account, mailbox, messageId);
        while (!string.IsNullOrWhiteSpace(endpoint))
        {
            using var document = await GetJsonAsync(account, endpoint, cancellationToken).ConfigureAwait(false);
            attachments.AddRange(document.RootElement.GetProperty("value").EnumerateArray().Select(MapAttachment));
            endpoint = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }

        return attachments;
    }

    public async Task SendAsync(
        MailAccount account,
        Mailbox mailbox,
        DraftMessage draft,
        CancellationToken cancellationToken = default)
    {
        ValidateCanSend(mailbox);
        var cloudDraft = await CreateDraftAsync(account, mailbox, draft, cancellationToken).ConfigureAwait(false);
        await SendDraftAsync(account, mailbox, cloudDraft.ProviderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(
        MailAccount account,
        Mailbox mailbox,
        CancellationToken cancellationToken = default)
    {
        var drafts = new List<CloudDraft>();
        string? endpoint = DraftsEndpoint(account, mailbox);
        while (!string.IsNullOrWhiteSpace(endpoint))
        {
            using var document = await GetJsonAsync(account, endpoint, cancellationToken).ConfigureAwait(false);
            foreach (var message in document.RootElement.GetProperty("value").EnumerateArray())
            {
                var attachments = message.TryGetProperty("hasAttachments", out var hasAttachments) && hasAttachments.GetBoolean()
                    ? await GetDraftAttachmentsAsync(account, mailbox, RequiredString(message, "id"), cancellationToken).ConfigureAwait(false)
                    : DraftAttachmentSet.Empty;
                drafts.Add(MapDraft(account, mailbox, message, attachments));
            }
            endpoint = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }
        return drafts;
    }

    public async Task<CloudDraft> GetDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        using var document = await GetJsonAsync(
            account,
            $"{DraftEndpoint(account, mailbox, draftId)}?$select={DraftSelect}",
            cancellationToken).ConfigureAwait(false);
        var attachments = document.RootElement.TryGetProperty("hasAttachments", out var hasAttachments) && hasAttachments.GetBoolean()
            ? await GetDraftAttachmentsAsync(account, mailbox, draftId, cancellationToken).ConfigureAwait(false)
            : DraftAttachmentSet.Empty;
        return MapDraft(account, mailbox, document.RootElement, attachments);
    }

    public async Task<CloudDraft> CreateDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        DraftMessage draft,
        CancellationToken cancellationToken = default)
    {
        ValidateDraft(draft);
        using var document = await SendJsonForResponseAsync(
            account,
            HttpMethod.Post,
            $"{MailboxPath(account, mailbox)}/messages",
            BuildMessagePayload(mailbox, draft),
            cancellationToken).ConfigureAwait(false);
        var draftId = RequiredString(document.RootElement, "id");
        await AddAttachmentsAsync(account, mailbox, draftId, draft.Attachments ?? [], cancellationToken).ConfigureAwait(false);
        return await GetDraftAsync(account, mailbox, draftId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudDraft> UpdateDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        DraftMessage draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ValidateDraft(draft);
        DraftAttachmentSet? existing = null;
        if (draft.Attachments is not null)
        {
            existing = await GetDraftAttachmentsAsync(account, mailbox, draftId, cancellationToken).ConfigureAwait(false);
            if (existing.HasUnsupported)
            {
                throw new NotSupportedException(
                    "This draft contains an Outlook item or reference attachment that BetterMail cannot safely replace.");
            }
        }
        await SendJsonAsync(
            account,
            HttpMethod.Patch,
            DraftEndpoint(account, mailbox, draftId),
            BuildMessagePayload(mailbox, draft),
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            foreach (var attachmentId in existing.ProviderIds)
            {
                await SendJsonAsync(
                    account,
                    HttpMethod.Delete,
                    $"{DraftEndpoint(account, mailbox, draftId)}/attachments/{Uri.EscapeDataString(attachmentId)}",
                    body: null,
                    cancellationToken).ConfigureAwait(false);
            }
            await AddAttachmentsAsync(account, mailbox, draftId, draft.Attachments!, cancellationToken).ConfigureAwait(false);
        }
        return await GetDraftAsync(account, mailbox, draftId, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        return SendJsonAsync(
            account,
            HttpMethod.Delete,
            DraftEndpoint(account, mailbox, draftId),
            body: null,
            cancellationToken);
    }

    public Task SendDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draftId);
        ValidateCanSend(mailbox);
        return SendJsonAsync(
            account,
            HttpMethod.Post,
            $"{DraftEndpoint(account, mailbox, draftId)}/send",
            body: null,
            cancellationToken);
    }

    internal static string DraftsEndpoint(MailAccount account, Mailbox mailbox) =>
        $"{MailboxPath(account, mailbox)}/mailFolders/drafts/messages?$select={DraftSelect}&$top=50";

    internal static string DraftEndpoint(MailAccount account, Mailbox mailbox, string draftId) =>
        $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(draftId)}";

    internal static object BuildMessagePayload(Mailbox mailbox, DraftMessage draft) => new
    {
        subject = draft.Subject,
        body = new { contentType = draft.IsHtml ? "HTML" : "Text", content = draft.Body },
        toRecipients = ToGraphRecipients(draft.To),
        ccRecipients = ToGraphRecipients(draft.Cc ?? []),
        bccRecipients = ToGraphRecipients(draft.Bcc ?? []),
        from = mailbox.IsShared
            ? new { emailAddress = new { name = mailbox.DisplayName, address = mailbox.Address } }
            : null
    };

    private static object[] ToGraphRecipients(IReadOnlyList<MailAddress> recipients) =>
        recipients.Select(static recipient => (object)new
        {
            emailAddress = new { name = recipient.Name, address = recipient.Address }
        }).ToArray();

    private async Task AddAttachmentsAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        IReadOnlyList<DraftAttachment> attachments,
        CancellationToken cancellationToken)
    {
        foreach (var attachment in attachments)
        {
            if (!RequiresUploadSession(attachment.Size))
            {
                await SendJsonAsync(
                    account,
                    HttpMethod.Post,
                    $"{DraftEndpoint(account, mailbox, draftId)}/attachments",
                    new Dictionary<string, object?>
                    {
                        ["@odata.type"] = "#microsoft.graph.fileAttachment",
                        ["name"] = attachment.Name,
                        ["contentType"] = attachment.ContentType,
                        ["contentBytes"] = Convert.ToBase64String(attachment.ContentBytes)
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var session = await SendJsonForResponseAsync(
                account,
                HttpMethod.Post,
                $"{DraftEndpoint(account, mailbox, draftId)}/attachments/createUploadSession",
                new
                {
                    AttachmentItem = new
                    {
                        attachmentType = "file",
                        name = attachment.Name,
                        contentType = attachment.ContentType,
                        size = attachment.Size
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var uploadUrl = new Uri(RequiredString(session.RootElement, "uploadUrl"), UriKind.Absolute);
            await Microsoft365RequestScheduler.Shared.RunAsync(
                account,
                DraftEndpoint(account, mailbox, draftId),
                token => UploadLargeAttachmentAsync(_httpClient, uploadUrl, attachment.ContentBytes, token),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DraftAttachmentSet> GetDraftAttachmentsAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken)
    {
        var supported = new List<DraftAttachment>();
        var providerIds = new List<string>();
        var hasUnsupported = false;
        string? endpoint = $"{DraftEndpoint(account, mailbox, draftId)}/attachments?$top=100";
        while (!string.IsNullOrWhiteSpace(endpoint))
        {
            using var document = await GetJsonAsync(account, endpoint, cancellationToken).ConfigureAwait(false);
            foreach (var attachment in document.RootElement.GetProperty("value").EnumerateArray())
            {
                providerIds.Add(RequiredString(attachment, "id"));
                var type = OptionalString(attachment, "@odata.type");
                if (!string.Equals(type, "#microsoft.graph.fileAttachment", StringComparison.OrdinalIgnoreCase) ||
                    !attachment.TryGetProperty("contentBytes", out var contentBytes) ||
                    contentBytes.ValueKind != JsonValueKind.String)
                {
                    hasUnsupported = true;
                    continue;
                }
                try
                {
                    supported.Add(new DraftAttachment(
                        OptionalString(attachment, "name") ?? "Attachment",
                        OptionalString(attachment, "contentType") ?? "application/octet-stream",
                        contentBytes.GetBytesFromBase64()));
                }
                catch (FormatException)
                {
                    hasUnsupported = true;
                }
            }
            endpoint = document.RootElement.TryGetProperty("@odata.nextLink", out var nextLink)
                ? nextLink.GetString()
                : null;
        }
        return new DraftAttachmentSet(supported, providerIds, hasUnsupported);
    }

    internal static CloudDraft MapDraft(
        MailAccount account,
        Mailbox mailbox,
        JsonElement message,
        IReadOnlyList<DraftAttachment> attachments,
        bool hasUnsupportedAttachments = false)
    {
        var body = message.TryGetProperty("body", out var value) ? value : default;
        var updatedAt = message.TryGetProperty("lastModifiedDateTime", out var updated) &&
                        DateTimeOffset.TryParse(updated.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
        return new CloudDraft(
            RequiredString(message, "id"),
            account.AccountId,
            mailbox.Id,
            new DraftMessage(
                OptionalString(message, "subject") ?? "",
                ReadAddresses(message, "toRecipients"),
                body.ValueKind == JsonValueKind.Object ? OptionalString(body, "content") ?? "" : "",
                body.ValueKind == JsonValueKind.Object &&
                string.Equals(OptionalString(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase),
                ReadAddresses(message, "ccRecipients"),
                ReadAddresses(message, "bccRecipients"),
                attachments),
            updatedAt,
            OptionalString(message, "@odata.etag"),
            hasUnsupportedAttachments);
    }

    private static CloudDraft MapDraft(
        MailAccount account,
        Mailbox mailbox,
        JsonElement message,
        DraftAttachmentSet attachments) =>
        MapDraft(account, mailbox, message, attachments.Supported, attachments.HasUnsupported);

    private static void ValidateDraft(DraftMessage draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        foreach (var attachment in draft.Attachments ?? [])
        {
            if (attachment.Size > DraftAttachment.MaximumSizeBytes)
            {
                throw new InvalidOperationException(
                    $"'{attachment.Name}' is larger than the 150 MB Microsoft Graph attachment limit.");
            }
        }
    }

    private static void ValidateCanSend(Mailbox mailbox)
    {
        if (mailbox.IsShared && !mailbox.CanSendAs && !mailbox.CanSendOnBehalf)
        {
            throw new InvalidOperationException(
                "This shared mailbox has not been configured with Send As or Send on Behalf permission.");
        }
    }

    private sealed record DraftAttachmentSet(
        IReadOnlyList<DraftAttachment> Supported,
        IReadOnlyList<string> ProviderIds,
        bool HasUnsupported)
    {
        public static DraftAttachmentSet Empty { get; } = new([], [], false);
    }

    internal static async Task UploadLargeAttachmentAsync(
        HttpClient httpClient,
        Uri uploadUrl,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var offset = 0L;
        while (offset < content.LongLength)
        {
            var count = (int)Math.Min(UploadChunkSizeBytes, content.LongLength - offset);
            var end = offset + count - 1;
            HttpResponseMessage? response = null;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
                    request.Content = new ByteArrayContent(content, checked((int)offset), count);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, end, content.LongLength);
                    response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken).ConfigureAwait(false);
                    if (attempt < 3 && IsTransient(response.StatusCode))
                    {
                        var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        response.Dispose();
                        response = null;
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    break;
                }
                catch (HttpRequestException) when (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                }
            }

            using (response)
            {
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                offset = end + 1;
                if (offset < content.LongLength)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    offset = NextExpectedOffset(document.RootElement, offset);
                }
            }
        }
    }

    internal static long NextExpectedOffset(JsonElement uploadSession, long fallback)
    {
        if (!uploadSession.TryGetProperty("nextExpectedRanges", out var ranges) ||
            ranges.ValueKind != JsonValueKind.Array ||
            ranges.GetArrayLength() == 0)
        {
            return fallback;
        }

        var range = ranges[0].GetString();
        var separator = range?.IndexOf('-') ?? -1;
        return long.TryParse(separator < 0 ? range : range![..separator], out var offset)
            ? offset
            : fallback;
    }

    internal static bool RequiresUploadSession(long size) => size >= SimpleAttachmentLimitBytes;

    public async Task<Mailbox> ValidateSharedMailboxAsync(
        MailAccount account,
        string address,
        CancellationToken cancellationToken = default)
    {
        if (!System.Net.Mail.MailAddress.TryCreate(address, out var parsed))
        {
            throw new ArgumentException("Enter a valid shared mailbox address.", nameof(address));
        }

        var mailbox = new Mailbox(account.AccountId, parsed.Address, parsed.Address, IsShared: true);
        using var document = await GetJsonAsync(
            account,
            $"{MailboxPath(account, mailbox)}/mailFolders/inbox?$select=displayName",
            cancellationToken).ConfigureAwait(false);
        // Graph cannot enumerate effective Send As/On Behalf grants; those are configured explicitly later.
        return mailbox;
    }

    private async Task<JsonDocument> GetJsonAsync(MailAccount account, string endpoint, CancellationToken cancellationToken)
    {
        using var response = await Microsoft365RequestScheduler.Shared.SendAsync(
            account,
            endpoint,
            async (_, token) =>
            {
                using var request = await CreateRequestAsync(account, HttpMethod.Get, endpoint, token).ConfigureAwait(false);
                return await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task SendJsonAsync(MailAccount account, HttpMethod method, string endpoint, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(account, method, endpoint, body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonForResponseAsync(MailAccount account, HttpMethod method, string endpoint, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(account, method, endpoint, body, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(MailAccount account, HttpMethod method, string endpoint, object? body, CancellationToken cancellationToken)
    {
        return await Microsoft365RequestScheduler.Shared.SendAsync(
            account,
            endpoint,
            async (_, token) =>
            {
                using var request = await CreateRequestAsync(account, method, endpoint, token).ConfigureAwait(false);
                if (body is not null)
                {
                    request.Content = JsonContent.Create(body);
                }
                return await _httpClient.SendAsync(request, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(MailAccount account, HttpMethod method, string endpoint, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            await authentication.GetAccessTokenAsync(account.AccountId, cancellationToken).ConfigureAwait(false));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Prefer", "outlook.body-content-type=html");
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = response.ReasonPhrase ?? "Microsoft Graph request failed";
        try
        {
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (error.RootElement.TryGetProperty("error", out var graphError) &&
                graphError.TryGetProperty("message", out var graphMessage))
            {
                message = graphMessage.GetString() ?? message;
            }
        }
        catch (JsonException)
        {
            // The status code remains useful when Graph returns a proxy/non-JSON error.
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    internal static string AttachmentEndpoint(MailAccount account, Mailbox mailbox, string messageId) =>
        $"{MailboxPath(account, mailbox)}/messages/{Uri.EscapeDataString(messageId)}/attachments?$top=100";

    internal static string MailboxPath(MailAccount account, Mailbox mailbox)
    {
        if (mailbox.AccountId != account.AccountId)
        {
            throw new InvalidOperationException("The mailbox does not belong to this account.");
        }

        return mailbox.IsShared ? $"users/{Uri.EscapeDataString(mailbox.Address)}" : "me";
    }

    internal static MailMessage MapMessage(Mailbox mailbox, JsonElement message)
    {
        var id = RequiredString(message, "id");
        if (message.TryGetProperty("@removed", out _))
        {
            return new MailMessage(
                mailbox.Id, id, null, null, "", "", new MailAddress("", ""), [],
                DateTimeOffset.MinValue, "", null, false, false, false, MailImportance.Normal, [], null, IsDeleted: true);
        }

        var body = message.TryGetProperty("body", out var bodyElement) ? bodyElement : default;
        var receivedAt = message.TryGetProperty("receivedDateTime", out var received) &&
                         DateTimeOffset.TryParse(received.GetString(), out var parsedReceived)
            ? parsedReceived
            : DateTimeOffset.UtcNow;
        var cacheBody = receivedAt >= DateTimeOffset.UtcNow.AddDays(-90);

        return new MailMessage(
            mailbox.Id,
            id,
            OptionalString(message, "conversationId"),
            OptionalString(message, "internetMessageId"),
            OptionalString(message, "parentFolderId") ?? "inbox",
            OptionalString(message, "subject") ?? "(no subject)",
            ReadAddress(message, "from"),
            ReadAddresses(message, "toRecipients"),
            receivedAt,
            OptionalString(message, "bodyPreview") ?? "",
            cacheBody && body.ValueKind == JsonValueKind.Object ? OptionalString(body, "content") : null,
            body.ValueKind == JsonValueKind.Object && string.Equals(OptionalString(body, "contentType"), "html", StringComparison.OrdinalIgnoreCase),
            message.TryGetProperty("isRead", out var isRead) && isRead.GetBoolean(),
            message.TryGetProperty("hasAttachments", out var attachments) && attachments.GetBoolean(),
            ParseImportance(OptionalString(message, "importance")),
            message.TryGetProperty("categories", out var categories)
                ? categories.EnumerateArray().Select(static category => category.GetString() ?? "").Where(static category => category.Length > 0).ToArray()
                : [],
            OptionalString(message, "@odata.etag"),
            message.TryGetProperty("flag", out var flag) &&
            string.Equals(OptionalString(flag, "flagStatus"), "flagged", StringComparison.OrdinalIgnoreCase),
            Cc: ReadAddresses(message, "ccRecipients"));
    }

    internal static MailAttachment MapAttachment(JsonElement attachment)
    {
        byte[]? bytes = null;
        if (attachment.TryGetProperty("contentBytes", out var contentBytes) && contentBytes.ValueKind == JsonValueKind.String)
        {
            try
            {
                bytes = contentBytes.GetBytesFromBase64();
            }
            catch (FormatException)
            {
                // A malformed attachment remains listed but is not rendered inline.
            }
        }

        return new MailAttachment(
            RequiredString(attachment, "id"),
            OptionalString(attachment, "name") ?? "Attachment",
            OptionalString(attachment, "contentType") ?? "application/octet-stream",
            attachment.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
            attachment.TryGetProperty("isInline", out var isInline) && isInline.GetBoolean(),
            OptionalString(attachment, "contentId"),
            bytes);
    }

    private static MailAddress ReadAddress(JsonElement message, string property)
    {
        if (!message.TryGetProperty(property, out var wrapper) || wrapper.ValueKind == JsonValueKind.Null ||
            !wrapper.TryGetProperty("emailAddress", out var address))
        {
            return new MailAddress("", "");
        }

        return new MailAddress(OptionalString(address, "name") ?? "", OptionalString(address, "address") ?? "");
    }

    private static IReadOnlyList<MailAddress> ReadAddresses(JsonElement message, string property) =>
        message.TryGetProperty(property, out var recipients)
            ? recipients.EnumerateArray().Select(recipient =>
            {
                var address = recipient.GetProperty("emailAddress");
                return new MailAddress(OptionalString(address, "name") ?? "", OptionalString(address, "address") ?? "");
            }).ToArray()
            : [];

    private static MailImportance ParseImportance(string? value) => value?.ToLowerInvariant() switch
    {
        "low" => MailImportance.Low,
        "high" => MailImportance.High,
        _ => MailImportance.Normal
    };

    private static string RequiredString(JsonElement element, string property) =>
        OptionalString(element, property) ?? throw new JsonException($"Microsoft Graph omitted required property '{property}'.");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}
