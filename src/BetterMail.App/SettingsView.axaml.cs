using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace BetterMail.App;

public sealed partial class SettingsView : UserControl
{
    private bool _layoutInitialized;
    private bool _isPhone;

    public SettingsView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
        ApplyResponsiveLayout(Bounds.Width);
    }

    private void ApplyResponsiveLayout(double width)
    {
        var phone = width < 720;
        if (_layoutInitialized && _isPhone == phone)
        {
            return;
        }
        _layoutInitialized = true;
        _isPhone = phone;
        SettingsContent.Margin = new Thickness(phone ? 14 : 28);
        SettingsBanner.Height = phone ? 118 : 176;
        SignatureTemplateLayout.ColumnDefinitions.Clear();
        SignatureTemplateLayout.RowDefinitions.Clear();
        SignatureEditorLayout.ColumnDefinitions.Clear();
        SignatureEditorLayout.RowDefinitions.Clear();
        if (phone)
        {
            SignatureTemplateLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SignatureTemplateLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            SignatureTemplateLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(SignatureTemplatePreviewPanel, 0);
            Grid.SetRow(SignatureTemplatePreviewPanel, 1);
            SignatureTemplatePreviewPanel.Margin = new Thickness(0, 8, 0, 0);
            SignatureEditorLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SignatureEditorLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            SignatureEditorLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(SignatureEditorPanel, 0);
            Grid.SetRow(SignatureEditorPanel, 1);
            SignatureEditorPanel.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            SignatureTemplateLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SignatureTemplateLayout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(420)));
            SignatureTemplateLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(SignatureTemplatePreviewPanel, 1);
            Grid.SetRow(SignatureTemplatePreviewPanel, 0);
            SignatureTemplatePreviewPanel.Margin = new Thickness(0);
            SignatureEditorLayout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(240)));
            SignatureEditorLayout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SignatureEditorLayout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(SignatureEditorPanel, 1);
            Grid.SetRow(SignatureEditorPanel, 0);
            SignatureEditorPanel.Margin = new Thickness(0);
        }
    }

    private Window? _signatureWindow;
    private void EditSignatureClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_signatureWindow is { } existing) { existing.Activate(); return; }
        if (DataContext is not MainWindowViewModel vm) return;
        var editor = new RichHtmlEditor { Html = vm.SignatureEditorHtml, IsReadOnly = !vm.CanEditSelectedSignature };
        var title = new TextBlock { Text = vm.SignatureEditorName, FontSize = 20, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        var done = new Button { Content = "Done", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12, Margin = new Thickness(16) };
        Grid.SetRow(editor, 1); Grid.SetRow(done, 2);
        grid.Children.Add(title); grid.Children.Add(editor); grid.Children.Add(done);
        var window = new Window { Title = "Signature editor", Width = 760, Height = 560, MinWidth = 400, MinHeight = 360, Content = grid };
        // Bind to the selected signature only while that signature is still active.
        var signature = vm.SelectedSignature;
        editor.PropertyChanged += (_, change) =>
        {
            if (change.Property == RichHtmlEditor.HtmlProperty && ReferenceEquals(signature, vm.SelectedSignature)) vm.SignatureEditorHtml = editor.Html;
        };
        done.Click += async (_, _) => { await editor.CaptureAsync(); window.Close(); };
        void SelectionChanged(object? _, System.ComponentModel.PropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(vm.SelectedSignature) && !ReferenceEquals(signature, vm.SelectedSignature)) window.Close();
            else if (ReferenceEquals(signature, vm.SelectedSignature))
            {
                if (change.PropertyName == nameof(vm.SignatureEditorHtml)) editor.Html = vm.SignatureEditorHtml;
                if (change.PropertyName == nameof(vm.SignatureEditorName)) title.Text = vm.SignatureEditorName;
            }
        }
        vm.PropertyChanged += SelectionChanged;
        window.Closed += (_, _) => { vm.PropertyChanged -= SelectionChanged; _signatureWindow = null; };
        _signatureWindow = window;
        IndependentWindow.Show(window);
    }

    private void PreviewSignatureClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var preview = new NativeWebView { Source = vm.SelectedSignatureTemplatePreviewUri };
        preview.NavigationStarted += SignaturePreviewNavigationStarted;
        IndependentWindow.Show(new Window { Title = "Signature template preview", Width = 640, Height = 420, Content = preview });
    }

    private void SignaturePreviewNavigationStarted(
        object? sender,
        WebViewNavigationStartingEventArgs e)
    {
        if (e.Request?.Scheme is "http" or "https" or "mailto")
        {
            e.Cancel = true;
        }
    }

    private void OpenWorkspaceLinkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: Uri uri })
        {
            try
            {
                _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Settings remains usable when the operating system has no URL handler.
            }
        }
    }

    private async void CheckForUpdatesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow { CheckForUpdatesAsync: { } check })
        {
            await check();
        }
    }

    private async void CopyMcpValueClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button { DataContext: McpSettingsViewModel settings, CommandParameter: string value } &&
            TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(DataFormat.Text, value switch
            {
                "header" => "Bearer " + settings.AccessKey,
                "key" => settings.AccessKey,
                "public" => settings.PublicEndpointUrl,
                _ => settings.EndpointUrl
            });
        }
    }
}
