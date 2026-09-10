using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed record McpAttachmentUpload(string Id, string MailboxId, string DraftId, DateTimeOffset ExpectedUpdatedAt,
    string Name, string ContentType, long Size, string Sha256);

public sealed partial class EncryptedMailStore
{
    public const int McpUploadChunkBytes = 262144;

    public Task<bool> TryUpdateMcpDraftAsync(LocalDraft draft, DateTimeOffset expectedUpdatedAt, CancellationToken cancellationToken = default, string? completedUploadId = null) =>
        WithLockAsync(async connection =>
        {
            if (completedUploadId is not null) await EnsureMcpUploadsAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE local_drafts SET recipients=$to, cc=$cc, bcc=$bcc, subject=$subject, body=$body,
                    is_html=$html, attachments_json=$attachments, updated_at=$updated, sync_status=NULL, sync_error=NULL
                WHERE id=$id AND mailbox_id=$mailbox AND account_id=$account AND updated_at=$expected
                    AND is_queued=0 AND send_accepted=0
                    AND NOT EXISTS(SELECT 1 FROM mail_actions WHERE item_id=$id AND kind IN (1,2));
                """;
            command.Parameters.AddWithValue("$id", draft.Id);
            command.Parameters.AddWithValue("$mailbox", draft.MailboxId);
            command.Parameters.AddWithValue("$account", draft.AccountId);
            command.Parameters.AddWithValue("$expected", expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$updated", draft.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$to", draft.To);
            command.Parameters.AddWithValue("$cc", draft.Cc);
            command.Parameters.AddWithValue("$bcc", draft.Bcc);
            command.Parameters.AddWithValue("$subject", draft.Subject);
            command.Parameters.AddWithValue("$body", draft.Body);
            command.Parameters.AddWithValue("$html", draft.IsHtml);
            command.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(draft.Attachments));
            if (completedUploadId is not null)
            {
                command.CommandText = command.CommandText.TrimEnd().TrimEnd(';') + " AND EXISTS(SELECT 1 FROM mcp_attachment_uploads WHERE id=$upload AND mailbox_id=$mailbox AND draft_id=$id AND remote_state='shared');";
                command.Parameters.AddWithValue("$upload", completedUploadId);
            }
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
            if (completedUploadId is not null)
            {
                command.CommandText = "UPDATE mcp_attachment_uploads SET remote_state='complete',completed_at=$updated WHERE id=$upload; DELETE FROM mcp_attachment_chunks WHERE upload_id=$upload;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private static async Task EnsureMcpUploadsAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS mcp_attachment_uploads (
                id TEXT PRIMARY KEY, mailbox_id TEXT NOT NULL, draft_id TEXT NOT NULL,
                metadata TEXT NOT NULL, total_size INTEGER NOT NULL, expires_at TEXT NOT NULL, completed_at TEXT);
            CREATE TABLE IF NOT EXISTS mcp_attachment_chunks (
                upload_id TEXT NOT NULL, offset INTEGER NOT NULL, content BLOB NOT NULL,
                PRIMARY KEY(upload_id,offset));
            DELETE FROM mcp_attachment_chunks WHERE upload_id IN
                (SELECT id FROM mcp_attachment_uploads WHERE expires_at <= $now);
            DELETE FROM mcp_attachment_uploads WHERE expires_at <= $now;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "mcp_attachment_uploads", "remote_state", "TEXT NOT NULL DEFAULT 'ready'", token).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "mcp_attachment_uploads", "remote_json", "TEXT", token).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "mcp_attachment_uploads", "link_json", "TEXT", token).ConfigureAwait(false);
    }

    public Task<McpAttachmentUpload> BeginMcpAttachmentUploadAsync(string mailboxId, string draftId, DateTimeOffset expectedUpdatedAt,
        string name, string contentType, long size, string sha256, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            if (size < 0 || size > DraftAttachment.MaximumSizeBytes || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(name) || name.Length > 255 || string.IsNullOrWhiteSpace(contentType) || contentType.Length > 255)
                throw new InvalidOperationException("Invalid attachment metadata, size, or SHA-256.");
            await EnsureMcpUploadsAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var quota = connection.CreateCommand();
            quota.CommandText = "SELECT COUNT(*) FROM mcp_attachment_uploads WHERE completed_at IS NULL;";
            if (Convert.ToInt64(await quota.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) >= 4)
                throw new InvalidOperationException("At most four attachment uploads may be active. Complete or cancel an upload first.");
            var upload = new McpAttachmentUpload(Guid.NewGuid().ToString("N"), mailboxId, draftId, expectedUpdatedAt, name, contentType, size, sha256.ToUpperInvariant());
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO mcp_attachment_uploads(id,mailbox_id,draft_id,metadata,total_size,expires_at,completed_at) VALUES($id,$mailbox,$draft,$metadata,$size,$expires,NULL);";
            command.Parameters.AddWithValue("$id", upload.Id);
            command.Parameters.AddWithValue("$mailbox", mailboxId);
            command.Parameters.AddWithValue("$draft", draftId);
            command.Parameters.AddWithValue("$metadata", JsonSerializer.Serialize(upload));
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddHours(1).ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return upload;
        }, cancellationToken);

    private static async Task<(McpAttachmentUpload Upload, string? Completed)> McpUploadAsync(SqliteConnection connection, string mailboxId, string uploadId, CancellationToken token)
    {
        await EnsureMcpUploadsAsync(connection, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT metadata,completed_at FROM mcp_attachment_uploads WHERE id=$id AND mailbox_id=$mailbox;";
        command.Parameters.AddWithValue("$id", uploadId);
        command.Parameters.AddWithValue("$mailbox", mailboxId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidOperationException("Upload unavailable or expired.");
        return (JsonSerializer.Deserialize<McpAttachmentUpload>(reader.GetString(0))!, reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public Task<long> WriteMcpAttachmentChunkAsync(string mailboxId, string uploadId, long offset, byte[] bytes, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var (upload, completed) = await McpUploadAsync(connection, mailboxId, uploadId, cancellationToken).ConfigureAwait(false);
            if (completed is not null || offset < 0 || bytes.Length is < 1 or > McpUploadChunkBytes || offset > upload.Size - bytes.Length)
                throw new InvalidOperationException("Invalid upload chunk or upload already complete.");
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT COALESCE(SUM(length(content)),0) FROM mcp_attachment_chunks WHERE upload_id=$id;";
            query.Parameters.AddWithValue("$id", uploadId);
            var received = Convert.ToInt64(await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            if (offset < received)
            {
                query.CommandText = "SELECT content FROM mcp_attachment_chunks WHERE upload_id=$id AND offset=$offset;";
                query.Parameters.AddWithValue("$offset", offset);
                var existing = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[];
                if (existing is null || !existing.AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Chunk retry differs from the saved bytes.");
                return received;
            }
            if (offset != received) throw new InvalidOperationException($"Send the next chunk at offset {received}.");
            query.CommandText = "INSERT INTO mcp_attachment_chunks VALUES($id,$offset,$bytes);";
            query.Parameters.AddWithValue("$offset", offset);
            query.Parameters.AddWithValue("$bytes", bytes);
            await query.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return received + bytes.Length;
        }, cancellationToken);

    public Task<DateTimeOffset> CompleteMcpAttachmentUploadAsync(string mailboxId, string uploadId, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var (upload, completed) = await McpUploadAsync(connection, mailboxId, uploadId, cancellationToken).ConfigureAwait(false);
            if (completed is not null) return DateTimeOffset.Parse(completed, CultureInfo.InvariantCulture);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT attachments_json FROM local_drafts WHERE id=$draft AND mailbox_id=$mailbox
                    AND updated_at=$expected AND is_queued=0 AND send_accepted=0
                    AND NOT EXISTS(SELECT 1 FROM mail_actions WHERE item_id=$draft AND kind IN (1,2));
                """;
            command.Parameters.AddWithValue("$draft", upload.DraftId);
            command.Parameters.AddWithValue("$mailbox", mailboxId);
            command.Parameters.AddWithValue("$expected", upload.ExpectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            var json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
                ?? throw new InvalidOperationException("Draft changed or is unavailable. Read the draft and start a new upload.");
            var attachments = JsonSerializer.Deserialize<List<DraftAttachment>>(json) ?? [];
            command.CommandText = "SELECT content FROM mcp_attachment_chunks WHERE upload_id=$id ORDER BY offset;";
            command.Parameters.AddWithValue("$id", uploadId);
            using var content = new MemoryStream();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    await content.WriteAsync((byte[])reader[0], cancellationToken).ConfigureAwait(false);
            var bytes = content.ToArray();
            if (bytes.LongLength != upload.Size || Convert.ToHexString(SHA256.HashData(bytes)) != upload.Sha256)
                throw new InvalidOperationException("Upload is incomplete or its SHA-256 does not match.");
            attachments.Add(new(upload.Name, upload.ContentType, bytes));
            var updated = DateTimeOffset.UtcNow;
            if (updated <= upload.ExpectedUpdatedAt) updated = upload.ExpectedUpdatedAt.AddTicks(1);
            command.CommandText = """
                UPDATE local_drafts SET attachments_json=$attachments,updated_at=$updated,sync_status=NULL,sync_error=NULL WHERE id=$draft;
                UPDATE mcp_attachment_uploads SET completed_at=$updated,remote_state='complete' WHERE id=$id;
                DELETE FROM mcp_attachment_chunks WHERE upload_id=$id;
                """;
            command.Parameters.AddWithValue("$attachments", JsonSerializer.Serialize(attachments));
            command.Parameters.AddWithValue("$updated", updated.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return updated;
        }, cancellationToken);

    public Task CancelMcpAttachmentUploadAsync(string mailboxId, string uploadId, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await EnsureMcpUploadsAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM mcp_attachment_chunks WHERE upload_id IN (SELECT id FROM mcp_attachment_uploads WHERE id=$id AND mailbox_id=$mailbox);
                DELETE FROM mcp_attachment_uploads WHERE id=$id AND mailbox_id=$mailbox;
                """;
            command.Parameters.AddWithValue("$id", uploadId);
            command.Parameters.AddWithValue("$mailbox", mailboxId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
}

public sealed record McpUploadStatus(McpAttachmentUpload Upload, string? CompletedAt, string State, CloudDriveItem? File, DriveShareLink? Link);

public sealed partial class EncryptedMailStore
{
    public Task<McpUploadStatus> GetMcpUploadStatusAsync(string owner, string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var (upload, completed) = await McpUploadAsync(connection, owner, id, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT remote_state,remote_json,link_json FROM mcp_attachment_uploads WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new McpUploadStatus(upload, completed, reader.GetString(0),
                reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<CloudDriveItem>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<DriveShareLink>(reader.GetString(2)));
        }, cancellationToken);

    public Task<bool> SetMcpUploadStateAsync(string owner, string id, string expectedState, string state,
        CloudDriveItem? file = null, DriveShareLink? link = null, DateTimeOffset? completedAt = null, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await EnsureMcpUploadsAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE mcp_attachment_uploads SET remote_state=$state, remote_json=COALESCE($file,remote_json),link_json=COALESCE($link,link_json),
                    completed_at=CASE WHEN $state='complete' THEN $completed ELSE completed_at END
                    WHERE id=$id AND mailbox_id=$owner AND remote_state=$expected;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$expected", expectedState);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$completed", (completedAt ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$file", file is null ? DBNull.Value : JsonSerializer.Serialize(file));
            command.Parameters.AddWithValue("$link", link is null ? DBNull.Value : JsonSerializer.Serialize(link));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) return false;
            if (state == "complete")
            {
                command.CommandText = "DELETE FROM mcp_attachment_chunks WHERE upload_id=$id;";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<byte[]> ReadMcpUploadBytesAsync(string owner, string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            var (upload, _) = await McpUploadAsync(connection, owner, id, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT content FROM mcp_attachment_chunks WHERE upload_id=$id ORDER BY offset;";
            command.Parameters.AddWithValue("$id", id);
            using var content = new MemoryStream();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    await content.WriteAsync((byte[])reader[0], cancellationToken).ConfigureAwait(false);
            var bytes = content.ToArray();
            if (bytes.LongLength != upload.Size || Convert.ToHexString(SHA256.HashData(bytes)) != upload.Sha256)
                throw new InvalidOperationException("Upload is incomplete or its SHA-256 does not match.");
            return bytes;
        }, cancellationToken);
}
