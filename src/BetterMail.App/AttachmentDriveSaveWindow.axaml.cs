using Avalonia.Controls;

namespace BetterMail.App;

public sealed partial class AttachmentDriveSaveWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();

    public AttachmentDriveSaveWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _lifetime.Cancel();
    }

    public AttachmentDriveSaveWindow(AttachmentDriveSaveViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) =>
        {
            try { await viewModel.Workspace.InitializeAsync(_lifetime.Token); }
            catch (OperationCanceledException) { }
        };
    }

    private async void SaveClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AttachmentDriveSaveViewModel viewModel)
            await viewModel.SaveAsync(_lifetime.Token);
    }

    private void CloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();
}
