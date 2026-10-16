using System.Text.Json;

namespace BetterMail.Core;

public sealed record MailWebhookSubscription(string Id, DateTimeOffset ExpiresAt);

public interface IMailWebhookProvider
{
    bool SupportsMailWebhooks(MailAccount account);
    Task<MailWebhookSubscription> CreateMailWebhookAsync(MailAccount account, string notificationUrl,
        string clientState, CancellationToken cancellationToken = default);
    Task<MailWebhookSubscription> RenewMailWebhookAsync(MailAccount account, string subscriptionId,
        CancellationToken cancellationToken = default);
    Task DeleteMailWebhookAsync(MailAccount account, string subscriptionId, CancellationToken cancellationToken = default);
}

public sealed record MailWebhookConfiguration(bool Enabled = false, string ClientState = "",
    string SubscriptionId = "", DateTimeOffset? ExpiresAt = null, string NotificationUrl = "");

public sealed partial class EncryptedMailStore
{
    private Task EnsureMailWebhooksAsync(Microsoft.Data.Sqlite.SqliteConnection connection, CancellationToken ct) =>
        ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS mail_webhooks (
                provider_id TEXT NOT NULL, account_id TEXT NOT NULL, configuration_json TEXT NOT NULL,
                pending_version INTEGER NOT NULL DEFAULT 0, completed_version INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(provider_id, account_id));
            """, ct);

    public Task<MailWebhookConfiguration> GetMailWebhookConfigurationAsync(string providerId, string accountId,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        await EnsureMailWebhooksAsync(connection, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT configuration_json FROM mail_webhooks WHERE provider_id = $provider AND account_id = $account;";
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$account", accountId);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<MailWebhookConfiguration>(json) ?? new() : new();
    }, ct);

    public Task SaveMailWebhookConfigurationAsync(string providerId, string accountId, MailWebhookConfiguration configuration,
        CancellationToken ct = default) => WithLockAsync(async connection =>
    {
        await EnsureMailWebhooksAsync(connection, ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mail_webhooks(provider_id, account_id, configuration_json) VALUES($provider, $account, $json)
            ON CONFLICT(provider_id, account_id) DO UPDATE SET configuration_json = excluded.configuration_json;
            """;
        command.Parameters.AddWithValue("$provider", providerId);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(configuration));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }, ct);

    // Versioned dirty markers prevent an event received during a sync from being cleared by that sync.
    public Task<long> GetPendingMailWebhookVersionAsync(string providerId, string accountId, CancellationToken ct = default) =>
        WithLockAsync(async connection =>
        {
            await EnsureMailWebhooksAsync(connection, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pending_version FROM mail_webhooks WHERE provider_id = $provider AND account_id = $account AND pending_version > completed_version;";
            command.Parameters.AddWithValue("$provider", providerId);
            command.Parameters.AddWithValue("$account", accountId);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is long version ? version : 0L;
        }, ct);

    public Task MarkMailWebhookPendingAsync(string providerId, string accountId, CancellationToken ct = default) =>
        UpdateMailWebhookVersionAsync(providerId, accountId, null, ct);

    public Task CompleteMailWebhookVersionAsync(string providerId, string accountId, long version, CancellationToken ct = default) =>
        UpdateMailWebhookVersionAsync(providerId, accountId, version, ct);

    private Task UpdateMailWebhookVersionAsync(string providerId, string accountId, long? completed, CancellationToken ct) =>
        WithLockAsync(async connection =>
        {
            await EnsureMailWebhooksAsync(connection, ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = completed is null
                ? "UPDATE mail_webhooks SET pending_version = pending_version + 1 WHERE provider_id = $provider AND account_id = $account;"
                : "UPDATE mail_webhooks SET completed_version = MAX(completed_version, MIN(pending_version, $version)) WHERE provider_id = $provider AND account_id = $account;";
            command.Parameters.AddWithValue("$provider", providerId);
            command.Parameters.AddWithValue("$account", accountId);
            if (completed is not null) command.Parameters.AddWithValue("$version", completed.Value);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }, ct);
}
