using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.VisualTree;
using BetterMail.App;
using BetterMail.Core;

internal static partial class Program
{
    private static async Task CaptureTunnelsAsync(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bettermail-tunnels-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new EncryptedMailStore(Path.Combine(directory, "mail.db"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            await store.InitializeAsync();
            var microsoft = new MailAccount("microsoft365", "preview-microsoft", "preview", "alex@work.example", "Alex Morgan", ProviderCapabilities.Mail);
            var google = new MailAccount("google-workspace", "preview-google", "preview", "alex@studio.example", "Studio", ProviderCapabilities.Mail);
            await store.SaveAccountAsync(microsoft);
            await store.SaveAccountAsync(google);
            var vm = new MainWindowViewModel(store, directory, _ => { }, _ => { }, null,
                provider: DispatchProxy.Create<IPreviewWebhookMailProvider, PreviewWebhookProvider>());
            await vm.InitializeAsync();
            await vm.Tunnels.InitializeAsync();
            await vm.Mcp.InitializeAsync();
            var view = new SettingsView();
            var window = new Window { DataContext = vm, Content = view, Width = 1100, Height = 1000,
                WindowDecorations = WindowDecorations.None, Position = new(0, 0) };
            try
            {
                window.Show();
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                {
                    Application.Current!.RequestedThemeVariant = theme;
                    vm.SelectedSettingsTab = vm.SettingsTabs.Single(tab => tab.Name == "MCP");
                    await Task.Delay(100);
                    Click("Manage Tunnels/Web");
                    if (!vm.ShowTunnelsSettings) throw new InvalidOperationException("MCP did not navigate to Tunnels/Web.");
                    await Shot("tunnels-" + theme);
                    Click("Manage account webhooks");
                    if (!vm.ShowAccountSettings) throw new InvalidOperationException("Tunnels/Web did not navigate to Accounts.");
                    await Task.Delay(100);
                    var toggles = view.GetVisualDescendants().OfType<ToggleSwitch>()
                        .Where(toggle => toggle.IsEffectivelyVisible && toggle.Content as string == "Enable mail webhooks").ToArray();
                    if (toggles.Length != 2 || toggles.Count(toggle => toggle.IsEnabled) != 1)
                        throw new InvalidOperationException("Account webhook support was not bound correctly.");
                    var accountScroll = toggles[0].GetVisualAncestors().OfType<ScrollViewer>().First();
                    var accountPoint = toggles[0].TranslatePoint(new Point(), accountScroll)!.Value;
                    accountScroll.Offset = new Vector(0, accountScroll.Offset.Y + accountPoint.Y - 100);
                    await Shot("webhook-accounts-" + theme);
                    Click("Manage Tunnels/Web");
                    if (!vm.ShowTunnelsSettings) throw new InvalidOperationException("Accounts did not navigate to Tunnels/Web.");
                    Click("Manage MCP");
                    if (!vm.ShowMcpSettings) throw new InvalidOperationException("Tunnels/Web did not navigate to MCP.");
                    var publicControl = view.GetVisualDescendants().OfType<ToggleSwitch>().Single(toggle => toggle.Content as string == "Enable public MCP access");
                    publicControl.IsChecked = true;
                    if (!vm.Mcp.PublicEnabled) throw new InvalidOperationException("Public MCP control is not bound.");
                    var mcpScroll = publicControl.GetVisualAncestors().OfType<ScrollViewer>().First();
                    var mcpPoint = publicControl.TranslatePoint(new Point(), mcpScroll)!.Value;
                    mcpScroll.Offset = new Vector(0, mcpScroll.Offset.Y + mcpPoint.Y - 100);
                    await Shot("public-mcp-" + theme);
                }
                Console.WriteLine("Tunnels/Web navigation, per-account support, and independent public MCP control passed in light and dark themes.");
            }
            finally { window.Close(); await vm.Tunnels.DisposeAsync(); await vm.Mcp.DisposeAsync(); }

            void Click(string label)
            {
                var button = view.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.Content as string == label);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            async Task Shot(string name)
            {
                await Task.Delay(300);
                using var process = Process.Start(new ProcessStartInfo("python3") { ArgumentList =
                    { "tools/BetterMail.UiPreview/capture.py", Path.Combine(output, name + ".png"), "1100", "1000" } })!;
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new InvalidOperationException("Settings capture failed.");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}

public interface IPreviewWebhookMailProvider : IMailProvider, IMailWebhookProvider;
public class PreviewWebhookProvider : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "SupportsMailWebhooks" => args?[0] is MailAccount { ProviderId: "microsoft365" },
        "get_SupportsCloudDrafts" or "SupportsCloudDraftsFor" => false,
        _ => throw new NotSupportedException("The settings preview does not access mail providers.")
    };
}
