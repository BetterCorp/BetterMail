using System.Diagnostics;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace BetterMail.App;

public sealed partial class NotesWorkspaceView : UserControl
{
    private NotesWorkspaceViewModel? _viewModel;
    private bool _isPhoneWidth;
    private bool _phoneShowingPage;

    public NotesWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            _viewModel = DataContext as NotesWorkspaceViewModel;
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            }
            ApplyResponsiveLayout(Bounds.Width);
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
        if (phone && !_isPhoneWidth)
        {
            _phoneShowingPage = _viewModel?.SelectedPage is not null;
        }
        _isPhoneWidth = phone;
        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();
        if (phone)
        {
            SetToolbarLayout(compact: true);
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(NotesToolbar, 0);
            Grid.SetColumn(NotesToolbar, 0);
            Grid.SetColumnSpan(NotesToolbar, 1);
            Grid.SetRow(NavigationPane, 1);
            Grid.SetColumn(NavigationPane, 0);
            NavigationPane.BorderThickness = new Thickness(0);
            Grid.SetRow(ReadingPane, 1);
            Grid.SetColumn(ReadingPane, 0);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 2);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 1);
            PageEditor.Width = double.NaN;
            PageEditor.Margin = new Thickness(12);
        }
        else if (compact)
        {
            SetToolbarLayout(compact: true);
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(new GridLength(240)));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(NotesToolbar, 0);
            Grid.SetColumn(NotesToolbar, 0);
            Grid.SetColumnSpan(NotesToolbar, 1);
            Grid.SetRow(NavigationPane, 1);
            Grid.SetColumn(NavigationPane, 0);
            NavigationPane.BorderThickness = new Thickness(0, 0, 0, 1);
            Grid.SetRow(ReadingPane, 2);
            Grid.SetColumn(ReadingPane, 0);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 3);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 1);
            PageEditor.Width = double.NaN;
            PageEditor.Margin = new Thickness(12);
        }
        else
        {
            SetToolbarLayout(compact: false);
            RootGrid.ColumnDefinitions.Add(new(new GridLength(300)));
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(NotesToolbar, 0);
            Grid.SetColumn(NotesToolbar, 0);
            Grid.SetColumnSpan(NotesToolbar, 2);
            Grid.SetRow(NavigationPane, 1);
            Grid.SetColumn(NavigationPane, 0);
            NavigationPane.BorderThickness = new Thickness(0, 0, 1, 0);
            Grid.SetRow(ReadingPane, 1);
            Grid.SetColumn(ReadingPane, 1);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 2);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 2);
            PageEditor.Width = 560;
            PageEditor.Margin = new Thickness(0);
        }
        NotesBackButton.IsVisible = phone;
        NavigationPane.IsVisible = !phone || !_phoneShowingPage;
        ReadingPane.IsVisible = !phone || _phoneShowingPage;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_isPhoneWidth && args.PropertyName == nameof(NotesWorkspaceViewModel.SelectedNode) &&
            _viewModel?.SelectedPage is not null)
        {
            _phoneShowingPage = true;
            ApplyResponsiveLayout(Bounds.Width);
            NotesBackButton.Focus();
        }
    }

    private void NotesBackButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        ShowPhoneNavigation();

    private void ShowPhoneNavigation()
    {
        _phoneShowingPage = false;
        ApplyResponsiveLayout(Bounds.Width);
        NotesTree.Focus();
    }

    private void SetToolbarLayout(bool compact)
    {
        ToolbarLayout.ColumnDefinitions.Clear();
        ToolbarLayout.RowDefinitions.Clear();
        ToolbarLayout.RowDefinitions.Add(new(GridLength.Auto));
        if (compact)
        {
            ToolbarLayout.RowDefinitions.Add(new(GridLength.Auto));
            ToolbarLayout.ColumnDefinitions.Add(new(GridLength.Star));
            ToolbarLayout.ColumnDefinitions.Add(new(GridLength.Auto));
            ToolbarLayout.ColumnDefinitions.Add(new(GridLength.Auto));
            Grid.SetRow(SearchBox, 0);
            Grid.SetColumn(SearchBox, 0);
            Grid.SetColumnSpan(SearchBox, 2);
            Grid.SetRow(SearchButton, 0);
            Grid.SetColumn(SearchButton, 2);
            Grid.SetRow(NewPageButton, 1);
            Grid.SetColumn(NewPageButton, 0);
            Grid.SetRow(EditPageButton, 1);
            Grid.SetColumn(EditPageButton, 1);
            Grid.SetRow(RefreshButton, 1);
            Grid.SetColumn(RefreshButton, 2);
            return;
        }

        ToolbarLayout.ColumnDefinitions.Add(new(GridLength.Star));
        for (var index = 0; index < 4; index++)
        {
            ToolbarLayout.ColumnDefinitions.Add(new(GridLength.Auto));
        }
        Grid.SetRow(SearchBox, 0);
        Grid.SetColumn(SearchBox, 0);
        Grid.SetColumnSpan(SearchBox, 1);
        Grid.SetRow(SearchButton, 0);
        Grid.SetColumn(SearchButton, 1);
        Grid.SetRow(NewPageButton, 0);
        Grid.SetColumn(NewPageButton, 2);
        Grid.SetRow(EditPageButton, 0);
        Grid.SetColumn(EditPageButton, 3);
        Grid.SetRow(RefreshButton, 0);
        Grid.SetColumn(RefreshButton, 4);
    }

    private void HandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not NotesWorkspaceViewModel viewModel)
        {
            return;
        }
        if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            args.Handled = true;
            return;
        }
        if (args.Source is TextBox)
        {
            return;
        }
        var command = args.Key switch
        {
            Key.N when args.KeyModifiers.HasFlag(KeyModifiers.Control) => viewModel.NewPageCommand,
            Key.F5 => viewModel.RefreshCommand,
            Key.Delete => viewModel.RequestDeleteCommand,
            Key.Escape when viewModel.IsDeleteConfirmationOpen => viewModel.CancelDeleteCommand,
            Key.Escape when viewModel.IsEditorOpen => viewModel.CloseEditorCommand,
            _ => null
        };
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            args.Handled = true;
            return;
        }
        if (_isPhoneWidth && _phoneShowingPage && args.Key == Key.Escape)
        {
            ShowPhoneNavigation();
            args.Handled = true;
        }
    }

    private void PageWebView_NavigationStarted(
        object? sender, WebViewNavigationStartingEventArgs args)
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
