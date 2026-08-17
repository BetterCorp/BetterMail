using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BetterMail.Core;
using CoreMailAddress = BetterMail.Core.MailAddress;
using MailAddressCollection = System.Net.Mail.MailAddressCollection;

namespace BetterMail.Google;

public sealed class GoogleGmailProvider(
    GoogleAuthService authentication,
    HttpClient? httpClient = null) : IMailProvider
{
    internal const string ArchiveFolderId = "BETTERMAIL_ARCHIVE";
    private const string ApiBase = "https://gmail.googleapis.com/gmail/v1/users/me";
    private static readonly string[] SystemLabels =
    [
        "CHAT", "SENT", "INBOX", "IMPORTANT", "TRASH", "DRAFT", "SPAM", "STARRED", "UNREAD",
        "CATEGORY_PERSONAL", "CATEGORY_SOCIAL", "CATEGORY_PROMOTIONS", "CATEGORY_UPDATES", "CATEGORY_FORUMS"
    ];
    private readonly HttpClient _http = httpClient ?? new HttpClient();
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _labelNames = new(StringComparer.Ordinal);

    public bool SupportsCloudDrafts => true;

    public async Task<IReadOnlyList<MailFolder>> GetFoldersAsync(
        MailAccount account,
        Mailbox mailbox,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var labels = await GetJsonAsync(account, "/labels", cancellationToken).ConfigureAwait(false);
        var labelMap = labels.RootElement.GetProperty("labels").EnumerateArray().ToDictionary(
            static label => RequiredString(label, "id"),
            static label => RequiredString(label, "name"),
            StringComparer.Ordinal);
        _labelNames[account.AccountId] = labelMap;

        var folders = new List<MailFolder>();
        foreach (var spec in FolderSpecs)
        {
            if (spec.Id != ArchiveFolderId && !labelMap.ContainsKey(spec.Id))
            {
                continue;
            }
            var counts = spec.Id == ArchiveFolderId
                ? await ArchiveCountsAsync(account, cancellationToken).ConfigureAwait(false)
                : await LabelCountsAsync(account, spec.Id, cancellationToken).ConfigureAwait(false);
            folders.Add(new MailFolder(
                mailbox.Id,
                spec.Id,
                spec.Name,
                counts.Unread,
                counts.Total,
                spec.WellKnownName));
        }
        return folders;
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
        Validate(account, mailbox);
        if (cursor?.StartsWith("history:", StringComparison.Ordinal) == true)
        {
            try
            {
                return await SyncHistoryAsync(account, mailbox, folderId, cursor, cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                cursor = null;
            }
        }
        return await SyncFullAsync(account, mailbox, folderId, cursor, receivedSince, cancellationToken).ConfigureAwait(false);
    }

    public Task MarkReadAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        bool isRead,
        CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(
            account,
            mailbox,
            messageId,
            isRead ? [] : ["UNREAD"],
            isRead ? ["UNREAD"] : [],
            cancellationToken);

    public async Task<MailMessage> GetMessageAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var document = await GetJsonAsync(
            account,
            $"/messages/{Escape(messageId)}?format=full",
            cancellationToken).ConfigureAwait(false);
        return MapMessage(mailbox, document.RootElement, LabelNames(account.AccountId));
    }

    public async Task<IReadOnlyList<MailMessage>> SearchMessagesAsync(
        MailAccount account,
        Mailbox mailbox,
        string query,
        int limit = 250,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        var ids = new List<string>();
        string? pageToken = null;
        do
        {
            var path = $"/messages?maxResults={Math.Min(500, limit - ids.Count)}&q={Escape(query)}" +
                       (pageToken is null ? "" : $"&pageToken={Escape(pageToken)}");
            using var page = await GetJsonAsync(account, path, cancellationToken).ConfigureAwait(false);
            if (page.RootElement.TryGetProperty("messages", out var messages))
            {
                ids.AddRange(messages.EnumerateArray().Select(static item => RequiredString(item, "id")));
            }
            pageToken = page.RootElement.TryGetProperty("nextPageToken", out var next)
                ? next.GetString()
                : null;
        }
        while (ids.Count < limit && !string.IsNullOrWhiteSpace(pageToken));
        return await GetMessagesAsync(account, mailbox, ids.Take(limit).ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MailHeader>> GetMessageHeadersAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var document = await GetJsonAsync(
            account,
            $"/messages/{Escape(messageId)}?format=metadata",
            cancellationToken).ConfigureAwait(false);
        return Headers(document.RootElement.GetProperty("payload"))
            .Select(static header => new MailHeader(header.Key, header.Value))
            .ToArray();
    }

    public async Task MoveMessageAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        string destinationFolderId,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        if (destinationFolderId == "TRASH")
        {
            using var response = await SendJsonAsync(
                account, HttpMethod.Post, $"/messages/{Escape(messageId)}/trash", new { }, cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<string> add;
        IReadOnlyList<string> remove;
        switch (destinationFolderId)
        {
            case "INBOX":
                add = ["INBOX"];
                remove = ["TRASH", "SPAM"];
                break;
            case "SPAM":
                add = ["SPAM"];
                remove = ["INBOX", "TRASH"];
                break;
            case ArchiveFolderId:
                add = [];
                remove = ["INBOX", "TRASH", "SPAM"];
                break;
            default:
                throw new NotSupportedException("Gmail messages can only be moved to Inbox, Archive, Spam, or Trash.");
        }
        await ModifyLabelsAsync(account, mailbox, messageId, add, remove, cancellationToken).ConfigureAwait(false);
    }

    public Task SetFlaggedAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        bool isFlagged,
        CancellationToken cancellationToken = default) =>
        ModifyLabelsAsync(
            account,
            mailbox,
            messageId,
            isFlagged ? ["STARRED"] : [],
            isFlagged ? [] : ["STARRED"],
            cancellationToken);

    public async Task<IReadOnlyList<MailAttachment>> GetAttachmentsAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var document = await GetJsonAsync(
            account,
            $"/messages/{Escape(messageId)}?format=full",
            cancellationToken).ConfigureAwait(false);
        return MapAttachments(document.RootElement.GetProperty("payload"));
    }

    public async Task<MailAttachment?> GetAttachmentAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        string attachmentId,
        CancellationToken cancellationToken = default)
    {
        var attachment = (await GetAttachmentsAsync(account, mailbox, messageId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.ProviderId == attachmentId);
        if (attachment is null || attachment.ContentBytes is not null)
        {
            return attachment;
        }
        using var document = await GetJsonAsync(
            account,
            $"/messages/{Escape(messageId)}/attachments/{Escape(attachmentId)}",
            cancellationToken).ConfigureAwait(false);
        return attachment with { ContentBytes = Decode(RequiredString(document.RootElement, "data")) };
    }

    public async Task SendAsync(
        MailAccount account,
        Mailbox mailbox,
        DraftMessage draft,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var response = await SendJsonAsync(
            account,
            HttpMethod.Post,
            "/messages/send",
            new { raw = Encode(BuildMime(mailbox, draft)) },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CloudDraft>> GetDraftsAsync(
        MailAccount account,
        Mailbox mailbox,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        var ids = new List<string>();
        string? pageToken = null;
        do
        {
            var path = "/drafts?maxResults=100" + (pageToken is null ? "" : $"&pageToken={Escape(pageToken)}");
            using var page = await GetJsonAsync(account, path, cancellationToken).ConfigureAwait(false);
            if (page.RootElement.TryGetProperty("drafts", out var drafts))
            {
                ids.AddRange(drafts.EnumerateArray().Select(static draft => RequiredString(draft, "id")));
            }
            pageToken = page.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        var result = new CloudDraft[ids.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, ids.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = cancellationToken },
            async (index, token) => result[index] = await GetDraftAsync(account, mailbox, ids[index], token).ConfigureAwait(false));
        return result;
    }

    public async Task<CloudDraft> GetDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var document = await GetJsonAsync(
            account,
            $"/drafts/{Escape(draftId)}?format=full",
            cancellationToken).ConfigureAwait(false);
        return await MapDraftAsync(account, mailbox, document.RootElement, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudDraft> CreateDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        DraftMessage draft,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var response = await SendJsonAsync(
            account,
            HttpMethod.Post,
            "/drafts",
            new { message = new { raw = Encode(BuildMime(mailbox, draft)) } },
            cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await GetDraftAsync(account, mailbox, RequiredString(document.RootElement, "id"), cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudDraft> UpdateDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        DraftMessage draft,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var response = await SendJsonAsync(
            account,
            HttpMethod.Put,
            $"/drafts/{Escape(draftId)}",
            new { message = new { raw = Encode(BuildMime(mailbox, draft)) } },
            cancellationToken).ConfigureAwait(false);
        return await GetDraftAsync(account, mailbox, draftId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var response = await SendAsync(
            account, HttpMethod.Delete, $"/drafts/{Escape(draftId)}", null, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        string draftId,
        CancellationToken cancellationToken = default)
    {
        Validate(account, mailbox);
        using var response = await SendJsonAsync(
            account, HttpMethod.Post, "/drafts/send", new { id = draftId }, cancellationToken).ConfigureAwait(false);
    }

    internal static MailMessage MapMessage(
        Mailbox mailbox,
        JsonElement message,
        IReadOnlyDictionary<string, string>? labelNames = null)
    {
        var payload = message.GetProperty("payload");
        var headers = Headers(payload);
        var labels = message.TryGetProperty("labelIds", out var labelArray)
            ? labelArray.EnumerateArray().Select(static label => label.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : [];
        var body = MessageBody(payload);
        var receivedAt = message.TryGetProperty("internalDate", out var internalDate) &&
                         long.TryParse(internalDate.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.TryParse(Header(headers, "Date"), CultureInfo.InvariantCulture, out var parsed) ? parsed : DateTimeOffset.MinValue;
        return new MailMessage(
            mailbox.Id,
            RequiredString(message, "id"),
            OptionalString(message, "threadId"),
            Header(headers, "Message-ID"),
            PrimaryFolder(labels),
            Header(headers, "Subject"),
            ParseAddresses(Header(headers, "From")).FirstOrDefault() ?? new CoreMailAddress("", ""),
            ParseAddresses(Header(headers, "To")),
            receivedAt,
            OptionalString(message, "snippet") ?? "",
            body.Content,
            body.IsHtml,
            !labels.Contains("UNREAD"),
            MapAttachments(payload).Count > 0,
            labels.Contains("IMPORTANT") ? MailImportance.High : MailImportance.Normal,
            labels.Where(label => !SystemLabels.Contains(label, StringComparer.Ordinal))
                .Select(label => labelNames?.GetValueOrDefault(label) ?? label)
                .ToArray(),
            OptionalString(message, "historyId"),
            labels.Contains("STARRED"),
            false,
            ParseAddresses(Header(headers, "Cc")));
    }

    internal static string BuildMime(Mailbox mailbox, DraftMessage draft)
    {
        var builder = new StringBuilder();
        HeaderLine(builder, "From", FormatAddress(new CoreMailAddress(mailbox.DisplayName, mailbox.Address)));
        HeaderLine(builder, "To", string.Join(", ", draft.To.Select(FormatAddress)));
        if (draft.Cc?.Count > 0)
        {
            HeaderLine(builder, "Cc", string.Join(", ", draft.Cc.Select(FormatAddress)));
        }
        if (draft.Bcc?.Count > 0)
        {
            HeaderLine(builder, "Bcc", string.Join(", ", draft.Bcc.Select(FormatAddress)));
        }
        HeaderLine(builder, "Subject", EncodeHeader(draft.Subject));
        HeaderLine(builder, "Date", DateTimeOffset.Now.ToString("r", CultureInfo.InvariantCulture));
        HeaderLine(builder, "MIME-Version", "1.0");

        if (draft.Attachments?.Count > 0)
        {
            var boundary = "bettermail-" + Guid.NewGuid().ToString("N");
            HeaderLine(builder, "Content-Type", $"multipart/mixed; boundary=\"{boundary}\"");
            builder.Append("\r\n--").Append(boundary).Append("\r\n");
            AppendBody(builder, draft.Body, draft.IsHtml);
            foreach (var attachment in draft.Attachments)
            {
                builder.Append("\r\n--").Append(boundary).Append("\r\n");
                HeaderLine(builder, "Content-Type", $"{SafeHeader(attachment.ContentType)}; name=\"{EncodeHeader(attachment.Name)}\"");
                HeaderLine(builder, "Content-Transfer-Encoding", "base64");
                HeaderLine(builder, "Content-Disposition", $"{(attachment.IsInline ? "inline" : "attachment")}; filename=\"{EncodeHeader(attachment.Name)}\"");
                if (!string.IsNullOrWhiteSpace(attachment.ContentId))
                {
                    HeaderLine(builder, "Content-ID", $"<{SafeHeader(attachment.ContentId)}>" );
                }
                builder.Append("\r\n").Append(WrapBase64(Convert.ToBase64String(attachment.ContentBytes)));
            }
            builder.Append("\r\n--").Append(boundary).Append("--\r\n");
        }
        else
        {
            AppendBody(builder, draft.Body, draft.IsHtml);
        }
        return builder.ToString();
    }

    internal static string PrimaryFolder(IReadOnlySet<string> labels) =>
        labels.Contains("TRASH") ? "TRASH" :
        labels.Contains("SPAM") ? "SPAM" :
        labels.Contains("INBOX") ? "INBOX" :
        labels.Contains("SENT") ? "SENT" : ArchiveFolderId;

    private async Task<MailSyncPage> SyncFullAsync(
        MailAccount account,
        Mailbox mailbox,
        string folderId,
        string? cursor,
        DateTimeOffset? receivedSince,
        CancellationToken cancellationToken)
    {
        string? pageToken = null;
        string? newestHistory = null;
        if (cursor?.StartsWith("full:", StringComparison.Ordinal) == true)
        {
            var parts = cursor.Split(':', 3);
            pageToken = parts.ElementAtOrDefault(1);
            newestHistory = parts.ElementAtOrDefault(2);
        }
        var query = FolderQuery(folderId, receivedSince);
        var path = $"/messages?maxResults=100{query}" +
                   (pageToken is null ? "" : $"&pageToken={Escape(pageToken)}");
        using var page = await GetJsonAsync(account, path, cancellationToken).ConfigureAwait(false);
        var ids = page.RootElement.TryGetProperty("messages", out var messages)
            ? messages.EnumerateArray().Select(static item => RequiredString(item, "id")).ToArray()
            : [];
        var mapped = await GetMessagesAsync(account, mailbox, ids, cancellationToken).ConfigureAwait(false);
        newestHistory = mapped.Select(static message => message.ETag)
            .Where(static value => ulong.TryParse(value, out _))
            .Append(newestHistory)
            .Where(static value => ulong.TryParse(value, out _))
            .OrderByDescending(static value => ulong.Parse(value!, CultureInfo.InvariantCulture))
            .FirstOrDefault();
        var nextPage = page.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        if (!string.IsNullOrWhiteSpace(nextPage))
        {
            return new(mapped, $"full:{nextPage}:{newestHistory}", true);
        }
        if (string.IsNullOrWhiteSpace(newestHistory))
        {
            using var profile = await GetJsonAsync(account, "/profile", cancellationToken).ConfigureAwait(false);
            newestHistory = RequiredString(profile.RootElement, "historyId");
        }
        return new(mapped, $"history:{newestHistory}", false);
    }

    private async Task<MailSyncPage> SyncHistoryAsync(
        MailAccount account,
        Mailbox mailbox,
        string folderId,
        string cursor,
        CancellationToken cancellationToken)
    {
        var parts = cursor.Split(':', 3);
        var start = parts[1];
        var pageToken = parts.ElementAtOrDefault(2);
        var path = $"/history?startHistoryId={Escape(start)}&maxResults=100" +
                   (folderId == ArchiveFolderId ? "" : $"&labelId={Escape(folderId)}") +
                   (string.IsNullOrWhiteSpace(pageToken) ? "" : $"&pageToken={Escape(pageToken)}");
        using var page = await GetJsonAsync(account, path, cancellationToken).ConfigureAwait(false);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        if (page.RootElement.TryGetProperty("history", out var history))
        {
            foreach (var entry in history.EnumerateArray())
            {
                AddHistoryIds(entry, "messages", changed);
                AddHistoryIds(entry, "messagesAdded", changed);
                AddHistoryIds(entry, "labelsAdded", changed);
                AddHistoryIds(entry, "labelsRemoved", changed);
                AddHistoryIds(entry, "messagesDeleted", deleted);
            }
        }
        changed.ExceptWith(deleted);
        var mapped = (await GetMessagesAsync(account, mailbox, changed.ToArray(), cancellationToken).ConfigureAwait(false)).ToList();
        mapped.AddRange(deleted.Select(id => DeletedMessage(mailbox, id, folderId)));
        var nextPage = page.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        return !string.IsNullOrWhiteSpace(nextPage)
            ? new(mapped, $"history:{start}:{nextPage}", true)
            : new(mapped, $"history:{RequiredString(page.RootElement, "historyId")}", false);
    }

    private async Task<IReadOnlyList<MailMessage>> GetMessagesAsync(
        MailAccount account,
        Mailbox mailbox,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        var result = new MailMessage?[ids.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, ids.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                try
                {
                    using var document = await GetJsonAsync(
                        account, $"/messages/{Escape(ids[index])}?format=full", token).ConfigureAwait(false);
                    result[index] = MapMessage(mailbox, document.RootElement, LabelNames(account.AccountId));
                }
                catch (GoogleApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
                {
                }
            });
        return result.Where(static message => message is not null).Select(static message => message!).ToArray();
    }

    private async Task<CloudDraft> MapDraftAsync(
        MailAccount account,
        Mailbox mailbox,
        JsonElement draft,
        CancellationToken cancellationToken)
    {
        var id = RequiredString(draft, "id");
        var message = draft.GetProperty("message");
        var payload = message.GetProperty("payload");
        var headers = Headers(payload);
        var body = MessageBody(payload);
        var attachments = MapAttachments(payload).ToArray();
        for (var index = 0; index < attachments.Length; index++)
        {
            if (attachments[index].ContentBytes is null)
            {
                using var content = await GetJsonAsync(
                    account,
                    $"/messages/{Escape(RequiredString(message, "id"))}/attachments/{Escape(attachments[index].ProviderId)}",
                    cancellationToken).ConfigureAwait(false);
                attachments[index] = attachments[index] with
                {
                    ContentBytes = Decode(RequiredString(content.RootElement, "data"))
                };
            }
        }
        var updatedAt = long.TryParse(OptionalString(message, "internalDate"), out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.UtcNow;
        return new CloudDraft(
            id,
            account.AccountId,
            mailbox.Id,
            new DraftMessage(
                Header(headers, "Subject"),
                ParseAddresses(Header(headers, "To")),
                body.Content,
                body.IsHtml,
                ParseAddresses(Header(headers, "Cc")),
                ParseAddresses(Header(headers, "Bcc")),
                attachments.Select(static attachment => new DraftAttachment(
                    attachment.Name,
                    attachment.ContentType,
                    attachment.ContentBytes ?? [],
                    attachment.IsInline,
                    attachment.ContentId)).ToArray()),
            updatedAt,
            OptionalString(message, "historyId"),
            false,
            OptionalString(message, "threadId"));
    }

    private async Task ModifyLabelsAsync(
        MailAccount account,
        Mailbox mailbox,
        string messageId,
        IReadOnlyList<string> add,
        IReadOnlyList<string> remove,
        CancellationToken cancellationToken)
    {
        Validate(account, mailbox);
        using var response = await SendJsonAsync(
            account,
            HttpMethod.Post,
            $"/messages/{Escape(messageId)}/modify",
            new { addLabelIds = add, removeLabelIds = remove },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FolderCounts> LabelCountsAsync(
        MailAccount account,
        string labelId,
        CancellationToken cancellationToken)
    {
        using var label = await GetJsonAsync(account, $"/labels/{Escape(labelId)}", cancellationToken).ConfigureAwait(false);
        return new(
            label.RootElement.TryGetProperty("messagesTotal", out var total) ? total.GetInt32() : 0,
            label.RootElement.TryGetProperty("messagesUnread", out var unread) ? unread.GetInt32() : 0);
    }

    private async Task<FolderCounts> ArchiveCountsAsync(MailAccount account, CancellationToken cancellationToken)
    {
        var query = Escape(ArchiveQuery);
        using var total = await GetJsonAsync(account, $"/messages?maxResults=1&q={query}", cancellationToken).ConfigureAwait(false);
        using var unread = await GetJsonAsync(account, $"/messages?maxResults=1&q={query}%20is%3Aunread", cancellationToken).ConfigureAwait(false);
        return new(ResultEstimate(total.RootElement), ResultEstimate(unread.RootElement));
    }

    private async Task<JsonDocument> GetJsonAsync(
        MailAccount account,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(account, HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendJsonAsync(
        MailAccount account,
        HttpMethod method,
        string path,
        object value,
        CancellationToken cancellationToken) =>
        SendAsync(account, method, path, JsonSerializer.Serialize(value), cancellationToken);

    private async Task<HttpResponseMessage> SendAsync(
        MailAccount account,
        HttpMethod method,
        string path,
        string? json,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await authentication.GetAccessTokenAsync(account.AccountId, cancellationToken).ConfigureAwait(false));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }
        var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = response.StatusCode;
        response.Dispose();
        throw new GoogleApiException(status, ErrorMessage(error));
    }

    private IReadOnlyDictionary<string, string> LabelNames(string accountId) =>
        _labelNames.GetValueOrDefault(accountId) ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static string FolderQuery(string folderId, DateTimeOffset? receivedSince)
    {
        var values = new List<string>();
        if (folderId == ArchiveFolderId)
        {
            values.Add($"q={Escape(ArchiveQuery)}");
        }
        else
        {
            values.Add($"labelIds={Escape(folderId)}");
            if (folderId is "TRASH" or "SPAM")
            {
                values.Add("includeSpamTrash=true");
            }
        }
        if (receivedSince is not null)
        {
            var after = $"after:{receivedSince.Value.UtcDateTime:yyyy/MM/dd}";
            var queryIndex = values.FindIndex(static value => value.StartsWith("q=", StringComparison.Ordinal));
            if (queryIndex >= 0)
            {
                values[queryIndex] += "%20" + Escape(after);
            }
            else
            {
                values.Add("q=" + Escape(after));
            }
        }
        return values.Count == 0 ? "" : "&" + string.Join('&', values);
    }

    private static IReadOnlyList<MailAttachment> MapAttachments(JsonElement payload)
    {
        var result = new List<MailAttachment>();
        CollectAttachments(payload, result);
        return result;
    }

    private static void CollectAttachments(JsonElement part, List<MailAttachment> result)
    {
        var filename = OptionalString(part, "filename") ?? "";
        var headers = Headers(part);
        var disposition = Header(headers, "Content-Disposition");
        var body = part.TryGetProperty("body", out var bodyElement) ? bodyElement : default;
        if (!string.IsNullOrWhiteSpace(filename) || disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase))
        {
            var attachmentId = body.ValueKind == JsonValueKind.Object ? OptionalString(body, "attachmentId") : null;
            var data = body.ValueKind == JsonValueKind.Object ? OptionalString(body, "data") : null;
            result.Add(new MailAttachment(
                attachmentId ?? $"inline:{OptionalString(part, "partId") ?? result.Count.ToString(CultureInfo.InvariantCulture)}",
                string.IsNullOrWhiteSpace(filename) ? "attachment" : filename,
                OptionalString(part, "mimeType") ?? "application/octet-stream",
                body.ValueKind == JsonValueKind.Object && body.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                disposition.StartsWith("inline", StringComparison.OrdinalIgnoreCase),
                Header(headers, "Content-ID").Trim('<', '>'),
                string.IsNullOrWhiteSpace(data) ? null : Decode(data)));
        }
        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray())
            {
                CollectAttachments(child, result);
            }
        }
    }

    private static MessageContent MessageBody(JsonElement payload)
    {
        MessageContent? plain = null;
        MessageContent? html = null;
        CollectBody(payload, ref plain, ref html);
        return html ?? plain ?? new MessageContent("", false);
    }

    private static void CollectBody(JsonElement part, ref MessageContent? plain, ref MessageContent? html)
    {
        var mimeType = OptionalString(part, "mimeType") ?? "";
        var filename = OptionalString(part, "filename") ?? "";
        if (string.IsNullOrWhiteSpace(filename) && part.TryGetProperty("body", out var body) &&
            OptionalString(body, "data") is { Length: > 0 } data)
        {
            if (mimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                html ??= new MessageContent(Encoding.UTF8.GetString(Decode(data)), true);
            }
            else if (mimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            {
                plain ??= new MessageContent(Encoding.UTF8.GetString(Decode(data)), false);
            }
        }
        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray())
            {
                CollectBody(child, ref plain, ref html);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> Headers(JsonElement payload)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload.TryGetProperty("headers", out var values))
        {
            foreach (var header in values.EnumerateArray())
            {
                var name = RequiredString(header, "name");
                var value = OptionalString(header, "value") ?? "";
                headers[name] = headers.TryGetValue(name, out var existing) ? $"{existing}, {value}" : value;
            }
        }
        return headers;
    }

    private static IReadOnlyList<CoreMailAddress> ParseAddresses(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }
        try
        {
            var collection = new MailAddressCollection();
            collection.Add(value);
            return collection.Select(static address => new CoreMailAddress(address.DisplayName, address.Address)).ToArray();
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static void AddHistoryIds(JsonElement entry, string property, HashSet<string> destination)
    {
        if (!entry.TryGetProperty(property, out var values))
        {
            return;
        }
        foreach (var value in values.EnumerateArray())
        {
            var message = value.TryGetProperty("message", out var nested) ? nested : value;
            if (OptionalString(message, "id") is { Length: > 0 } id)
            {
                destination.Add(id);
            }
        }
    }

    private static MailMessage DeletedMessage(Mailbox mailbox, string messageId, string folderId) => new(
        mailbox.Id, messageId, null, null, folderId, "", new CoreMailAddress("", ""), [],
        DateTimeOffset.MinValue, "", null, false, true, false, MailImportance.Normal, [], null,
        IsDeleted: true);

    private static string FormatAddress(CoreMailAddress address) =>
        string.IsNullOrWhiteSpace(address.Name)
            ? SafeHeader(address.Address)
            : $"{EncodeHeader(address.Name)} <{SafeHeader(address.Address)}>";

    private static void AppendBody(StringBuilder builder, string body, bool isHtml)
    {
        HeaderLine(builder, "Content-Type", $"text/{(isHtml ? "html" : "plain")}; charset=utf-8");
        HeaderLine(builder, "Content-Transfer-Encoding", "8bit");
        builder.Append("\r\n").Append(body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    private static void HeaderLine(StringBuilder builder, string name, string value) =>
        builder.Append(name).Append(": ").Append(SafeHeader(value)).Append("\r\n");

    private static string SafeHeader(string value) => value.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
    private static string EncodeHeader(string value) => value.All(static character => character is >= ' ' and <= '~')
        ? SafeHeader(value)
        : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(SafeHeader(value)))}?=";
    private static string WrapBase64(string value) => string.Join("\r\n", value.Chunk(76).Select(static chunk => new string(chunk)));
    private static string Header(IReadOnlyDictionary<string, string> headers, string name) => headers.GetValueOrDefault(name) ?? "";
    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    private static int ResultEstimate(JsonElement element) => element.TryGetProperty("resultSizeEstimate", out var value) ? value.GetInt32() : 0;
    private static string RequiredString(JsonElement element, string property) => OptionalString(element, property) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Gmail did not return '{property}'.");
    private static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value.GetString() : null;
    private static string ErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("error", out var error) &&
                   error.TryGetProperty("message", out var message)
                ? message.GetString() ?? json
                : json;
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void Validate(MailAccount account, Mailbox mailbox)
    {
        if (account.ProviderId != GoogleAuthService.Id || mailbox.AccountId != account.AccountId || mailbox.IsShared)
        {
            throw new InvalidOperationException("The Gmail mailbox does not belong to this Google account.");
        }
    }

    private const string ArchiveQuery = "-label:inbox -label:sent -label:drafts -label:spam -label:trash";
    private static readonly (string Id, string Name, string WellKnownName)[] FolderSpecs =
    [
        ("INBOX", "Inbox", "inbox"),
        (ArchiveFolderId, "Archive", "archive"),
        ("SENT", "Sent", "sentitems"),
        ("SPAM", "Spam", "junkemail"),
        ("TRASH", "Trash", "deleteditems")
    ];

    private sealed record FolderCounts(int Total, int Unread);
    private sealed record MessageContent(string Content, bool IsHtml);
    private sealed class GoogleApiException(HttpStatusCode statusCode, string message) : HttpRequestException(message, null, statusCode)
    {
        public new HttpStatusCode StatusCode { get; } = statusCode;
    }
}
