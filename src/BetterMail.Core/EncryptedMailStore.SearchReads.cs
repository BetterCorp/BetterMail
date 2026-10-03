using Microsoft.Data.Sqlite;

namespace BetterMail.Core;

public sealed partial class EncryptedMailStore
{
    private readonly SemaphoreSlim _searchReadGate = new(1, 1);
    private SqliteConnection? _searchReadConnection;
    private bool _searchReaderDisposed;

    // Searches must neither wait for sync nor block writes or other interactive readers.
    private async Task<T> WithSearchReadAsync<T>(Func<SqliteConnection, bool, Task<T>> action, CancellationToken token)
    {
        await _searchReadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                ObjectDisposedException.ThrowIf(_searchReaderDisposed, this);
                _ = GetConnection();
                if (_searchReadConnection is null)
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
                        _searchReadConnection = connection;
                    }
                    catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
                }
                var reader = _searchReadConnection;
                // SqliteCommand.Cancel is a no-op. Interrupt this dedicated connection instead.
                // Dispose the registration before releasing the gate so cancellation cannot hit a later search.
                using var cancellation = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(reader.Handle));
                try
                {
                    token.ThrowIfCancellationRequested();
                    using var transaction = reader.BeginTransaction(deferred: true);
                    // Reading the marker pins a WAL snapshot: index selection and the query see
                    // the same schema even if maintenance drops the legacy index concurrently.
                    var optimized = await MigrationCompleteAsync(reader, "message-search-v2", token).ConfigureAwait(false);
                    var result = await action(reader, optimized).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                    return result;
                }
                catch (SqliteException) when (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }
            }, token).ConfigureAwait(false);
        }
        finally { _searchReadGate.Release(); }
    }

    private async Task DisposeSearchReaderAsync()
    {
        await _searchReadGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _searchReaderDisposed = true;
            if (_searchReadConnection is not null)
            {
                await _searchReadConnection.DisposeAsync().ConfigureAwait(false);
                _searchReadConnection = null;
            }
        }
        finally { _searchReadGate.Release(); }
    }
}
