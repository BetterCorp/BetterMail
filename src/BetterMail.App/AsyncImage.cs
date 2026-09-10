using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace BetterMail.App;

/// <summary>Loads only while in the viewport; recycled rows cannot receive stale images.</summary>
public sealed class AsyncImage : Grid
{
    public static readonly StyledProperty<ImageRequest?> RequestProperty = AvaloniaProperty.Register<AsyncImage, ImageRequest?>(nameof(Request));
    public static readonly StyledProperty<string> FallbackPathProperty = AvaloniaProperty.Register<AsyncImage, string>(nameof(FallbackPath), "M8,8 A4,4 0 1 0 16,8 A4,4 0 1 0 8,8 M4,22 V19 C4,12 20,12 20,19 V22");
    public ImageRequest? Request { get => GetValue(RequestProperty); set => SetValue(RequestProperty, value); }
    public string FallbackPath { get => GetValue(FallbackPathProperty); set => SetValue(FallbackPathProperty, value); }
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Avalonia.Controls.Shapes.Path _fallback = new() { Width = 22, Height = 22, Stretch = Stretch.Uniform, Stroke = Brushes.Gray, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? _cancellation;
    private Bitmap? _bitmap;
    private bool _inViewport;

    public AsyncImage()
    {
        Children.Add(_fallback);
        Children.Add(_image);
        _fallback.Data = Geometry.Parse(FallbackPath);
        EffectiveViewportChanged += (_, args) =>
        {
            var visible = args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            if (_inViewport == visible) return;
            _inViewport = visible;
            if (visible) Start(); else Stop();
        };
        DetachedFromVisualTree += (_, _) => { _inViewport = false; Stop(); };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RequestProperty) { Stop(); if (_inViewport) Start(); }
        if (change.Property == FallbackPathProperty) _fallback.Data = Geometry.Parse(FallbackPath);
    }

    private void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _fallback.IsVisible = true;
    }

    private async void Start()
    {
        if (_cancellation is not null || Request is not { } request || !this.IsAttachedToVisualTree()) return;
        var source = _cancellation = new CancellationTokenSource();
        var token = source.Token;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(async () =>
            {
                var bytes = await BackgroundImages.GetAsync(request, token);
                token.ThrowIfCancellationRequested();
                return bytes is null ? null : new Bitmap(new MemoryStream(bytes, writable: false));
            }, token);
            if (token.IsCancellationRequested || _cancellation != source) return;
            _bitmap = bitmap;
            bitmap = null;
            _image.Source = _bitmap;
            _fallback.IsVisible = _bitmap is null;
        }
        catch (Exception) { /* Optional artwork must never interrupt navigation. */ }
        finally { bitmap?.Dispose(); }
    }
}
