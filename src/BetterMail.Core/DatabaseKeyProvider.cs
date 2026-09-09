using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Identity.Client.Extensions.Msal;

namespace BetterMail.Core;

public static class DatabaseKeyProvider
{
    private const string EnvironmentVariable = "BETTERMAIL_DATABASE_KEY";
    private static readonly object Gate = new();

    public static string GetOrCreate(string dataDirectory)
    {
        // Preserve the exact derivation used by existing installations and custom builds.
        var configuredKey = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredKey))
            return DeriveLegacyKey(configuredKey);

        return WithStorage(dataDirectory, storage => GetOrCreate(dataDirectory, storage));
    }

    public static string Recover(string dataDirectory, string passphrase) =>
        WithStorage(dataDirectory, storage => Recover(dataDirectory, passphrase, storage));

    internal static string GetOrCreate(string dataDirectory, IDatabaseKeyStorage storage)
    {
        var saved = storage.Read();
        if (saved.Length > 0)
            return ValidateKey(saved);

        // Never replace a lost key with a new one while encrypted data still exists.
        if (File.Exists(Path.Combine(dataDirectory, "mail.db")) ||
            File.Exists(Path.Combine(dataDirectory, "database.key")))
            throw new DatabaseKeyRecoveryException();

        var key = RandomNumberGenerator.GetBytes(32);
        SaveVerified(storage, key);
        return Convert.ToHexString(key);
    }

    internal static string Recover(string dataDirectory, string passphrase, IDatabaseKeyStorage storage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passphrase);
        var key = DeriveLegacyKey(passphrase);
        // Validate without creating, migrating or modifying the user's database.
        SQLitePCL.Batteries_V2.Init();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "mail.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"PRAGMA key = '{key}';";
                command.ExecuteNonQuery();
                command.CommandText = "SELECT count(*) FROM sqlite_master;";
                command.ExecuteScalar();
            }
            catch (SqliteException)
            {
                throw new InvalidOperationException("That password could not unlock the existing database. Check the password and try again, or restore the original database and key from your backup.");
            }
        }

        var saved = storage.Read();
        if (saved.Length > 0)
        {
            if (ValidateKey(saved) != key)
                throw new InvalidOperationException("A different database key is already saved. Restore the matching database and key from your backup.");
            return key;
        }
        SaveVerified(storage, Convert.FromHexString(key));
        return key;
    }

    internal static string DeriveLegacyKey(string passphrase) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passphrase)));

    private static string ValidateKey(byte[] key) => key.Length == 32
        ? Convert.ToHexString(key)
        : throw new InvalidOperationException("The saved database key is damaged. Restore your original key from a backup; BetterMail has not replaced it.");

    private static void SaveVerified(IDatabaseKeyStorage storage, byte[] key)
    {
        storage.Write(key);
        if (!CryptographicOperations.FixedTimeEquals(key, storage.Read()))
            throw new InvalidOperationException("BetterMail could not securely save its database key. Unlock your system keyring and try again.");
    }

    private static string WithStorage(string dataDirectory, Func<IDatabaseKeyStorage, string> action)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(dataDirectory);
            using var keyLock = new CrossPlatLock(Path.Combine(dataDirectory, "database.key.lock"));
            return action(new SystemDatabaseKeyStorage(dataDirectory));
        }
    }
}

public sealed class DatabaseKeyRecoveryException() : InvalidOperationException(
    "BetterMail found an existing database without its saved key. Enter the password used by your previous setup once to save it securely. If you never set a password, restore the original key from your backup.");

internal interface IDatabaseKeyStorage
{
    byte[] Read();
    void Write(byte[] key);
}

internal sealed class SystemDatabaseKeyStorage : IDatabaseKeyStorage
{
    private readonly Storage _storage;

    public SystemDatabaseKeyStorage(string dataDirectory)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory)))));
        _storage = Storage.Create(new StorageCreationPropertiesBuilder("database.key", dataDirectory)
            .WithLinuxKeyring("com.bettermail.database", MsalCacheHelper.LinuxKeyRingDefaultCollection,
                "BetterMail database key", new("application", "BetterMail"), new("directory", identity))
            .WithMacKeyChain("com.bettermail.database", identity)
            .Build());
    }

    public byte[] Read() => Access(() => _storage.ReadData());

    public void Write(byte[] key) => Access(() => { _storage.WriteData(key); return true; });

    private static T Access<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                "BetterMail could not access secure storage. Unlock your system keyring or Keychain and try again. On Linux, a desktop Secret Service and libsecret must be available.", error);
        }
    }
}
