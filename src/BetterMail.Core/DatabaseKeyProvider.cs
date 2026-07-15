using System.Security.Cryptography;
using System.Text;

namespace BetterMail.Core;

public static class DatabaseKeyProvider
{
    private const string EnvironmentVariable = "BETTERMAIL_DATABASE_KEY";

    public static string GetOrCreate(string dataDirectory)
    {
        var configuredKey = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey)));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                $"Set {EnvironmentVariable} to a strong passphrase. BetterMail will not store an unprotected database key on this platform.");
        }

        Directory.CreateDirectory(dataDirectory);
        var keyPath = Path.Combine(dataDirectory, "database.key");
        if (File.Exists(keyPath))
        {
            return Convert.ToHexString(ProtectedData.Unprotect(File.ReadAllBytes(keyPath), null, DataProtectionScope.CurrentUser));
        }

        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyPath, ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
        return Convert.ToHexString(key);
    }
}
