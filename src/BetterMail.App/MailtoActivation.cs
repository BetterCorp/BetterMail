using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace BetterMail.App;

internal static class MailtoParser
{
    public static bool TryParse(string? value, out ComposeRequest request)
    {
        request = new();
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var raw = value["mailto:".Length..];
        var separator = raw.IndexOf('?');
        var path = separator < 0 ? raw : raw[..separator];
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        Add(fields, "to", Decode(path));
        if (separator >= 0)
        {
            foreach (var pair in raw[(separator + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var equals = pair.IndexOf('=');
                var name = Decode(equals < 0 ? pair : pair[..equals]).ToLowerInvariant();
                if (name is not ("to" or "cc" or "bcc" or "subject" or "body"))
                {
                    continue;
                }
                Add(fields, name, Decode(equals < 0 ? "" : pair[(equals + 1)..]));
            }
        }

        request = new ComposeRequest(
            To: Join(fields, "to", "; "),
            Subject: Join(fields, "subject", " ").ReplaceLineEndings(" "),
            Body: Join(fields, "body", Environment.NewLine),
            Cc: Join(fields, "cc", "; "),
            Bcc: Join(fields, "bcc", "; "));
        return true;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    private static void Add(Dictionary<string, List<string>> fields, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (!fields.TryGetValue(name, out var values))
        {
            fields[name] = values = [];
        }
        values.Add(value);
    }

    private static string Join(Dictionary<string, List<string>> fields, string name, string separator) =>
        fields.TryGetValue(name, out var values) ? string.Join(separator, values) : "";
}

internal static class AppActivation
{
    private static readonly ConcurrentQueue<string> Pending = new();
    private static Action<string>? _handler;

    public static void Publish(string value)
    {
        var handler = Volatile.Read(ref _handler);
        if (handler is null)
        {
            Pending.Enqueue(value);
        }
        else
        {
            handler(value);
        }
    }

    public static void SetHandler(Action<string> handler)
    {
        Volatile.Write(ref _handler, handler);
        while (Pending.TryDequeue(out var value))
        {
            handler(value);
        }
    }
}

internal sealed class AppActivationRelay : IDisposable
{
    private const string PipeName = "BetterCorp.BetterMail.Activation.v1";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();

    private AppActivationRelay(Mutex mutex)
    {
        _mutex = mutex;
        _ = ListenAsync();
    }

    public static bool TryStartPrimary(string activation, out AppActivationRelay? relay)
    {
        var mutex = new Mutex(initiallyOwned: true, PipeName, out var created);
        if (created)
        {
            relay = new(mutex);
            AppActivation.Publish(activation);
            return true;
        }

        mutex.Dispose();
        relay = null;
        TryForward(activation);
        return false;
    }

    private static bool TryForward(string activation)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(250);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(activation);
                return true;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                Thread.Sleep(100);
            }
        }
        return false;
    }

    private async Task ListenAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cancellation.Token);
                using var reader = new StreamReader(server);
                if (await reader.ReadLineAsync(_cancellation.Token) is { Length: > 0 } activation)
                {
                    AppActivation.Publish(activation);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _cancellation.Dispose();
    }
}

internal static class DefaultMailApp
{
    private const string ApplicationName = "BetterMail";
    private const string ProgId = "BetterCorp.BetterMail.mailto";
    private const string CapabilitiesPath = @"Software\BetterCorp\BetterMail\Capabilities";

    public static void Register()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { Length: > 0 } executable)
        {
            return;
        }

        try
        {
            using (var protocol = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                protocol.SetValue("", "URL:mailto Protocol");
                protocol.SetValue("URL Protocol", "");
                protocol.CreateSubKey("DefaultIcon").SetValue("", $"{executable},0");
                var quote = (char)34;
                protocol.CreateSubKey(@"shell\open\command").SetValue(
                    "", $"{quote}{executable}{quote} {quote}%1{quote}");
            }
            using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
            {
                capabilities.SetValue("ApplicationName", ApplicationName);
                capabilities.SetValue("ApplicationDescription", "Fast local-first Microsoft 365 mail");
                capabilities.CreateSubKey("UrlAssociations").SetValue("mailto", ProgId);
            }
            Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications")
                .SetValue(ApplicationName, CapabilitiesPath);
            SHChangeNotify(0x08000000, 0, 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    public static void Unregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\BetterCorp\BetterMail", false);
            using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            registered?.DeleteValue(ApplicationName, false);
            SHChangeNotify(0x08000000, 0, 0, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }
    }

    public static async Task ChooseAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Start("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(ApplicationName));
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", "-a Mail") { UseShellExecute = false });
            return;
        }
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(appImage))
        {
            throw new InvalidOperationException("Install the BetterMail AppImage before choosing it as the default mail app.");
        }
        if (appImage.Contains((char)34))
        {
            throw new InvalidOperationException("The AppImage path contains an unsupported quote character.");
        }

        var applications = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "applications");
        Directory.CreateDirectory(applications);
        var desktopPath = Path.Combine(applications, "bettermail.desktop");
        var desktop = """
            [Desktop Entry]
            Type=Application
            Name=BetterMail
            Comment=Fast local-first Microsoft 365 mail
            Exec=__APPIMAGE__ %u
            Icon=BetterMail
            Categories=Network;Email;
            MimeType=x-scheme-handler/mailto;
            Terminal=false

            """;
        desktop = desktop.Replace(
            "__APPIMAGE__", string.Concat((char)34, appImage, (char)34), StringComparison.Ordinal);
        await File.WriteAllTextAsync(desktopPath, desktop);
        using var process = Process.Start(new ProcessStartInfo(
            "xdg-mime", "default bettermail.desktop x-scheme-handler/mailto")
        {
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("xdg-mime is unavailable.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("The desktop could not set BetterMail as the default mail app.");
        }
    }

    private static void Start(string uri) =>
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
