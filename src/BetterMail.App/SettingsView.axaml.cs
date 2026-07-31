using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;

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
}
