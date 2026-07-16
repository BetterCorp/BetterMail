using System.Text.Json;

namespace BetterMail.App;

public sealed record AppPreferences(
    string ThemeMode = "System",
    string AccentName = "Blue",
    bool IsCompact = false,
    bool DesktopNotificationsEnabled = true,
    string MailSyncRange = "All mail",
    string Signature = "",
    string? DefaultSenderMailboxId = null,
    Dictionary<string, string>? SenderSignatures = null,
    List<SignaturePreference>? Signatures = null,
    Dictionary<string, MailboxSignaturePreferences>? MailboxSignatures = null,
    List<string>? MailQuickActions = null);

public static class AppPreferencesStore
{
    private const string FileName = "settings.json";

    public static AppPreferences Load(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, FileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(path)) ?? new AppPreferences()
                : new AppPreferences();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppPreferences();
        }
    }

    public static void Save(string dataDirectory, AppPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(
                Path.Combine(dataDirectory, FileName),
                JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preferences are non-critical; mail remains usable if the profile is read-only.
        }
    }
}
