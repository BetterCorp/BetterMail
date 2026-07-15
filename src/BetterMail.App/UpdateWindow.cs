using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace BetterMail.App;

internal sealed class UpdateWindow : Window
{
    private readonly ProgressBar? _progress;
    private readonly TextBlock? _message;

    public UpdateWindow(string version)
    {
        Title = "Updating BetterMail";
        Width = 420;
        Height = 210;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 6 };
        _message = new TextBlock
        {
            Text = $"Downloading BetterMail {version}…",
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(_progress, "Update download progress");
        Content = Layout(
            new TextBlock
            {
                Text = "Updating BetterMail",
                FontSize = 20,
                FontWeight = FontWeight.SemiBold
            },
            _message,
            _progress);
    }

    private UpdateWindow(string version, bool prompt)
    {
        Title = "BetterMail update";
        Width = 440;
        Height = 230;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var later = new Button { Content = "Later", MinWidth = 90 };
        var now = new Button { Content = "Update now", MinWidth = 110 };
        now.Classes.Add("primary");
        later.Click += (_, _) => Close(false);
        now.Click += (_, _) => Close(true);
        Content = Layout(
            new TextBlock
            {
                Text = "An update is available",
                FontSize = 20,
                FontWeight = FontWeight.SemiBold
            },
            new TextBlock
            {
                Text = $"BetterMail {version} is downloading. Update now waits for it to finish and restarts BetterMail. Later keeps downloading and applies it the next time you start the app.",
                TextWrapping = TextWrapping.Wrap
            },
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Children = { later, now }
            });
    }

    public static Task<bool> PromptAsync(Window owner, string version) =>
        new UpdateWindow(version, prompt: true).ShowDialog<bool>(owner);

    public void ReportProgress(int value) => Dispatcher.UIThread.Post(() =>
    {
        if (_progress is not null)
        {
            _progress.Value = value;
        }
    });

    public void ShowError(string detail) => Dispatcher.UIThread.Post(() =>
    {
        if (_message is null)
        {
            return;
        }

        _message.Text = $"The update could not be downloaded. {detail}";
        if (_progress is not null)
        {
            _progress.IsVisible = false;
        }

        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 90
        };
        close.Click += (_, _) => Close();
        ((StackPanel)((Border)Content!).Child!).Children.Add(close);
    });

    private static Border Layout(params Control[] children)
    {
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 16
        };
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        return new Border
        {
            Padding = new Avalonia.Thickness(24),
            Child = stack
        };
    }
}
