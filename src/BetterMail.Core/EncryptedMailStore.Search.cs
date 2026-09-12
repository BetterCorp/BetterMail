using System.Globalization;
using System.Text.Json;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    public Task<IReadOnlyList<MailMessage>> SearchFilteredMailAsync(SearchQuery query,
        IReadOnlyList<MailFolderKey> folders, int limit = 500, CancellationToken cancellationToken = default,
        string? accountId = null, string? mailboxId = null, bool includeUnknownFolders = false, IReadOnlyList<MailFolderKey>? knownFolders = null)
    {
        var clauses = new List<string> { "(EXISTS (SELECT 1 FROM json_each($folders) f WHERE json_extract(f.value, '$.MailboxId') = mailbox_id AND json_extract(f.value, '$.FolderId') = folder_id) OR ($unknown = 1 AND NOT EXISTS (SELECT 1 FROM json_each($known) f WHERE json_extract(f.value, '$.MailboxId') = mailbox_id AND json_extract(f.value, '$.FolderId') = folder_id) AND NOT EXISTS (SELECT 1 FROM mail_folders f WHERE f.mailbox_id = messages.mailbox_id AND f.provider_id = messages.folder_id)))",
            "($account IS NULL OR EXISTS (SELECT 1 FROM mailboxes m WHERE m.account_id = $account AND m.account_id || ':' || lower(m.address) = messages.mailbox_id))",
            "($mailbox IS NULL OR mailbox_id = $mailbox)" };
        var parameters = new List<(string Name, object Value)> { ("$folders", JsonSerializer.Serialize(folders)), ("$unknown", includeUnknownFolders), ("$known", System.Text.Json.JsonSerializer.Serialize(knownFolders ?? [])), ("$account", (object?)accountId ?? DBNull.Value), ("$mailbox", (object?)mailboxId ?? DBNull.Value) };
        string Add(object value) { var name = "$p" + parameters.Count; parameters.Add((name, value)); return name; }
        if (query.Terms.Count > 0)
        {
            var parameter = Add(query.FtsText);
            clauses.Add(_optimizedSearch
                ? $"rowid IN (SELECT rowid FROM message_search_v2 WHERE message_search_v2 MATCH {parameter})"
                : $"rowid IN (SELECT rowid FROM message_search WHERE message_search MATCH {parameter} UNION SELECT rowid FROM message_search_v2 WHERE message_search_v2 MATCH {parameter})");
        }
        foreach (var key in new[] { "from", "to", "cc", "subject", "category" })
        {
            if (query[key] is not { } value) continue;
            var parameter = Add(value);
            string Contains(string column) => $"instr(lower({column}), lower({parameter})) > 0";
            clauses.Add(key switch
            {
                "from" => $"({Contains("from_name")} OR {Contains("from_address")})",
                "to" or "cc" => $"EXISTS (SELECT 1 FROM json_each({(key == "to" ? "recipients_json" : "cc_recipients_json")}) recipient WHERE {Contains("json_extract(recipient.value, '$.Name')")} OR {Contains("json_extract(recipient.value, '$.Address')")})",
                "category" => $"EXISTS (SELECT 1 FROM json_each(categories_json) category WHERE lower(category.value) = lower({parameter}))",
                _ => Contains("subject")
            });
        }
        if (query["has"] is { } has) clauses.Add("has_attachments = " + (has == "noattachments" ? "0" : "1"));
        if (query["is"] is { } state) clauses.Add(state switch
        { "read" => "is_read = 1", "unread" => "is_read = 0", "flagged" => "is_flagged = 1", _ => "is_pinned = 1" });
        if (query["importance"] is { } importance) clauses.Add("importance = " + Add((int)Enum.Parse<MailImportance>(importance, true)));
        foreach (var date in query.Dates)
        {
            if (date.Time is { } time)
                clauses.Add($"strftime('%H:%M:%S', received_at, 'localtime') {date.Operator} {Add(time.ToString("HH:mm:ss", CultureInfo.InvariantCulture))}");
            else
            {
                var instant = date.Instant!.Value;
                string Julian(DateTimeOffset value) => "julianday(" + Add(value.ToUniversalTime().ToString("O")) + ")";
                if (date.WholeDay)
                {
                    // Compute the next local midnight independently, including DST changes.
                    var end = new DateTimeOffset(instant.LocalDateTime.Date.AddDays(1));
                    clauses.Add(date.Operator switch
                    {
                        "=" => $"(julianday(received_at) >= {Julian(instant)} AND julianday(received_at) < {Julian(end)})",
                        ">" => $"julianday(received_at) >= {Julian(end)}",
                        "<=" => $"julianday(received_at) < {Julian(end)}",
                        _ => $"julianday(received_at) {date.Operator} {Julian(instant)}"
                    });
                }
                else clauses.Add($"julianday(received_at) {date.Operator} {Julian(instant)}");
            }
        }
        return QueryMessagesAsync("WHERE " + string.Join(" AND ", clauses), limit, false, cancellationToken, parameters.ToArray());
    }
}
