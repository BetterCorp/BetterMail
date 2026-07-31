using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace BetterMail.App;

public sealed partial class ConversationThreadView : UserControl
{
    public ConversationThreadView()
    {
        InitializeComponent();
        KeyDown += HandleKeyDown;
    }

    internal static bool IsCompactWidth(double width) => width < 640;

    private void HandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not ConversationThreadViewModel viewModel ||
            args.Source is TextBox ||
            viewModel.SelectedThread is null)
        {
            return;
        }

        var messages = viewModel.SelectedThread.Messages;
        var index = viewModel.SelectedMessage is null ? -1 : messages.IndexOf(viewModel.SelectedMessage);
        if (args.Key is Key.Up or Key.Down && messages.Count > 0)
        {
            var next = args.Key == Key.Up
                ? Math.Max(0, index - 1)
                : Math.Min(messages.Count - 1, index + 1);
            viewModel.SelectMessageCommand.Execute(messages[next]);
            args.Handled = true;
            return;
        }

        var command = args.Key switch
        {
            Key.R when args.KeyModifiers.HasFlag(KeyModifiers.Shift) => viewModel.ReplyAllCommand,
            Key.R => viewModel.ReplyCommand,
            Key.F => viewModel.ForwardCommand,
            _ => null
        };
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            args.Handled = true;
        }
    }

    private void MessageWebView_NavigationStarted(
        object? sender,
        WebViewNavigationStartingEventArgs args)
    {
        var request = args.Request;
        if (request is null ||
            (request.Scheme != Uri.UriSchemeHttp && request.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            args.Cancel = Process.Start(
                new ProcessStartInfo(request.AbsoluteUri) { UseShellExecute = true }) is not null;
        }
        catch
        {
            args.Cancel = false;
        }
    }
}
