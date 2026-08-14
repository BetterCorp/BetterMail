using Avalonia;
using Velopack;

namespace BetterMail.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var velopack = VelopackApp.Build()
            .SetAutoApplyOnStartup(true);
        if (OperatingSystem.IsWindows())
        {
            velopack.OnBeforeUninstallFastCallback(_ => DefaultMailApp.Unregister());
        }
        velopack.Run();
        DefaultMailApp.Register();

        var activation = args.FirstOrDefault(argument =>
            argument.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) ?? "activate";
        AppActivationRelay? relay = null;
        if (OperatingSystem.IsMacOS())
        {
            AppActivation.Publish(activation);
        }
        else if (!AppActivationRelay.TryStartPrimary(activation, out relay))
        {
            return;
        }
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            relay?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
