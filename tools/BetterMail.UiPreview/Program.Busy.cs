using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using BetterMail.App;
using BetterMail.Core;
using System.Diagnostics;

internal static partial class Program
{
    private static async Task CaptureBusyAsync(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-busy-preview-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var vm = new MainWindowViewModel(null, directory, _ => { }, _ => { }, null);
        vm.Accounts.Add(PreviewProvider.Account);
        await vm.InitializeAsync();
        var now = DateTimeOffset.Now;
        vm.BusyActions.Add(new("move", "account", "account:alex@example.test", "message", MailActionKind.Move,
            "Project review", now.AddDays(-1), DestinationName: "Archive", Error: "The specified object was not found in the store.",
            FailureCount: 22, LastAttemptAt: now.AddMinutes(-2), LastFailureAt: now.AddMinutes(-2)));
        vm.BusyActions.Add(new("send", "account", "account:alex@example.test", "draft", MailActionKind.Send,
            "Planning notes", now, Error: "The connection was interrupted before delivery was confirmed.", SendAttempted: true, FailureCount: 1));
        vm.ShowOutboxCommand.Execute(null);
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 900, WindowDecorations = WindowDecorations.None, Position = new PixelPoint(0, 0) };
        try
        {
            window.Show();
            window.Position = new PixelPoint(0, 0);
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var rows = vm.BusyActions.ToArray();
                vm.BusyActions.Clear();
                typeof(MainWindowViewModel).GetMethod("RecordSyncOutcome", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [false, false]);
                foreach (var row in rows) vm.BusyActions.Add(row);
                Application.Current!.RequestedThemeVariant = theme;
                await Task.Delay(700);
                if (!window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == "The specified object was not found in the store."))
                    throw new InvalidOperationException("Busy failure details were not rendered.");
                using var capture = Process.Start(new ProcessStartInfo("python3") { ArgumentList = {
                    "tools/BetterMail.UiPreview/capture.py", Path.Combine(output, "busy-" + theme.ToString().ToLowerInvariant() + ".png"), "1200", "900" } })!;
                await capture.WaitForExitAsync();
                if (capture.ExitCode != 0) throw new InvalidOperationException("Busy capture failed.");
                foreach (var (state, colour) in new[] { ("orange", "#D98200"), ("red", "#E45155") })
                {
                    typeof(MainWindowViewModel).GetMethod("RecordSyncOutcome", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(vm, [false, false]);
                    await Task.Delay(200);
                    foreach (var name in new[] { "SyncStatusButton", "BusyNavigationButton" })
                        if (window.FindControl<Button>(name)!.Foreground is not Avalonia.Media.ISolidColorBrush brush || brush.Color != Avalonia.Media.Color.Parse(colour))
                            throw new InvalidOperationException(name + " did not show " + state);
                    using var coloured = Process.Start(new ProcessStartInfo("python3") { ArgumentList = {
                        "tools/BetterMail.UiPreview/capture.py", Path.Combine(output, "busy-" + theme.ToString().ToLowerInvariant() + "-" + state + ".png"), "1200", "900" } })!;
                    await coloured.WaitForExitAsync();
                    if (coloured.ExitCode != 0) throw new InvalidOperationException("Colour capture failed.");
                }
            }
        }
        finally { window.Close(); Directory.Delete(directory, true); }
    }
}
