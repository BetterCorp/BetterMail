using Avalonia.Controls;
using Avalonia.Input;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class DrivePickerWindow : Window
{
    private readonly DriveWorkspaceViewModel _viewModel;

    public DrivePickerWindow()
    {
        InitializeComponent();
        _viewModel = null!;
    }

    public DrivePickerWindow(IFilesProvider provider, IReadOnlyList<MailAccount> accounts) : this()
    {
        _viewModel = new DriveWorkspaceViewModel(provider, accounts);
        _viewModel.ItemChosen += selection => Close(selection);
        DataContext = _viewModel;
        Opened += async (_, _) => await _viewModel.InitializeAsync();
    }

    private void CancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(null);

    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void FileDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_viewModel.ChooseItemCommand.CanExecute(null))
        {
            _viewModel.ChooseItemCommand.Execute(null);
            e.Handled = true;
        }
    }
}
