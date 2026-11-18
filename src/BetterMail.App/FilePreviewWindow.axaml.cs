using System.Text;
using BetterMail.Core;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace BetterMail.App;

internal enum FilePreviewKind
{
    Unsupported,
    Image,
    Pdf,
    Text,
    Email
}

public sealed partial class FilePreviewWindow : Window
{
    private const int MaximumTextPreviewBytes = 5 * 1024 * 1024;
    internal const int MaximumEmailPreviewBytes = 50 * 1024 * 1024;
    private const int NavigateToStringLimitBytes = 2 * 1024 * 1024;
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/bmp", "image/gif", "image/jpeg", "image/png", "image/tiff", "image/webp"
    };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csv", ".html", ".json", ".log", ".md", ".txt", ".xml"
    };

    private string _name = "Attachment";
    private string _contentType = "application/octet-stream";
    private byte[]? _content;
    private string? _temporaryDirectory;
    private Bitmap? _bitmap;
    private IFilesProvider? _filesProvider;
    private IReadOnlyList<MailAccount> _driveAccounts = [];
    private AttachmentDriveSaveWindow? _driveWindow;
    private bool _closed;

    public FilePreviewWindow()
    {
        InitializeComponent();
        _ = new ReadOnlyWebViewLinks(EmlBodyPreview);
    }

    public FilePreviewWindow(string name, string contentType, long size, byte[]? content,
        IFilesProvider? filesProvider = null, IReadOnlyList<MailAccount>? accounts = null) : this()
    {
        _name = string.IsNullOrWhiteSpace(name) ? "Attachment" : name;
        _contentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        _content = content;
        _filesProvider = filesProvider;
        _driveAccounts = accounts ?? [];
        SaveDriveButton.IsEnabled = content is not null;
        Title = $"{_name} — Preview";
        FileNameText.Text = _name;
        FileDetailsText.Text = $"{FormatSize(content?.LongLength ?? size)} · {_contentType}";
        SaveButton.IsEnabled = content is not null;
        Opened += WindowOpened;
        Closed += WindowClosed;
    }

    internal static FilePreviewKind PreviewKindFor(string name, string contentType)
    {
        var extension = Path.GetExtension(name);
        var mediaType = contentType.Split(';', 2)[0].Trim();
        if (string.Equals(extension, ".eml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mediaType, "message/rfc822", StringComparison.OrdinalIgnoreCase))
        {
            return FilePreviewKind.Email;
        }
        if (string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return FilePreviewKind.Pdf;
        }
        if (ImageTypes.Contains(mediaType) || ImageExtensions.Contains(extension))
        {
            return FilePreviewKind.Image;
        }
        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            TextExtensions.Contains(extension))
        {
            return FilePreviewKind.Text;
        }
        return FilePreviewKind.Unsupported;
    }

    private async void WindowOpened(object? sender, EventArgs e)
    {
        try
        {
            if (_content is null)
            {
                ShowUnavailable("The attachment data is unavailable.");
                return;
            }

            switch (PreviewKindFor(_name, _contentType))
            {
                case FilePreviewKind.Email when _content.Length <= MaximumEmailPreviewBytes:
                    await ShowEmailAsync(await Task.Run(() => EmlMessagePreview.Parse(_content)));
                    break;
                case FilePreviewKind.Email:
                    ShowUnavailable("Email previews are limited to 50 MB. Save this file to view it.");
                    break;
                case FilePreviewKind.Image:
                    _bitmap = await Task.Run(() =>
                    {
                        using var stream = new MemoryStream(_content);
                        return new Bitmap(stream);
                    });
                    ImagePreview.Source = _bitmap;
                    ImagePreview.IsVisible = true;
                    break;
                case FilePreviewKind.Pdf when
                    _content.AsSpan(0, Math.Min(_content.Length, 1024)).IndexOf("%PDF-"u8) >= 0:
                    var directory = Directory.CreateTempSubdirectory("BetterMail-preview-");
                    _temporaryDirectory = directory.FullName;
                    var path = Path.Combine(directory.FullName, "preview.pdf");
                    await File.WriteAllBytesAsync(path, _content);
                    if (_closed)
                    {
                        CleanupTemporaryDirectory();
                        break;
                    }
                    PdfPreview.Source = new Uri(path, UriKind.Absolute);
                    PdfPreview.IsVisible = true;
                    break;
                case FilePreviewKind.Text when _content.Length <= MaximumTextPreviewBytes:
                    TextPreview.Text = await Task.Run(() =>
                    {
                        using var reader = new StreamReader(
                            new MemoryStream(_content),
                            Encoding.UTF8,
                            detectEncodingFromByteOrderMarks: true);
                        return reader.ReadToEnd();
                    });
                    TextPreview.IsVisible = true;
                    break;
                case FilePreviewKind.Text:
                    ShowUnavailable("Text previews are limited to 5 MB. Save this file to view it.");
                    break;
                default:
                    ShowUnavailable("Save this file and open it with another application.");
                    break;
            }
        }
        catch (FormatException)
        {
            ShowUnavailable("This file does not contain a valid email message.");
        }
        catch
        {
            ShowUnavailable("BetterMail could not render this file. You can still save it.");
        }
        finally
        {
            LoadingPreview.IsVisible = false;
        }
    }

    private async Task ShowEmailAsync(EmlMessagePreview message)
    {
        if (_closed) return;
        EmlSubject.Text = message.Subject;
        EmlFrom.Text = $"From: {message.From}";
        EmlTo.Text = $"To: {message.To}";
        EmlCc.Text = $"Cc: {message.Cc}";
        EmlCc.IsVisible = !string.IsNullOrWhiteSpace(message.Cc);
        EmlDate.Text = message.Date;
        foreach (var attachment in message.Attachments)
        {
            var button = new Button
            {
                Content = attachment.Name,
                MaxWidth = 240,
                Margin = new Avalonia.Thickness(0, 0, 6, 4),
                [Avalonia.Automation.AutomationProperties.NameProperty] = $"Preview attachment {attachment.Name}"
            };
            ToolTip.SetTip(button, attachment.Name);
            button.Classes.Add("command");
            button.Click += (_, _) => IndependentWindow.Show(new FilePreviewWindow(
                attachment.Name, attachment.ContentType, attachment.Size, attachment.ContentBytes,
                _filesProvider, _driveAccounts));
            EmlAttachmentList.Children.Add(button);
        }
        EmlAttachmentList.IsVisible = message.Attachments.Count > 0;
        EmlPreview.IsVisible = true;

        var html = await Task.Run(() => new MailContentRenderer().RenderDocument(
            message.Body, message.IsHtml, message.InlineParts,
            maxInlineImageCharacters: 16 * 1024 * 1024));
        if (_closed) return;
        if (Encoding.UTF8.GetByteCount(html) <= NavigateToStringLimitBytes)
        {
            EmlBodyPreview.NavigateToString(html, new Uri("about:blank"));
        }
        else
        {
            try
            {
                var directory = Directory.CreateTempSubdirectory("BetterMail-preview-");
                _temporaryDirectory = directory.FullName;
                var path = Path.Combine(directory.FullName, "message.html");
                await File.WriteAllTextAsync(path, html, Encoding.UTF8);
                if (!_closed) EmlBodyPreview.Source = new Uri(path, UriKind.Absolute);
            }
            finally
            {
                if (_closed) CleanupTemporaryDirectory();
            }
        }
    }

    private void SaveDriveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_content is null) return;
        if (_filesProvider is null)
        {
            FileDetailsText.Text = "Connect a OneDrive account in Settings to save attachments to Drive.";
            return;
        }
        if (_driveWindow is not null)
        {
            _driveWindow.Activate();
            return;
        }
        _driveWindow = new AttachmentDriveSaveWindow(new AttachmentDriveSaveViewModel(
            _filesProvider, _driveAccounts, _name, _contentType, _content));
        _driveWindow.Closed += (_, _) => _driveWindow = null;
        IndependentWindow.Show(_driveWindow);
    }

    private async void SaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_content is null)
        {
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save attachment",
                SuggestedFileName = _name
            });
            if (file is null)
            {
                return;
            }
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            await stream.WriteAsync(_content);
        }
        catch
        {
            FileDetailsText.Text = "Could not save file";
        }
    }

    private void PdfNavigationStarted(object? sender, WebViewNavigationStartingEventArgs e)
    {
        if (e.Request?.Scheme is "http" or "https")
        {
            e.Cancel = true;
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _bitmap?.Dispose();
        CleanupTemporaryDirectory();
    }

    private void CleanupTemporaryDirectory()
    {
        if (_temporaryDirectory is null)
        {
            return;
        }
        try
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
            _temporaryDirectory = null;
        }
        catch (DirectoryNotFoundException) { _temporaryDirectory = null; }
        catch
        {
            // An in-flight WebView or file write can briefly retain the directory. Its continuation retries cleanup.
        }
    }

    private void ShowUnavailable(string message)
    {
        UnavailableText.Text = message;
        UnavailablePreview.IsVisible = true;
    }

    private static string FormatSize(long size) => size < 1024 * 1024
        ? $"{Math.Max(1, size / 1024):N0} KB"
        : $"{size / (1024d * 1024d):N1} MB";
}
