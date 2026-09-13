using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    private readonly SemaphoreSlim _messageReadGate = new(1, 1);
    private SqliteConnection? _messageReadConnection;
    private bool _messageReaderDisposed;

    // Dedicated WAL snapshots let message bodies load while sync or folder navigation is busy.
    // Private cache avoids shared-cache table locks; reuse the encrypted connection to avoid
    // repeating SQLCipher key derivation on every message click.
    private async Task<T> WithMessageReadAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken token)
    {
        await _messageReadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                ObjectDisposedException.ThrowIf(_messageReaderDisposed, this);
                _ = GetConnection(); // Require initialization, including schema migrations.
                if (_messageReadConnection is null)
                {
                    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Private, Pooling = false, Password = ValidateHexKey(key)
                    }.ToString());
                    try
                    {
                        await connection.OpenAsync(token).ConfigureAwait(false);
                        await ExecuteAsync(connection, "PRAGMA query_only = ON; PRAGMA busy_timeout = 1000;", token).ConfigureAwait(false);
                        _messageReadConnection = connection;
                    }
                    catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
                }
                try { return await action(_messageReadConnection).ConfigureAwait(false); }
                catch (SqliteException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
            }, token).ConfigureAwait(false);
        }
        finally { _messageReadGate.Release(); }
    }

    private async Task DisposeMessageReaderAsync()
    {
        await _messageReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _messageReaderDisposed = true;
            if (_messageReadConnection is not null)
            {
                await _messageReadConnection.DisposeAsync().ConfigureAwait(false);
                _messageReadConnection = null;
            }
        }
        finally { _messageReadGate.Release(); }
    }
}
