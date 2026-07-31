using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace BetterMail.App;

public sealed partial class ConversationThreadView : UserControl
{
    private const int NavigateToStringLimitBytes = 2 * 1024 * 1024;
    private ConversationThreadViewModel? _viewModel;
    private ConversationMessageItem? _message;
    private string? _displayedHtml;
    private bool _attached;
    private int _navigationVersion;
    private string? _temporaryDirectory;
    private string? _temporaryDocument;

    public ConversationThreadView()
    {
        InitializeComponent();
        KeyDown += HandleKeyDown;
        DataContextChanged += (_, _) => BindViewModel(DataContext as ConversationThreadViewModel);
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            BindViewModel(DataContext as ConversationThreadViewModel);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            _navigationVersion++;
            BindViewModel(null);
            CleanupTemporaryContent();
        };
    }

    internal static bool IsCompactWidth(double width) => width < 640;

    private void BindViewModel(ConversationThreadViewModel? viewModel)
    {
        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= ViewModelPropertyChanged;
            }
            _viewModel = viewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += ViewModelPropertyChanged;
            }
        }
        BindMessage(_viewModel?.SelectedMessage);
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ConversationThreadViewModel.SelectedMessage))
        {
            BindMessage(_viewModel?.SelectedMessage);
        }
    }

    private void BindMessage(ConversationMessageItem? message)
    {
        if (!ReferenceEquals(_message, message))
        {
            if (_message is not null)
            {
                _message.PropertyChanged -= MessagePropertyChanged;
            }
            _message = message;
            _displayedHtml = null;
            if (_message is not null)
            {
                _message.PropertyChanged += MessagePropertyChanged;
            }
        }
        NavigateToMessage();
    }

    private void MessagePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ConversationMessageItem.BodyHtml))
        {
            NavigateToMessage();
        }
    }

    private async void NavigateToMessage()
    {
        if (!_attached || _message is null)
        {
            return;
        }
        var html = _message.BodyHtml;
        if (string.Equals(_displayedHtml, html, StringComparison.Ordinal))
        {
            return;
        }
        _displayedHtml = html;
        var version = ++_navigationVersion;
        try
        {
            if (Encoding.UTF8.GetByteCount(html) <= NavigateToStringLimitBytes)
            {
                MessageWebView.NavigateToString(html, new Uri("about:blank"));
                DeleteTemporaryDocument();
                return;
            }
            _temporaryDirectory ??= Directory.CreateTempSubdirectory("BetterMail-message-").FullName;
            var path = Path.Combine(_temporaryDirectory, $"message-{version}.html");
            await File.WriteAllTextAsync(path, html, Encoding.UTF8);
            if (!_attached || version != _navigationVersion)
            {
                TryDelete(path);
                return;
            }
            var previous = _temporaryDocument;
            _temporaryDocument = path;
            MessageWebView.Source = new Uri(path, UriKind.Absolute);
            TryDelete(previous);
        }
        catch
        {
            if (_attached && version == _navigationVersion)
            {
                MessageWebView.NavigateToString(
                    "<html><body>BetterMail could not render this unusually large message.</body></html>",
                    new Uri("about:blank"));
            }
        }
    }

    private void DeleteTemporaryDocument()
    {
        TryDelete(_temporaryDocument);
        _temporaryDocument = null;
    }

    private void CleanupTemporaryContent()
    {
        DeleteTemporaryDocument();
        if (_temporaryDirectory is null)
        {
            return;
        }
        try
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
        catch
        {
            // The OS temp directory handles a WebView file still being released.
        }
        _temporaryDirectory = null;
    }

    private static void TryDelete(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch
        {
            // WebView can briefly retain the previous document during navigation.
        }
    }

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
