using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace BetterMail.App;

public sealed partial class TasksWorkspaceView : UserControl
{
    private TasksWorkspaceViewModel? _loadedViewModel;
    private bool _layoutInitialized;
    private bool _isCompactWidth;
    private bool _isPhoneWidth;
    private bool _phoneShowingTasks;

    public TasksWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (DataContext is TasksWorkspaceViewModel viewModel &&
                !ReferenceEquals(viewModel, _loadedViewModel))
            {
                _loadedViewModel = viewModel;
                await viewModel.InitializeAsync();
            }
        };
        SizeChanged += (_, args) => ApplyResponsiveLayout(args.NewSize.Width);
        KeyDown += HandleKeyDown;
    }

    internal static bool IsCompactWidth(double width) => width < 760;
    internal static bool IsPhoneWidth(double width) => width < 560;

    private void ApplyResponsiveLayout(double width, bool force = false)
    {
        var compact = IsCompactWidth(width);
        var phone = IsPhoneWidth(width);
        if (!force && _layoutInitialized && _isCompactWidth == compact && _isPhoneWidth == phone)
        {
            return;
        }

        if (phone && !_isPhoneWidth)
        {
            _phoneShowingTasks = _loadedViewModel?.SelectedList is not null;
        }
        _layoutInitialized = true;
        _isCompactWidth = compact;
        _isPhoneWidth = phone;
        SetToolbarLayout(compact);
        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();
        if (phone)
        {
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(TasksToolbar, 0);
            Grid.SetColumn(TasksToolbar, 0);
            Grid.SetColumnSpan(TasksToolbar, 1);
            Grid.SetRow(ListsPane, 1);
            Grid.SetColumn(ListsPane, 0);
            ListsPane.BorderThickness = new Thickness(0);
            Grid.SetRow(TasksPane, 1);
            Grid.SetColumn(TasksPane, 0);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 2);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 1);
            TaskEditor.Width = double.NaN;
            TaskEditor.Margin = new Thickness(12);
        }
        else if (compact)
        {
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(new GridLength(220)));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(TasksToolbar, 0);
            Grid.SetColumn(TasksToolbar, 0);
            Grid.SetColumnSpan(TasksToolbar, 1);
            Grid.SetRow(ListsPane, 1);
            Grid.SetColumn(ListsPane, 0);
            ListsPane.BorderThickness = new Thickness(0, 0, 0, 1);
            Grid.SetRow(TasksPane, 2);
            Grid.SetColumn(TasksPane, 0);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 3);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 1);
            TaskEditor.Width = double.NaN;
            TaskEditor.Margin = new Thickness(12);
        }
        else
        {
            RootGrid.ColumnDefinitions.Add(new(new GridLength(270)));
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(TasksToolbar, 0);
            Grid.SetColumn(TasksToolbar, 0);
            Grid.SetColumnSpan(TasksToolbar, 2);
            Grid.SetRow(ListsPane, 1);
            Grid.SetColumn(ListsPane, 0);
            ListsPane.BorderThickness = new Thickness(0, 0, 1, 0);
            Grid.SetRow(TasksPane, 1);
            Grid.SetColumn(TasksPane, 1);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 2);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 2);
            TaskEditor.Width = 520;
            TaskEditor.Margin = new Thickness(0);
        }
        TasksBackButton.IsVisible = phone;
        ListsPane.IsVisible = !phone || !_phoneShowingTasks;
        TasksPane.IsVisible = !phone || _phoneShowingTasks;
    }

    private void ShowTasksPane_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        if (!_isPhoneWidth)
        {
            return;
        }
        _phoneShowingTasks = true;
        ApplyResponsiveLayout(Bounds.Width, force: true);
        TasksBackButton.Focus();
    }

    private void TasksBackButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args) =>
        ShowPhoneLists();

    private void ShowPhoneLists()
    {
        _phoneShowingTasks = false;
        ApplyResponsiveLayout(Bounds.Width, force: true);
        AllTasksButton.Focus();
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
            Grid.SetRow(NewTaskButton, 1);
            Grid.SetColumn(NewTaskButton, 0);
            Grid.SetRow(RefreshButton, 1);
            Grid.SetColumn(RefreshButton, 2);
            return;
        }
        ToolbarLayout.ColumnDefinitions.Add(new(GridLength.Star));
        for (var index = 0; index < 3; index++)
        {
            ToolbarLayout.ColumnDefinitions.Add(new(GridLength.Auto));
        }
        Grid.SetRow(SearchBox, 0);
        Grid.SetColumn(SearchBox, 0);
        Grid.SetColumnSpan(SearchBox, 1);
        Grid.SetRow(SearchButton, 0);
        Grid.SetColumn(SearchButton, 1);
        Grid.SetRow(NewTaskButton, 0);
        Grid.SetColumn(NewTaskButton, 2);
        Grid.SetRow(RefreshButton, 0);
        Grid.SetColumn(RefreshButton, 3);
    }

    private void HandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (DataContext is not TasksWorkspaceViewModel viewModel)
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
        if (args.Source is TextBox or CalendarDatePicker or TimePicker)
        {
            return;
        }
        var command = args.Key switch
        {
            Key.N when args.KeyModifiers.HasFlag(KeyModifiers.Control) => viewModel.NewTaskCommand,
            Key.F5 => viewModel.RefreshCommand,
            Key.Delete => viewModel.RequestDeleteCommand,
            Key.Space when viewModel.SelectedTask is not null => viewModel.ToggleCompleteCommand,
            Key.Escape when viewModel.IsDeleteConfirmationOpen => viewModel.CancelDeleteCommand,
            Key.Escape when viewModel.IsEditorOpen => viewModel.CloseEditorCommand,
            _ => null
        };
        var parameter = args.Key == Key.Space ? viewModel.SelectedTask : null;
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
            args.Handled = true;
            return;
        }
        if (_isPhoneWidth && _phoneShowingTasks && args.Key == Key.Escape)
        {
            ShowPhoneLists();
            args.Handled = true;
        }
    }
}
