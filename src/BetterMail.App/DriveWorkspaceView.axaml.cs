using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class DriveWorkspaceView : UserControl
{
    private DriveWorkspaceViewModel? _loadedViewModel;
    private bool _layoutInitialized;
    private bool _isCompactLayout;
    private bool _isPhoneLayout;
    private bool _showPhoneTree = true;

    public DriveWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is DriveWorkspaceViewModel viewModel &&
                !ReferenceEquals(viewModel, _loadedViewModel))
            {
                if (_loadedViewModel is not null)
                {
                    _loadedViewModel.PropertyChanged -= ViewModelPropertyChanged;
                }
                _loadedViewModel = viewModel;
                viewModel.PropertyChanged += ViewModelPropertyChanged;
                viewModel.ConfigureFileTransfers(PickUploadAsync, PickDownloadAsync);
                await viewModel.InitializeAsync();
                _showPhoneTree = true;
                UpdatePhoneStage();
            }
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        KeyDown += HandleKeyDown;
    }

    internal static bool IsCompactWidth(double width) => width < 760;
    internal static bool IsPhoneWidth(double width) => width < 560;

    private void ApplyResponsiveLayout(double width)
    {
        var compact = IsCompactWidth(width);
        var phone = IsPhoneWidth(width);
        if (_layoutInitialized && _isCompactLayout == compact && _isPhoneLayout == phone)
        {
            return;
        }

        _layoutInitialized = true;
        _isCompactLayout = compact;
        _isPhoneLayout = phone;
        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();
        if (compact)
        {
            SearchGrid.ColumnDefinitions.Clear();
            SearchGrid.ColumnDefinitions.Add(new(GridLength.Star));
            SearchGrid.ColumnDefinitions.Add(new(GridLength.Auto));
            SearchGrid.ColumnDefinitions.Add(new(GridLength.Auto));
            SearchGrid.RowDefinitions.Clear();
            SearchGrid.RowDefinitions.Add(new(GridLength.Auto));
            SearchGrid.RowDefinitions.Add(new(GridLength.Auto));
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 0);
            Grid.SetRow(SearchButton, 0);
            Grid.SetColumn(SearchButton, 1);
            Grid.SetRow(ClearSearchButton, 0);
            Grid.SetColumn(ClearSearchButton, 2);
            Grid.SetRow(AccountFilter, 1);
            Grid.SetColumn(AccountFilter, 0);
            Grid.SetColumnSpan(AccountFilter, 3);
            AccountFilter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            AccountFilter.Margin = new Thickness(0, 6, 0, 0);
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(_isPhoneLayout
                ? GridLength.Star
                : new GridLength(210, GridUnitType.Pixel)));
            if (!_isPhoneLayout)
            {
                RootGrid.RowDefinitions.Add(new(GridLength.Star));
            }
            Grid.SetRow(DriveToolbar, 0);
            Grid.SetColumnSpan(DriveToolbar, 1);
            Grid.SetRow(IssueBar, 1);
            Grid.SetColumnSpan(IssueBar, 1);
            Grid.SetRow(DriveTreePane, 2);
            Grid.SetColumn(DriveTreePane, 0);
            DriveTreePane.BorderThickness = new Thickness(0, 0, 0, 1);
            Grid.SetRow(DriveContent, _isPhoneLayout ? 2 : 3);
            Grid.SetColumn(DriveContent, 0);
        }
        else
        {
            SearchGrid.ColumnDefinitions.Clear();
            SearchGrid.ColumnDefinitions.Add(new(GridLength.Star));
            SearchGrid.ColumnDefinitions.Add(new(GridLength.Auto));
            SearchGrid.ColumnDefinitions.Add(new(GridLength.Auto));
            SearchGrid.ColumnDefinitions.Add(new(GridLength.Auto));
            SearchGrid.RowDefinitions.Clear();
            SearchGrid.RowDefinitions.Add(new(GridLength.Auto));
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 0);
            Grid.SetRow(SearchButton, 0);
            Grid.SetColumn(SearchButton, 1);
            Grid.SetRow(ClearSearchButton, 0);
            Grid.SetColumn(ClearSearchButton, 2);
            Grid.SetRow(AccountFilter, 0);
            Grid.SetColumn(AccountFilter, 3);
            Grid.SetColumnSpan(AccountFilter, 1);
            AccountFilter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            AccountFilter.Margin = new Thickness(0);
            RootGrid.ColumnDefinitions.Add(new(new GridLength(260, GridUnitType.Pixel)));
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(DriveToolbar, 0);
            Grid.SetColumnSpan(DriveToolbar, 2);
            Grid.SetRow(IssueBar, 1);
            Grid.SetColumnSpan(IssueBar, 2);
            Grid.SetRow(DriveTreePane, 2);
            Grid.SetColumn(DriveTreePane, 0);
            DriveTreePane.BorderThickness = new Thickness(0, 0, 1, 0);
            Grid.SetRow(DriveContent, 2);
            Grid.SetColumn(DriveContent, 1);
        }
        UpdatePhoneStage();
    }

    private void ViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (_isPhoneLayout && args.PropertyName is nameof(DriveWorkspaceViewModel.SelectedDirectory)
            or nameof(DriveWorkspaceViewModel.IsSearchMode))
        {
            _showPhoneTree = false;
            UpdatePhoneStage();
        }
    }

    private void DriveBackClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _showPhoneTree = true;
        UpdatePhoneStage();
        DriveTree.Focus();
    }

    private void UpdatePhoneStage()
    {
        if (!IsInitialized)
        {
            return;
        }
        DriveTreePane.IsVisible = !_isPhoneLayout || _showPhoneTree;
        DriveContent.IsVisible = !_isPhoneLayout || !_showPhoneTree;
        DriveBackButton.IsVisible = _isPhoneLayout && !_showPhoneTree;
    }

    private async Task<DriveUploadSource?> PickUploadAsync(CancellationToken cancellationToken)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Upload to drive",
            AllowMultiple = false
        });
        var file = files.FirstOrDefault();
        if (file is null)
        {
            return null;
        }
        var stream = await file.OpenReadAsync();
        if (!stream.CanSeek)
        {
            await stream.DisposeAsync();
            throw new NotSupportedException("The selected file does not expose its length.");
        }
        return new DriveUploadSource(file.Name, stream, stream.Length);
    }

    private async Task<Stream?> PickDownloadAsync(
        CloudDriveItem file,
        CancellationToken cancellationToken)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }
        var destination = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save drive file",
            SuggestedFileName = file.Name
        });
        return destination is null ? null : await destination.OpenWriteAsync();
    }

    private void HandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not DriveWorkspaceViewModel viewModel)
        {
            return;
        }
        if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            args.Handled = true;
            return;
        }
        if (args.Source is TextBox or ComboBox)
        {
            return;
        }
        var command = args.Key switch
        {
            Key.F5 => viewModel.RefreshCommand,
            Key.Delete => viewModel.RequestDeleteCommand,
            Key.Enter => viewModel.OpenItemCommand,
            Key.U when args.KeyModifiers.HasFlag(KeyModifiers.Control) => viewModel.UploadCommand,
            _ => null
        };
        var parameter = args.Key == Key.Enter ? viewModel.SelectedItem : null;
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
            args.Handled = true;
        }
    }
}
