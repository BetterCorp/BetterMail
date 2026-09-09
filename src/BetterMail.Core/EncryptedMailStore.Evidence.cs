using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    private const string EvidenceSchema = """
        CREATE TABLE IF NOT EXISTS evidence_messages(
            id TEXT PRIMARY KEY, mailbox_id TEXT NOT NULL, provider_id TEXT NOT NULL,
            payload_json TEXT NOT NULL, internet_message_id TEXT, mime BLOB, mime_sha256 TEXT,
            attachment_state TEXT NOT NULL DEFAULT 'Pending', UNIQUE(mailbox_id, provider_id));
        CREATE INDEX IF NOT EXISTS evidence_messages_mailbox ON evidence_messages(mailbox_id);
        CREATE INDEX IF NOT EXISTS evidence_messages_internet_id ON evidence_messages(internet_message_id);
        CREATE TABLE IF NOT EXISTS evidence_documents(
            id TEXT PRIMARY KEY, message_id TEXT NOT NULL REFERENCES evidence_messages(id) ON DELETE CASCADE,
            attachment_id TEXT NOT NULL, mailbox_id TEXT NOT NULL, name TEXT NOT NULL,
            text TEXT NOT NULL, sha256 TEXT, state TEXT NOT NULL, kind INTEGER NOT NULL,
            verification INTEGER NOT NULL, payload_json TEXT NOT NULL, content BLOB,
            UNIQUE(message_id, attachment_id));
        CREATE INDEX IF NOT EXISTS evidence_documents_scope ON evidence_documents(mailbox_id, message_id);
        CREATE INDEX IF NOT EXISTS evidence_documents_hash ON evidence_documents(sha256);
        CREATE VIRTUAL TABLE IF NOT EXISTS evidence_document_search USING fts5(name, text, content=evidence_documents, content_rowid=rowid);
        CREATE TRIGGER IF NOT EXISTS evidence_documents_ai AFTER INSERT ON evidence_documents BEGIN
            INSERT INTO evidence_document_search(rowid,name,text) VALUES(new.rowid,new.name,new.text);
        END;
        CREATE TRIGGER IF NOT EXISTS evidence_documents_ad AFTER DELETE ON evidence_documents BEGIN
            INSERT INTO evidence_document_search(evidence_document_search,rowid,name,text) VALUES('delete',old.rowid,old.name,old.text);
        END;
        CREATE TRIGGER IF NOT EXISTS evidence_documents_au AFTER UPDATE OF name,text ON evidence_documents BEGIN
            INSERT INTO evidence_document_search(evidence_document_search,rowid,name,text) VALUES('delete',old.rowid,old.name,old.text);
            INSERT INTO evidence_document_search(rowid,name,text) VALUES(new.rowid,new.name,new.text);
        END;
        CREATE TABLE IF NOT EXISTS evidence_reviews(
            id INTEGER PRIMARY KEY, document_id TEXT NOT NULL REFERENCES evidence_documents(id) ON DELETE CASCADE,
            payload_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS evidence_exports(
            id TEXT PRIMARY KEY, payload_json TEXT NOT NULL, expires_at TEXT NOT NULL, content BLOB NOT NULL);
        DELETE FROM evidence_exports WHERE julianday(expires_at) <= julianday('now');
        """;

    public Task<EvidenceMessage> CaptureEvidenceMessageAsync(MailMessage message, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            // Captures survive cache pruning and moves; removing the account removes its evidence too.
            // Provider flags can exclude inline-only attachments; enumeration determines coverage.
            var captured = new EvidenceMessage(Guid.NewGuid().ToString("N"), message, DateTimeOffset.UtcNow);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO evidence_messages(id,mailbox_id,provider_id,payload_json,internet_message_id,attachment_state)
                SELECT $id,$mailbox,$provider,$payload,$internet,$state
                WHERE EXISTS(SELECT 1 FROM mailboxes WHERE account_id || ':' || lower(address)=$mailbox);
                SELECT payload_json FROM evidence_messages WHERE mailbox_id=$mailbox AND provider_id=$provider;
                """;
            command.Parameters.AddWithValue("$id", captured.Id);
            command.Parameters.AddWithValue("$mailbox", message.MailboxId);
            command.Parameters.AddWithValue("$provider", message.ProviderId);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(captured));
            command.Parameters.AddWithValue("$internet", (object?)message.InternetMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", captured.AttachmentState);
            return await command.ExecuteScalarAsync(cancellationToken) is string saved
                ? JsonSerializer.Deserialize<EvidenceMessage>(saved)!
                : throw new EvidenceException("mailbox_unavailable", "The source mailbox was removed before evidence could be captured.");
        }, cancellationToken);

    public Task<EvidenceMessage?> GetEvidenceMessageAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM evidence_messages WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteScalarAsync(cancellationToken) is string json ? JsonSerializer.Deserialize<EvidenceMessage>(json) : null;
        }, cancellationToken);

    public Task SetEvidenceMessageStateAsync(EvidenceMessage message, byte[]? mime = null, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE evidence_messages SET payload_json=$payload,attachment_state=$state,
                    mime=coalesce(mime,$mime),mime_sha256=coalesce(mime_sha256,$hash) WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(message));
            command.Parameters.AddWithValue("$state", message.AttachmentState);
            command.Parameters.AddWithValue("$mime", (object?)mime ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)message.MimeSha256 ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<EvidenceDocument?> GetEvidenceDocumentAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM evidence_documents WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteScalarAsync(cancellationToken) is string json ? JsonSerializer.Deserialize<EvidenceDocument>(json) : null;
        }, cancellationToken);

    public Task SaveEvidenceDocumentAsync(EvidenceDocument document, byte[]? content, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO evidence_documents(id,message_id,attachment_id,mailbox_id,name,text,sha256,state,kind,verification,payload_json,content)
                VALUES($id,$message,$attachment,$mailbox,$name,$text,$hash,$state,$kind,$verification,$payload,$content)
                ON CONFLICT(id) DO UPDATE SET text=excluded.text,sha256=excluded.sha256,state=excluded.state,
                    payload_json=excluded.payload_json,content=coalesce(evidence_documents.content,excluded.content);
                """;
            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$message", document.MessageId);
            command.Parameters.AddWithValue("$attachment", document.AttachmentId);
            command.Parameters.AddWithValue("$mailbox", document.MailboxId);
            command.Parameters.AddWithValue("$name", document.Name);
            command.Parameters.AddWithValue("$text", document.Extraction.Text);
            command.Parameters.AddWithValue("$hash", (object?)document.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", document.State);
            command.Parameters.AddWithValue("$kind", (int)document.Kind);
            command.Parameters.AddWithValue("$verification", (int)document.Verification);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(document));
            command.Parameters.AddWithValue("$content", (object?)content ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<byte[]?> GetEvidenceContentAsync(string id, bool message = false, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = message ? "SELECT mime FROM evidence_messages WHERE id=$id;" : "SELECT content FROM evidence_documents WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteScalarAsync(cancellationToken) as byte[];
        }, cancellationToken);

    public async Task<EvidencePage<EvidenceDocument>> SearchEvidenceDocumentsAsync(string[] mailboxIds, string query = "",
        string? cursor = null, int limit = 50, string? messageId = null, DocumentKind? kind = null,
        VerificationStatus? verification = null, CancellationToken cancellationToken = default)
    {
        ValidateEvidenceScope(mailboxIds, limit);
        if (query is null || query.Length > 1000) throw new EvidenceException("invalid_query", "Supply a search query of at most 1,000 characters.");
        if (kind is not null && !Enum.IsDefined(kind.Value) || verification is not null && !Enum.IsDefined(verification.Value))
            throw new EvidenceException("invalid_filter", "Choose a valid document type and verification status.");
        var scope = EvidenceCursor.ScopeFor(new { mailboxIds = mailboxIds.Order(), query, messageId, kind, verification });
        var continuation = EvidenceCursor.Parse(cursor, scope);
        var (items, next) = await WithLockAsync(async connection =>
        {
            var upper = continuation?.Upper ?? await EvidenceUpperAsync(connection, "evidence_documents", cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.rowid,d.payload_json FROM evidence_documents d
                WHERE d.mailbox_id IN (SELECT value FROM json_each($mailboxes)) AND d.rowid>$after AND d.rowid<=$upper
                    AND ($message IS NULL OR d.message_id=$message) AND ($kind IS NULL OR d.kind=$kind)
                    AND ($verification IS NULL OR d.verification=$verification)
                """ + (string.IsNullOrWhiteSpace(query) ? "" : " AND d.rowid IN (SELECT rowid FROM evidence_document_search WHERE evidence_document_search MATCH $query)") +
                " ORDER BY d.rowid LIMIT $limit;";
            AddEvidencePageParameters(command, mailboxIds, continuation?.After ?? 0, upper, limit);
            command.Parameters.AddWithValue("$message", (object?)messageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$kind", kind is null ? DBNull.Value : (object)(int)kind);
            command.Parameters.AddWithValue("$verification", verification is null ? DBNull.Value : (object)(int)verification);
            command.Parameters.AddWithValue("$query", string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(term => $"\"{term.Replace("\"", "\"\"")}\"*")));
            var results = new List<EvidenceDocument>();
            long last = 0;
            string? next = null;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (results.Count == limit) { next = new EvidenceCursor(scope, last, upper).Encode(); break; }
                last = reader.GetInt64(0);
                results.Add(JsonSerializer.Deserialize<EvidenceDocument>(reader.GetString(1))!);
            }
            return (results, next);
        }, cancellationToken);
        return new(items, next, await GetEvidenceCompletenessAsync(mailboxIds, cancellationToken));
    }

    public async Task<EvidencePage<MailMessage>> SearchEvidenceMailAsync(string[] mailboxIds, string[] terms,
        string? cursor = null, int limit = 50, bool attachmentsOnly = false, string? threadId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateEvidenceScope(mailboxIds, limit);
        if (terms is null || terms.Length > 40 || terms.Any(term => term is null || term.Length > 1000)) throw new EvidenceException("invalid_query", "Use at most 40 search terms of up to 1,000 characters.");
        var scope = EvidenceCursor.ScopeFor(new { mailboxIds = mailboxIds.Order(), terms, attachmentsOnly, threadId });
        var continuation = EvidenceCursor.Parse(cursor, scope);
        var (items, next) = await WithLockAsync(async connection =>
        {
            var upper = continuation?.Upper ?? await EvidenceUpperAsync(connection, "messages", cancellationToken);
            await using var command = connection.CreateCommand();
            // ponytail: substring scans scoped cached bodies; use a trigram index if evidence searches become slow.
            command.CommandText = """
                SELECT m.mailbox_id,m.provider_id,m.conversation_id,m.internet_message_id,m.folder_id,m.subject,
                    m.from_name,m.from_address,m.recipients_json,m.received_at,m.preview,m.body,m.is_html,
                    m.is_read,m.has_attachments,m.importance,m.categories_json,m.etag,m.is_flagged,
                    m.cc_recipients_json,m.body_blob,m.is_pinned,m.rowid
                FROM messages m WHERE m.mailbox_id IN (SELECT value FROM json_each($mailboxes))
                    AND m.rowid>$after AND m.rowid<=$upper AND ($attachments=0 OR m.has_attachments=1)
                    AND ($thread IS NULL OR EXISTS(SELECT 1 FROM message_threads t WHERE t.mailbox_id=m.mailbox_id AND t.provider_id=m.provider_id AND t.thread_id=$thread))
                    AND (json_array_length($terms)=0 OR EXISTS(SELECT 1 FROM json_each($terms) term
                        WHERE instr(lower(m.subject || ' ' || m.from_name || ' ' || m.from_address || ' ' || m.recipients_json || ' ' ||
                            m.cc_recipients_json || ' ' || m.preview || ' ' || coalesce(decode_body(m.body_blob),m.body,'')),lower(term.value))>0))
                ORDER BY m.rowid LIMIT $limit;
                """;
            AddEvidencePageParameters(command, mailboxIds, continuation?.After ?? 0, upper, limit);
            command.Parameters.AddWithValue("$terms", JsonSerializer.Serialize(terms));
            command.Parameters.AddWithValue("$attachments", attachmentsOnly);
            command.Parameters.AddWithValue("$thread", (object?)threadId ?? DBNull.Value);
            var results = new List<MailMessage>();
            long last = 0;
            string? next = null;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (results.Count == limit) { next = new EvidenceCursor(scope, last, upper).Encode(); break; }
                last = reader.GetInt64(22);
                results.Add(ReadMessage(reader));
            }
            return (results, next);
        }, cancellationToken);
        return new(items, next, await GetEvidenceCompletenessAsync(mailboxIds, cancellationToken));
    }

    public Task<EvidenceCompleteness> GetEvidenceCompletenessAsync(string[] mailboxIds, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            ValidateEvidenceScope(mailboxIds, 1);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT count(*) FROM messages WHERE mailbox_id IN (SELECT value FROM json_each($mailboxes))),
                    (SELECT count(*) FROM messages WHERE has_attachments=1 AND mailbox_id IN (SELECT value FROM json_each($mailboxes))),
                    (SELECT count(*) FROM messages m JOIN evidence_messages e ON e.mailbox_id=m.mailbox_id AND e.provider_id=m.provider_id
                        WHERE e.attachment_state='Complete' AND m.mailbox_id IN (SELECT value FROM json_each($mailboxes))),
                    (SELECT count(*) FROM evidence_documents WHERE mailbox_id IN (SELECT value FROM json_each($mailboxes))),
                    (SELECT count(*) FROM evidence_documents WHERE length(text)>0 AND mailbox_id IN (SELECT value FROM json_each($mailboxes))),
                    (SELECT count(*) FROM evidence_documents WHERE state<>'Complete' AND mailbox_id IN (SELECT value FROM json_each($mailboxes)));
                """;
            command.Parameters.AddWithValue("$mailboxes", JsonSerializer.Serialize(mailboxIds));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new EvidenceCompleteness("Cached mail and captured evidence", false, reader.GetInt64(0), reader.GetInt64(1),
                reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        }, cancellationToken);

    public Task ReviewEvidenceDocumentAsync(EvidenceDocument document, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE evidence_documents SET kind=$kind,verification=$verification,payload_json=$payload WHERE id=$id;
                INSERT INTO evidence_reviews(document_id,payload_json) VALUES($id,$payload);
                """;
            command.Parameters.AddWithValue("$id", document.Id);
            command.Parameters.AddWithValue("$kind", (int)document.Kind);
            command.Parameters.AddWithValue("$verification", (int)document.Verification);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(document));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }, cancellationToken);

    public Task<EvidencePage<EvidenceDuplicate>> FindEvidenceDuplicatesAsync(string[] mailboxIds, string? cursor = null,
        int limit = 50, CancellationToken cancellationToken = default) => WithLockAsync(async connection =>
        {
            ValidateEvidenceScope(mailboxIds, limit);
            var scope = EvidenceCursor.ScopeFor(new { mailboxIds = mailboxIds.Order(), duplicates = true });
            var continuation = EvidenceCursor.Parse(cursor, scope);
            // The snapshot is bounded by captured rowids; no source records are merged or deleted.
            var upper = continuation?.Upper ?? Math.Max(await EvidenceUpperAsync(connection, "evidence_documents", cancellationToken),
                await EvidenceUpperAsync(connection, "evidence_messages", cancellationToken));
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH records AS (
                    SELECT 'SHA-256 attachment bytes' basis,sha256 fingerprint,id FROM evidence_documents
                    WHERE sha256 IS NOT NULL AND rowid<=$upper AND mailbox_id IN (SELECT value FROM json_each($mailboxes))
                    UNION ALL SELECT 'Internet Message-ID (review content differences)',internet_message_id,id FROM evidence_messages
                    WHERE internet_message_id IS NOT NULL AND internet_message_id<>'' AND rowid<=$upper AND mailbox_id IN (SELECT value FROM json_each($mailboxes))
                    UNION ALL SELECT 'SHA-256 MIME bytes',mime_sha256,id FROM evidence_messages
                    WHERE mime_sha256 IS NOT NULL AND rowid<=$upper AND mailbox_id IN (SELECT value FROM json_each($mailboxes))
                ), groups AS (SELECT basis,fingerprint,count(*) occurrences,json_group_array(id) ids FROM records GROUP BY basis,fingerprint HAVING count(*)>1)
                SELECT basis,fingerprint,occurrences,ids FROM groups ORDER BY basis,fingerprint LIMIT $limit OFFSET $after;
                """;
            AddEvidencePageParameters(command, mailboxIds, continuation?.After ?? 0, upper, limit);
            var items = new List<EvidenceDuplicate>();
            string? next = null;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (items.Count == limit) { next = new EvidenceCursor(scope, (continuation?.After ?? 0) + limit, upper).Encode(); break; }
                var ids = JsonSerializer.Deserialize<string[]>(reader.GetString(3))!;
                items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(0).StartsWith("SHA-256", StringComparison.Ordinal),
                    ids.Take(200).ToArray(), reader.GetInt64(2), ids.Length > 200));
            }
            return new EvidencePage<EvidenceDuplicate>(items, next,
                new("Captured messages and downloaded attachments only", false, 0, 0, 0, 0, 0, 0,
                    "Matching hashes identify identical bytes. Matching Message-IDs are candidates for review. Forwarded wrappers can differ; their identical attachments are grouped. No evidence is merged or deleted."));
        }, cancellationToken);

    public Task SaveEvidenceExportAsync(EvidenceExport export, byte[] content, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM evidence_exports WHERE expires_at<$now;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = """
                INSERT INTO evidence_exports(id,payload_json,expires_at,content)
                SELECT $id,$payload,$expires,$content
                WHERE NOT EXISTS(SELECT 1 FROM json_each($payload,'$.MailboxIds') scope
                    WHERE NOT EXISTS(SELECT 1 FROM mailboxes m WHERE m.account_id || ':' || lower(m.address)=scope.value));
                """;
            command.Parameters.AddWithValue("$id", export.Id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(export));
            command.Parameters.AddWithValue("$expires", export.ExpiresAt.ToString("O"));
            command.Parameters.AddWithValue("$content", content);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw EvidenceService.Unavailable();
        }, cancellationToken);

    public Task<(EvidenceExport Export, byte[] Content)?> GetEvidenceExportAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync<(EvidenceExport, byte[])?>(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json,content FROM evidence_exports WHERE id=$id AND expires_at>$now;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? (JsonSerializer.Deserialize<EvidenceExport>(reader.GetString(0))!, (byte[])reader[1]) : null;
        }, cancellationToken);

    private static void ValidateEvidenceScope(string[] mailboxIds, int limit)
    {
        if (mailboxIds is null || mailboxIds.Length is < 1 or > 100 || mailboxIds.Any(string.IsNullOrWhiteSpace))
            throw new EvidenceException("invalid_mailboxes", "Choose 1–100 mailbox IDs.");
        if (limit is < 1 or > 200) throw new EvidenceException("invalid_page_size", "Page size must be between 1 and 200.");
    }

    private static async Task<long> EvidenceUpperAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT coalesce(max(rowid),0) FROM {table};";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static void AddEvidencePageParameters(SqliteCommand command, string[] mailboxes, long after, long upper, int limit)
    {
        command.Parameters.AddWithValue("$mailboxes", JsonSerializer.Serialize(mailboxes));
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$upper", upper);
        command.Parameters.AddWithValue("$limit", limit + 1);
    }
}
