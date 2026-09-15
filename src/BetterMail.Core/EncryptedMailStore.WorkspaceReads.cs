using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    private readonly SemaphoreSlim _workspaceReadGate = new(1, 1);
    private SqliteConnection? _workspaceReadConnection;
    private bool _workspaceReaderDisposed;

    // Keep workspace queries and aggregate counts off both the writer and folder-navigation gates.
    // Private cache avoids shared-cache table locks; reuse the encrypted connection to avoid
    // repeating SQLCipher key derivation on every workspace query.
    private async Task<T> WithWorkspaceReadAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken token)
    {
        await _workspaceReadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                ObjectDisposedException.ThrowIf(_workspaceReaderDisposed, this);
                _ = GetConnection(); // Require initialization, including schema migrations.
                if (_workspaceReadConnection is null)
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
                        _workspaceReadConnection = connection;
                    }
                    catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
                }
                try { return await action(_workspaceReadConnection).ConfigureAwait(false); }
                catch (SqliteException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
            }, token).ConfigureAwait(false);
        }
        finally { _workspaceReadGate.Release(); }
    }

    private async Task DisposeWorkspaceReaderAsync()
    {
        await _workspaceReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _workspaceReaderDisposed = true;
            if (_workspaceReadConnection is not null)
            {
                await _workspaceReadConnection.DisposeAsync().ConfigureAwait(false);
                _workspaceReadConnection = null;
            }
        }
        finally { _workspaceReadGate.Release(); }
    }
}
