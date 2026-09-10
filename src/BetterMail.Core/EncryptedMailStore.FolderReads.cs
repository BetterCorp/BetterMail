using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    private readonly SemaphoreSlim _folderReadGate = new(1, 1);
    private SqliteConnection? _folderReadConnection;
    private bool _folderReaderDisposed;

    // WAL snapshots let navigation read committed messages while sync holds the writer gate.
    // Private cache avoids shared-cache table locks; reuse the encrypted connection to avoid
    // repeating SQLCipher key derivation on every folder click.
    private async Task<T> WithFolderReadAsync<T>(Func<SqliteConnection, Task<T>> action, CancellationToken token)
    {
        await _folderReadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                ObjectDisposedException.ThrowIf(_folderReaderDisposed, this);
                _ = GetConnection(); // Require initialization, including schema migrations.
                if (_folderReadConnection is null)
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
                        _folderReadConnection = connection;
                    }
                    catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
                }
                try { return await action(_folderReadConnection).ConfigureAwait(false); }
                catch (SqliteException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
            }, token).ConfigureAwait(false);
        }
        finally { _folderReadGate.Release(); }
    }

    private async Task DisposeFolderReaderAsync()
    {
        await _folderReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _folderReaderDisposed = true;
            if (_folderReadConnection is not null)
            {
                await _folderReadConnection.DisposeAsync().ConfigureAwait(false);
                _folderReadConnection = null;
            }
        }
        finally { _folderReadGate.Release(); }
    }
}
