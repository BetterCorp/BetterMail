using System.Security.Cryptography;
using BetterMail.Core;
using Microsoft.Data.Sqlite;

namespace BetterMail.Tests;

public sealed class DatabaseKeyProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bettermail-key-" + Guid.NewGuid().ToString("N"));

    public DatabaseKeyProviderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FreshInstallCreatesAndReusesRandomKey()
    {
        var storage = new MemoryStorage();
        var first = DatabaseKeyProvider.GetOrCreate(_directory, storage);
        Assert.Equal(32, Convert.FromHexString(first).Length);
        Assert.Equal(first, DatabaseKeyProvider.GetOrCreate(_directory, storage));
        Assert.Equal(1, storage.Writes);
        Assert.Empty(Directory.GetFiles(_directory));
        Assert.NotEqual(first, DatabaseKeyProvider.GetOrCreate(_directory, new MemoryStorage()));
    }

    [Theory]
    [InlineData("mail.db")]
    [InlineData("database.key")]
    public void MissingKeyNeverReplacesExistingData(string file)
    {
        File.WriteAllText(Path.Combine(_directory, file), "existing");
        var storage = new MemoryStorage();
        Assert.Throws<DatabaseKeyRecoveryException>(() => DatabaseKeyProvider.GetOrCreate(_directory, storage));
        Assert.Equal(0, storage.Writes);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(_directory, file)));
    }

    [Fact]
    public void CorruptSavedKeyIsNotReplaced()
    {
        var storage = new MemoryStorage { Value = [1, 2, 3] };
        Assert.Throws<InvalidOperationException>(() => DatabaseKeyProvider.GetOrCreate(_directory, storage));
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public void UnavailableKeyringDoesNotCreateKeyOrDatabase()
    {
        var storage = new MemoryStorage { ReadError = new InvalidOperationException("Locked") };
        Assert.Throws<InvalidOperationException>(() => DatabaseKeyProvider.GetOrCreate(_directory, storage));
        Assert.Equal(0, storage.Writes);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void FailedPersistenceDoesNotReturnAnUnrecoverableKey()
    {
        var storage = new MemoryStorage { IgnoreWrites = true };
        Assert.Throws<InvalidOperationException>(() => DatabaseKeyProvider.GetOrCreate(_directory, storage));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ExistingPasswordCanBeRememberedWithoutReencryptingDatabase()
    {
        CreateLegacyDatabase("previous password");
        var before = File.ReadAllBytes(Path.Combine(_directory, "mail.db"));
        var storage = new MemoryStorage();
        var key = DatabaseKeyProvider.Recover(_directory, "previous password", storage);
        Assert.Equal(DatabaseKeyProvider.DeriveLegacyKey("previous password"), key);
        Assert.Equal(key, DatabaseKeyProvider.GetOrCreate(_directory, storage));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_directory, "mail.db")));
        Assert.Equal(1, storage.Writes);
    }

    [Fact]
    public void WrongPasswordDoesNotOverwriteKeyOrDatabase()
    {
        CreateLegacyDatabase("correct password");
        var before = File.ReadAllBytes(Path.Combine(_directory, "mail.db"));
        var storage = new MemoryStorage();
        Assert.Throws<InvalidOperationException>(() => DatabaseKeyProvider.Recover(_directory, "wrong password", storage));
        Assert.Equal(0, storage.Writes);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_directory, "mail.db")));
    }

    [Fact]
    public void RecoveryDoesNotCreateAMissingDatabase()
    {
        var storage = new MemoryStorage();
        Assert.Throws<InvalidOperationException>(() => DatabaseKeyProvider.Recover(_directory, "password", storage));
        Assert.Equal(0, storage.Writes);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void RecoveryNeverOverwritesADifferentSavedKey()
    {
        CreateLegacyDatabase("password");
        var storage = new MemoryStorage { Value = RandomNumberGenerator.GetBytes(32) };
        var original = storage.Value.ToArray();
        Assert.Throws<InvalidOperationException>(() => DatabaseKeyProvider.Recover(_directory, "password", storage));
        Assert.Equal(original, storage.Value);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public void WindowsReadsExistingDpapiKey()
    {
        if (!OperatingSystem.IsWindows()) return;
        var original = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(Path.Combine(_directory, "database.key"),
            ProtectedData.Protect(original, null, DataProtectionScope.CurrentUser));
        var storage = new SystemDatabaseKeyStorage(_directory);
        Assert.Equal(Convert.ToHexString(original), DatabaseKeyProvider.GetOrCreate(_directory, storage));
    }

    private void CreateLegacyDatabase(string password)
    {
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, "mail.db"), Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA key = '{DatabaseKeyProvider.DeriveLegacyKey(password)}'; CREATE TABLE saved_mail (body TEXT); INSERT INTO saved_mail VALUES ('Keep this mail');";
        command.ExecuteNonQuery();
    }

    public void Dispose() => Directory.Delete(_directory, true);

    private sealed class MemoryStorage : IDatabaseKeyStorage
    {
        public byte[] Value { get; set; } = [];
        public int Writes { get; private set; }
        public bool IgnoreWrites { get; init; }
        public Exception? ReadError { get; init; }
        public byte[] Read() => ReadError is { } error ? throw error : Value.ToArray();
        public void Write(byte[] key)
        {
            Writes++;
            if (!IgnoreWrites) Value = key.ToArray();
        }
    }
}
