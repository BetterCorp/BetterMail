using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BetterMail.App;

public sealed class StartupWindow : Window
{
    public StartupWindow(bool dark)
    {
        var background = new SolidColorBrush(Color.Parse(dark ? "#202020" : "#F3F3F3"));
        var foreground = new SolidColorBrush(Color.Parse(dark ? "#F5F5F5" : "#1B1B1B"));
        using var logoStream = AssetLoader.Open(new Uri("avares://BetterMail/Assets/BetterMail.png"));
        var progress = new ProgressBar { Width = 240, Height = 4, IsIndeterminate = true };
        AutomationProperties.SetName(progress, "Loading BetterMail");

        Title = "BetterMail";
        Width = 420;
        Height = 230;
        CanResize = false;
        ShowInTaskbar = true;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = background;
        Content = new Border
        {
            Background = background,
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 14,
                Children =
                {
                    new Image
                    {
                        Source = new Bitmap(logoStream),
                        Width = 48,
                        Height = 48,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    new TextBlock
                    {
                        Text = "BetterMail",
                        Foreground = foreground,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 22,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    progress,
                    new TextBlock
                    {
                        Text = "Loading BetterMail…",
                        Foreground = foreground,
                        Opacity = 0.68,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                }
            }
        };
    }
}
