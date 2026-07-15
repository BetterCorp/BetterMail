using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace BetterMail.App;

public sealed partial class CalendarWorkspaceView : UserControl
{
    private bool _layoutInitialized;
    private bool _isCompactWidth;
    private bool _isPhoneWidth;
    private bool _phoneShowingCalendars;

    public CalendarWorkspaceView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CalendarWorkspaceViewModel viewModel)
            {
                viewModel.SetViewportWidth(Bounds.Width);
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
            if (DataContext is CalendarWorkspaceViewModel existingViewModel)
            {
                existingViewModel.SetViewportWidth(width - (compact ? 0 : 238));
            }
            return;
        }

        if (phone && !_isPhoneWidth)
        {
            _phoneShowingCalendars = false;
        }
        _layoutInitialized = true;
        _isCompactWidth = compact;
        _isPhoneWidth = phone;
        RootGrid.ColumnDefinitions.Clear();
        RootGrid.RowDefinitions.Clear();
        if (phone)
        {
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(CalendarToolbar, 0);
            Grid.SetColumn(CalendarToolbar, 0);
            Grid.SetRow(CalendarSidebar, 1);
            Grid.SetRowSpan(CalendarSidebar, 1);
            Grid.SetColumn(CalendarSidebar, 0);
            CalendarSidebar.MaxHeight = double.PositiveInfinity;
            MiniCalendar.IsVisible = true;
            Grid.SetRow(CalendarContent, 1);
            Grid.SetColumn(CalendarContent, 0);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 2);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 1);
            EventEditor.Width = double.NaN;
            EventEditor.MaxWidth = 560;
        }
        else if (compact)
        {
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(CalendarToolbar, 0);
            Grid.SetColumn(CalendarToolbar, 0);
            Grid.SetRow(CalendarSidebar, 1);
            Grid.SetRowSpan(CalendarSidebar, 1);
            Grid.SetColumn(CalendarSidebar, 0);
            CalendarSidebar.MaxHeight = 220;
            MiniCalendar.IsVisible = false;
            Grid.SetRow(CalendarContent, 2);
            Grid.SetColumn(CalendarContent, 0);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 3);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 1);
            EventEditor.Width = double.NaN;
            EventEditor.MaxWidth = 560;
        }
        else
        {
            RootGrid.ColumnDefinitions.Add(new(new GridLength(238)));
            RootGrid.ColumnDefinitions.Add(new(GridLength.Star));
            RootGrid.RowDefinitions.Add(new(GridLength.Auto));
            RootGrid.RowDefinitions.Add(new(GridLength.Star));
            Grid.SetRow(CalendarSidebar, 0);
            Grid.SetRowSpan(CalendarSidebar, 2);
            Grid.SetColumn(CalendarSidebar, 0);
            CalendarSidebar.MaxHeight = double.PositiveInfinity;
            MiniCalendar.IsVisible = true;
            Grid.SetRow(CalendarToolbar, 0);
            Grid.SetColumn(CalendarToolbar, 1);
            Grid.SetRow(CalendarContent, 1);
            Grid.SetColumn(CalendarContent, 1);
            Grid.SetRow(EditorOverlay, 0);
            Grid.SetRowSpan(EditorOverlay, 2);
            Grid.SetColumn(EditorOverlay, 0);
            Grid.SetColumnSpan(EditorOverlay, 2);
            EventEditor.Width = 560;
        }
        CalendarPaneButton.IsVisible = phone && !_phoneShowingCalendars;
        CalendarSidebarBackButton.IsVisible = phone;
        CalendarSidebar.IsVisible = !phone || _phoneShowingCalendars;
        CalendarContent.IsVisible = !phone || !_phoneShowingCalendars;
        DayViewButton.IsVisible = !phone;
        WorkWeekViewButton.IsVisible = !phone;
        WeekViewButton.IsVisible = !phone;
        MonthViewButton.IsVisible = !phone;
        if (DataContext is CalendarWorkspaceViewModel viewModel)
        {
            if (phone && viewModel.ViewMode != CalendarViewMode.Day && viewModel.DayCommand.CanExecute(null))
            {
                viewModel.DayCommand.Execute(null);
            }
            viewModel.SetViewportWidth(width - (compact ? 0 : 238));
        }
    }

    private void CalendarPaneButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _phoneShowingCalendars = true;
        ApplyResponsiveLayout(Bounds.Width, force: true);
        CalendarSidebarBackButton.Focus();
    }

    private void CalendarSidebarBackButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        ShowPhoneCalendar();
    }

    private void ShowPhoneCalendar()
    {
        _phoneShowingCalendars = false;
        ApplyResponsiveLayout(Bounds.Width, force: true);
        CalendarPaneButton.Focus();
    }

    private void HandleKeyDown(object? sender, KeyEventArgs args)
    {
        if (_isPhoneWidth && _phoneShowingCalendars && args.Key == Key.Escape)
        {
            ShowPhoneCalendar();
            args.Handled = true;
            return;
        }
        if (DataContext is not CalendarWorkspaceViewModel viewModel ||
            args.Source is TextBox or NumericUpDown or CalendarDatePicker or TimePicker)
        {
            return;
        }
        var command = args.Key switch
        {
            Key.N when args.KeyModifiers.HasFlag(KeyModifiers.Control) => viewModel.NewEventCommand,
            Key.T => viewModel.TodayCommand,
            Key.Left => viewModel.PreviousCommand,
            Key.Right => viewModel.NextCommand,
            Key.D => viewModel.DayCommand,
            Key.W => viewModel.WeekCommand,
            Key.M => viewModel.MonthCommand,
            _ => null
        };
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            args.Handled = true;
        }
    }

    private void CalendarEventDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is not Button { DataContext: CalendarEventItem item })
        {
            return;
        }
        if (DataContext is CalendarWorkspaceViewModel viewModel && viewModel.CloseEditorCommand.CanExecute(null))
        {
            viewModel.CloseEditorCommand.Execute(null);
        }

        var calendarEvent = item.Source.Event;
        var attendees = string.Join(", ", (calendarEvent.Attendees ?? [])
            .Select(static attendee => attendee.Address.ToString()));
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = calendarEvent.Subject,
            FontSize = 24,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock { Text = calendarEvent.TimeText, Opacity = 0.72 });
        content.Children.Add(new TextBlock { Text = item.CalendarIdentity, Opacity = 0.62 });
        if (!string.IsNullOrWhiteSpace(calendarEvent.Location))
        {
            content.Children.Add(new TextBlock { Text = $"Location: {calendarEvent.Location}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }
        if (!string.IsNullOrWhiteSpace(attendees))
        {
            content.Children.Add(new TextBlock { Text = $"Attendees: {attendees}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        }
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            new Window
            {
                Title = calendarEvent.Subject,
                Icon = owner.Icon,
                Width = 560,
                Height = 360,
                MinWidth = 360,
                MinHeight = 240,
                Content = new ScrollViewer { Content = content }
            }.Show(owner);
        }
        args.Handled = true;
    }
}
