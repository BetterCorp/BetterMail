using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed record McpConfiguration(
    bool Enabled = false,
    int Port = 47831,
    bool AllowWrites = false,
    bool AllowSending = false,
    string[]? MailboxIds = null);

public sealed partial class EncryptedMailStore
{
    public Task<MailAction?> GetMailActionAsync(string id, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload_json FROM mail_actions WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
                ? JsonSerializer.Deserialize<MailAction>(json) : null;
        }, cancellationToken);

    private static async Task EnsureMcpSettingsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS mcp_settings (
                id INTEGER PRIMARY KEY CHECK(id = 1), configuration_json TEXT NOT NULL, access_key TEXT NOT NULL);
            """, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO mcp_settings VALUES(1, $settings, $key);";
        command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(new McpConfiguration()));
        command.Parameters.AddWithValue("$key", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<(McpConfiguration Configuration, string AccessKey)> GetMcpConfigurationAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await EnsureMcpSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT configuration_json, access_key FROM mcp_settings WHERE id = 1;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<McpConfiguration>(reader.GetString(0)) ?? new(), reader.GetString(1));
        }, cancellationToken);

    public Task SaveMcpConfigurationAsync(McpConfiguration configuration, CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            if (configuration.Port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(configuration), "Choose a port from 1024 to 65535.");
            await EnsureMcpSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mcp_settings SET configuration_json = $settings WHERE id = 1;";
            command.Parameters.AddWithValue("$settings", JsonSerializer.Serialize(configuration));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<string> RotateMcpAccessKeyAsync(CancellationToken cancellationToken = default) =>
        WithLockAsync(async connection =>
        {
            await EnsureMcpSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
            var accessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mcp_settings SET access_key = $key WHERE id = 1;";
            command.Parameters.AddWithValue("$key", accessKey);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return accessKey;
        }, cancellationToken);
}
