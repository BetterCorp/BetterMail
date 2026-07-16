using System.Text;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace BetterMail.App;

internal enum FilePreviewKind
{
    Unsupported,
    Image,
    Pdf,
    Text
}

public sealed partial class FilePreviewWindow : Window
{
    private const int MaximumTextPreviewBytes = 5 * 1024 * 1024;
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

    public FilePreviewWindow()
    {
        InitializeComponent();
    }

    public FilePreviewWindow(string name, string contentType, long size, byte[]? content) : this()
    {
        _name = string.IsNullOrWhiteSpace(name) ? "Attachment" : name;
        _contentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        _content = content;
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
        catch
        {
            ShowUnavailable("BetterMail could not render this file. You can still save it.");
        }
        finally
        {
            LoadingPreview.IsVisible = false;
        }
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
        _bitmap?.Dispose();
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
            // ponytail: the OS temp directory handles crash/lock leftovers; add a startup sweep only if these accumulate.
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
