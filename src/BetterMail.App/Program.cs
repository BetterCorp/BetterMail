using Avalonia;
using Velopack;

namespace BetterMail.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(true)
            .OnBeforeUninstallFastCallback(_ => DefaultMailApp.Unregister())
            .Run();
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
