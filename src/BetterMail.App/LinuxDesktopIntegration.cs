using System.Diagnostics;

namespace BetterMail.App;

internal static class LinuxDesktopIntegration
{
    internal static string DataDirectory =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } directory && Path.IsPathFullyQualified(directory)
            ? directory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static void Register()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage)
            return;
        try { Install(appImage, DataDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Read-only home directories must not prevent opening mail.
            Trace.TraceWarning("Could not register Linux launcher: {0}", exception.GetType().Name);
        }
    }

    internal static void Install(string appImage, string dataDirectory)
    {
        if (!Path.IsPathFullyQualified(appImage) || appImage.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("The AppImage path must be an absolute single-line path.", nameof(appImage));

        var iconPath = Path.Combine(dataDirectory, "icons", "hicolor", "512x512", "apps", "BetterMail.png");
        Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
        using (var icon = typeof(LinuxDesktopIntegration).Assembly.GetManifestResourceStream("BetterMail.LinuxIcon.png")!)
        using (var output = File.Create(iconPath))
            icon.CopyTo(output);

        var applications = Path.Combine(dataDirectory, "applications");
        Directory.CreateDirectory(applications);
        // Use a persistent icon, not the temporary AppImage mount. Registering the launcher
        // does not change the user's default mail application.
        // env also avoids GLib checking the executable path before decoding literal %%.
        var desktop = $"""
            [Desktop Entry]
            Type=Application
            Name=BetterMail
            Comment=Fast local-first Microsoft 365 mail
            Exec=env -- {QuoteExecutable(appImage)} %u
            Icon={EscapeValue(iconPath)}
            StartupWMClass=BetterMail
            Categories=Network;Email;
            MimeType=x-scheme-handler/mailto;x-scheme-handler/bettermail;
            Terminal=false

            """;
        File.WriteAllText(Path.Combine(applications, "bettermail.desktop"), desktop);
    }

    private static string EscapeValue(string value) => value.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static string QuoteExecutable(string value)
    {
        // Exec quoting is decoded after desktop-entry string escaping.
        var quoted = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%");
        return "\"" + EscapeValue(quoted) + "\"";
    }
}
