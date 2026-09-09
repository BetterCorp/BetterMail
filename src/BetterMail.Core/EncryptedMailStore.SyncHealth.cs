using System.Text.Json;

namespace BetterMail.Core;

public sealed record MailboxSyncHealth(string AccountId, string MailboxId, string Address, DateTimeOffset? LastSuccess, string? Error)
{
    public string Summary => LastSuccess is { } time
        ? $"Last complete mail sync: {time.ToLocalTime():g}"
        : "No complete mail sync yet";
    public string State => Error is null ? "Cached mail available" : "Sync needs attention — cached mail remains available";
    public string Recovery => Error is null ? "" : "Use F9 to retry. Automatic retries run every minute. For sign-in errors, choose Re-authenticate on the account below.";
}

public sealed partial class EncryptedMailStore
{
    public Task<IReadOnlyList<MailboxSyncHealth>> GetSyncHealthAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync<IReadOnlyList<MailboxSyncHealth>>(async connection =>
        {
            await ExecuteAsync(connection, SyncHealthSchema, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM mailbox_sync_health;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<MailboxSyncHealth>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                results.Add(JsonSerializer.Deserialize<MailboxSyncHealth>(reader.GetString(0))!);
            return results;
        }, cancellationToken);

    public Task SaveSyncHealthAsync(MailboxSyncHealth health, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await ExecuteAsync(connection, SyncHealthSchema, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO mailbox_sync_health(mailbox_id, account_id, payload_json) VALUES ($id, $account, $json);";
            command.Parameters.AddWithValue("$id", health.MailboxId);
            command.Parameters.AddWithValue("$account", health.AccountId);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(health));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private const string SyncHealthSchema = """
        CREATE TABLE IF NOT EXISTS mailbox_sync_health (
            mailbox_id TEXT PRIMARY KEY, account_id TEXT NOT NULL,
            payload_json TEXT NOT NULL);
        """;
}
