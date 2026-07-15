using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public enum CalendarViewMode
{
    Day,
    WorkWeek,
    Week,
    Month
}

public sealed class CalendarWorkspaceViewModel : ViewModelBase
{
    private static readonly string[] Palette =
        ["#0F6CBD", "#8764B8", "#038387", "#C239B3", "#CA5010", "#498205", "#D13438", "#8E562E"];

    private readonly ICalendarProvider _provider;
    private IReadOnlyList<MailAccount> _accounts;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<CalendarEventSource> _events = [];
    private readonly List<string> _calendarIssues = [];
    private DateTimeOffset _selectedDate;
    private CalendarViewMode _viewMode = CalendarViewMode.WorkWeek;
    private CalendarEventItem? _editingEvent;
    private EditorCalendarOption? _selectedEditorCalendar;
    private bool _initialized;
    private bool _isLoading;
    private bool _isEditorOpen;
    private string? _error;
    private string? _editorError;
    private string _editorSubject = "";
    private string _editorLocation = "";
    private string _editorAttendees = "";
    private DateTime? _editorStartDate;
    private DateTime? _editorEndDate;
    private TimeSpan? _editorStartTime;
    private TimeSpan? _editorEndTime;
    private bool _editorReminderOn = true;
    private int _editorReminderMinutes = 15;
    private bool _editorRecurs;
    private CalendarRecurrencePatternType _editorPattern = CalendarRecurrencePatternType.Weekly;
    private int _editorInterval = 1;
    private CalendarRecurrenceRangeType _editorRange = CalendarRecurrenceRangeType.NoEnd;
    private DateTime? _editorRecurrenceEndDate;
    private int _editorOccurrences = 10;
    private string _editorDaysOfWeek = "";
    private int _editorDayOfMonth = 1;
    private CalendarRecurrenceIndex _editorRecurrenceIndex = CalendarRecurrenceIndex.First;
    private int _editorMonth = 1;
    private DayOfWeek _editorFirstDayOfWeek = DayOfWeek.Monday;
    private double _viewportWidth = 1200;

    public CalendarWorkspaceViewModel(
        ICalendarProvider provider,
        IReadOnlyList<MailAccount> accounts,
        Func<DateTimeOffset>? now = null)
    {
        _provider = provider;
        _accounts = accounts;
        _now = now ?? (() => DateTimeOffset.Now);
        _selectedDate = _now();

        TodayCommand = new AsyncCommand(() => NavigateAsync(_now()));
        PreviousCommand = new AsyncCommand(() => NavigateAsync(ShiftDate(-1)));
        NextCommand = new AsyncCommand(() => NavigateAsync(ShiftDate(1)));
        RefreshCommand = new AsyncCommand(RefreshEventsAsync, () => !IsLoading && CalendarGroups.Count > 0);
        DayCommand = new AsyncCommand(() => SetModeAsync(CalendarViewMode.Day));
        WorkWeekCommand = new AsyncCommand(() => SetModeAsync(CalendarViewMode.WorkWeek));
        WeekCommand = new AsyncCommand(() => SetModeAsync(CalendarViewMode.Week));
        MonthCommand = new AsyncCommand(() => SetModeAsync(CalendarViewMode.Month));
        NewEventCommand = new AsyncCommand(OpenNewEventAsync, () => EditableCalendars.Count > 0);
        EditEventCommand = new AsyncCommand<CalendarEventItem>(OpenEditEventAsync);
        SaveEventCommand = new AsyncCommand(SaveEventAsync, () => SelectedEditorCalendar is not null);
        DeleteEventCommand = new AsyncCommand(DeleteEventAsync, () => _editingEvent is not null);
        CloseEditorCommand = new AsyncCommand(CloseEditorAsync);
    }

    public ObservableCollection<CalendarAccountGroup> CalendarGroups { get; } = [];
    public ObservableCollection<CalendarDayColumn> DayColumns { get; } = [];
    public ObservableCollection<CalendarMonthCell> MonthCells { get; } = [];
    public ObservableCollection<string> LoadIssues { get; } = [];
    public ObservableCollection<EditorCalendarOption> EditableCalendars { get; } = [];
    public IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToArray();
    public IReadOnlyList<CalendarRecurrencePatternType> RecurrencePatterns { get; } = Enum.GetValues<CalendarRecurrencePatternType>();
    public IReadOnlyList<CalendarRecurrenceRangeType> RecurrenceRanges { get; } = Enum.GetValues<CalendarRecurrenceRangeType>();
    public IReadOnlyList<CalendarRecurrenceIndex> RecurrenceIndexes { get; } = Enum.GetValues<CalendarRecurrenceIndex>();
    public IReadOnlyList<DayOfWeek> WeekDays { get; } = Enum.GetValues<DayOfWeek>();
    public ICommand TodayCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand DayCommand { get; }
    public ICommand WorkWeekCommand { get; }
    public ICommand WeekCommand { get; }
    public ICommand MonthCommand { get; }
    public ICommand NewEventCommand { get; }
    public ICommand EditEventCommand { get; }
    public ICommand SaveEventCommand { get; }
    public ICommand DeleteEventCommand { get; }
    public ICommand CloseEditorCommand { get; }

    public DateTimeOffset SelectedDate
    {
        get => _selectedDate;
        set
        {
            if (SetProperty(ref _selectedDate, value))
            {
                RaisePropertyChanged(nameof(CalendarSelectedDate));
                RaisePropertyChanged(nameof(DateTitle));
                if (_initialized)
                {
                    _ = RefreshEventsAsync();
                }
            }
        }
    }

    public DateTime? CalendarSelectedDate
    {
        get => SelectedDate.ToLocalTime().Date;
        set
        {
            if (value is { } date)
            {
                SelectedDate = new DateTimeOffset(DateTime.SpecifyKind(date.Date, DateTimeKind.Local));
            }
        }
    }

    public CalendarViewMode ViewMode
    {
        get => _viewMode;
        private set
        {
            if (SetProperty(ref _viewMode, value))
            {
                RaisePropertyChanged(nameof(IsMonthView));
                RaisePropertyChanged(nameof(IsTimelineView));
                RaisePropertyChanged(nameof(DateTitle));
            }
        }
    }

    public bool IsMonthView => ViewMode == CalendarViewMode.Month;
    public bool IsTimelineView => !IsMonthView;
    public string DateTitle => ViewMode switch
    {
        CalendarViewMode.Day => SelectedDate.ToString("dddd, MMMM d, yyyy"),
        CalendarViewMode.Month => SelectedDate.ToString("MMMM yyyy"),
        _ => $"{Range().Start:MMM d} – {Range().End.AddDays(-1):MMM d, yyyy}"
    };

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                ((AsyncCommand)RefreshCommand).Refresh();
            }
        }
    }

    public bool IsEditorOpen { get => _isEditorOpen; private set => SetProperty(ref _isEditorOpen, value); }
    public bool IsEditing => _editingEvent is not null;
    public string? Error { get => _error; private set { if (SetProperty(ref _error, value)) RaisePropertyChanged(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public string? EditorError { get => _editorError; private set { if (SetProperty(ref _editorError, value)) RaisePropertyChanged(nameof(HasEditorError)); } }
    public bool HasEditorError => !string.IsNullOrWhiteSpace(EditorError);
    public string EditorTitle => IsEditing ? "Edit event" : "New event";
    public string EditorAvailabilityText => _editingEvent?.AvailabilityText ?? "Busy";
    public EditorCalendarOption? SelectedEditorCalendar
    {
        get => _selectedEditorCalendar;
        set
        {
            if (SetProperty(ref _selectedEditorCalendar, value))
            {
                ((AsyncCommand)SaveEventCommand).Refresh();
            }
        }
    }
    public string EditorSubject { get => _editorSubject; set => SetProperty(ref _editorSubject, value); }
    public string EditorLocation { get => _editorLocation; set => SetProperty(ref _editorLocation, value); }
    public string EditorAttendees { get => _editorAttendees; set => SetProperty(ref _editorAttendees, value); }
    public DateTime? EditorStartDate { get => _editorStartDate; set => SetProperty(ref _editorStartDate, value); }
    public DateTime? EditorEndDate { get => _editorEndDate; set => SetProperty(ref _editorEndDate, value); }
    public TimeSpan? EditorStartTime { get => _editorStartTime; set => SetProperty(ref _editorStartTime, value); }
    public TimeSpan? EditorEndTime { get => _editorEndTime; set => SetProperty(ref _editorEndTime, value); }
    public bool EditorReminderOn { get => _editorReminderOn; set => SetProperty(ref _editorReminderOn, value); }
    public int EditorReminderMinutes { get => _editorReminderMinutes; set => SetProperty(ref _editorReminderMinutes, value); }
    public bool EditorRecurs { get => _editorRecurs; set => SetProperty(ref _editorRecurs, value); }
    public CalendarRecurrencePatternType EditorPattern { get => _editorPattern; set => SetProperty(ref _editorPattern, value); }
    public int EditorInterval { get => _editorInterval; set => SetProperty(ref _editorInterval, value); }
    public CalendarRecurrenceRangeType EditorRange { get => _editorRange; set => SetProperty(ref _editorRange, value); }
    public DateTime? EditorRecurrenceEndDate { get => _editorRecurrenceEndDate; set => SetProperty(ref _editorRecurrenceEndDate, value); }
    public int EditorOccurrences { get => _editorOccurrences; set => SetProperty(ref _editorOccurrences, value); }
    public string EditorDaysOfWeek { get => _editorDaysOfWeek; set => SetProperty(ref _editorDaysOfWeek, value); }
    public int EditorDayOfMonth { get => _editorDayOfMonth; set => SetProperty(ref _editorDayOfMonth, value); }
    public CalendarRecurrenceIndex EditorRecurrenceIndex { get => _editorRecurrenceIndex; set => SetProperty(ref _editorRecurrenceIndex, value); }
    public int EditorMonth { get => _editorMonth; set => SetProperty(ref _editorMonth, value); }
    public DayOfWeek EditorFirstDayOfWeek { get => _editorFirstDayOfWeek; set => SetProperty(ref _editorFirstDayOfWeek, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        CalendarGroups.Clear();
        EditableCalendars.Clear();
        LoadIssues.Clear();
        _calendarIssues.Clear();
        var results = await Task.WhenAll(_accounts.Select((account, index) =>
            LoadAccountAsync(account, index, cancellationToken)));
        foreach (var result in results)
        {
            CalendarGroups.Add(result.Group);
            if (result.Error is not null)
            {
                _calendarIssues.Add(result.Error);
            }
            foreach (var calendar in result.Group.Calendars.Where(static item => item.Info.CanEdit))
            {
                EditableCalendars.Add(new(result.Group.Account, calendar));
            }
        }
        _initialized = true;
        ((AsyncCommand)NewEventCommand).Refresh();
        await RefreshEventsAsync(cancellationToken);
    }

    public async Task UpdateAccountsAsync(
        IReadOnlyList<MailAccount> accounts,
        CancellationToken cancellationToken = default)
    {
        _accounts = accounts.ToArray();
        await InitializeAsync(cancellationToken);
    }

    public void SetViewportWidth(double width)
    {
        _viewportWidth = Math.Max(320, width);
        RebuildLayout();
    }

    private async Task<AccountLoadResult> LoadAccountAsync(
        MailAccount account,
        int accountIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            var calendars = await _provider.GetCalendarsAsync(account, cancellationToken);
            var choices = calendars.Select((calendar, index) => new CalendarChoice(
                calendar,
                ResolveColor(calendar.Color, accountIndex + index),
                RebuildLayout)).ToArray();
            return new(new(account, choices), null);
        }
        catch (Exception ex)
        {
            return new(new(account, []), $"{account.EmailAddress}: calendars could not be loaded ({ex.Message})");
        }
    }

    private async Task RefreshEventsAsync() => await RefreshEventsAsync(CancellationToken.None);

    private async Task RefreshEventsAsync(CancellationToken cancellationToken)
    {
        if (!_initialized || IsLoading)
        {
            return;
        }

        IsLoading = true;
        Error = null;
        LoadIssues.Clear();
        foreach (var issue in _calendarIssues)
        {
            LoadIssues.Add(issue);
        }
        try
        {
            var range = Range();
            var calendars = CalendarGroups
                .SelectMany(group => group.Calendars.Select(calendar => (group.Account, Calendar: calendar)))
                .ToArray();
            var results = await Task.WhenAll(calendars.Select(item =>
                LoadEventsAsync(item.Account, item.Calendar, range.Start, range.End, cancellationToken)));
            _events.Clear();
            foreach (var result in results)
            {
                if (result.Error is not null)
                {
                    LoadIssues.Add(result.Error);
                }
                else
                {
                    _events.AddRange(result.Events);
                }
            }
            RebuildLayout();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<EventLoadResult> LoadEventsAsync(
        MailAccount account,
        CalendarChoice calendar,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            var events = await _provider.GetEventsAsync(
                account, calendar.Info.ProviderId, from, to, cancellationToken);
            return new(events.Select(item => new CalendarEventSource(account, calendar, item)).ToArray(), null);
        }
        catch (Exception ex)
        {
            return new([], $"{account.EmailAddress} / {calendar.Info.Name}: {ex.Message}");
        }
    }

    private async Task NavigateAsync(DateTimeOffset date)
    {
        _selectedDate = date;
        RaisePropertyChanged(nameof(SelectedDate));
        RaisePropertyChanged(nameof(CalendarSelectedDate));
        RaisePropertyChanged(nameof(DateTitle));
        await RefreshEventsAsync();
    }

    private DateTimeOffset ShiftDate(int direction) => ViewMode switch
    {
        CalendarViewMode.Day => SelectedDate.AddDays(direction),
        CalendarViewMode.WorkWeek => SelectedDate.AddDays(7 * direction),
        CalendarViewMode.Week => SelectedDate.AddDays(7 * direction),
        CalendarViewMode.Month => SelectedDate.AddMonths(direction),
        _ => SelectedDate
    };

    private async Task SetModeAsync(CalendarViewMode mode)
    {
        ViewMode = mode;
        await RefreshEventsAsync();
    }

    private (DateTimeOffset Start, DateTimeOffset End) Range()
    {
        var day = StartOfDay(SelectedDate);
        return ViewMode switch
        {
            CalendarViewMode.Day => (day, day.AddDays(1)),
            CalendarViewMode.WorkWeek => (StartOfWeek(day), StartOfWeek(day).AddDays(5)),
            CalendarViewMode.Week => (StartOfWeek(day), StartOfWeek(day).AddDays(7)),
            CalendarViewMode.Month => MonthRange(day),
            _ => (day, day.AddDays(1))
        };
    }

    private static (DateTimeOffset Start, DateTimeOffset End) MonthRange(DateTimeOffset date)
    {
        var first = new DateTimeOffset(date.Year, date.Month, 1, 0, 0, 0, date.Offset);
        var start = StartOfWeek(first);
        return (start, start.AddDays(42));
    }

    private static DateTimeOffset StartOfDay(DateTimeOffset date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, date.Offset);

    private static DateTimeOffset StartOfWeek(DateTimeOffset date)
    {
        var offset = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return StartOfDay(date).AddDays(-offset);
    }

    private void RebuildLayout()
    {
        if (!_initialized)
        {
            return;
        }

        var visible = _events.Where(static item => item.Calendar.IsVisible).ToArray();
        var range = Range();
        if (ViewMode == CalendarViewMode.Month)
        {
            DayColumns.Clear();
            Replace(MonthCells, Enumerable.Range(0, 42).Select(index =>
            {
                var day = range.Start.AddDays(index);
                return new CalendarMonthCell(
                    day,
                    day.Month == SelectedDate.Month,
                    visible.Where(item => OccursOn(item.Event, day))
                        .OrderBy(item => item.Event.StartsAt)
                        .Select(CalendarEventItem.ForMonth)
                        .ToArray());
            }));
            return;
        }

        MonthCells.Clear();
        var days = ViewMode == CalendarViewMode.Day ? 1 : ViewMode == CalendarViewMode.WorkWeek ? 5 : 7;
        var dayWidth = days == 1
            ? Math.Max(300, _viewportWidth - 90)
            : Math.Max(180, (_viewportWidth - 90) / Math.Min(days, 5));
        Replace(DayColumns, Enumerable.Range(0, days).Select(index =>
        {
            var day = range.Start.AddDays(index);
            var sources = visible.Where(item => OccursOn(item.Event, day))
                .OrderBy(item => item.Event.StartsAt)
                .ThenBy(item => item.Event.ProviderId, StringComparer.Ordinal)
                .ToArray();
            var items = sources.Select(source => BuildTimelineItem(source, day, sources, dayWidth)).ToArray();
            return new CalendarDayColumn(day, dayWidth, items);
        }));
    }

    private static CalendarEventItem BuildTimelineItem(
        CalendarEventSource source,
        DateTimeOffset day,
        IReadOnlyList<CalendarEventSource> dayEvents,
        double dayWidth)
    {
        var dayEnd = day.AddDays(1);
        var start = source.Event.StartsAt < day ? day : source.Event.StartsAt;
        var end = source.Event.EndsAt > dayEnd ? dayEnd : source.Event.EndsAt;
        var overlaps = dayEvents.Where(other =>
                other.Event.StartsAt < source.Event.EndsAt && other.Event.EndsAt > source.Event.StartsAt)
            .OrderBy(other => other.Event.StartsAt)
            .ThenBy(other => other.Event.ProviderId, StringComparer.Ordinal)
            .ToArray();
        var column = Array.IndexOf(overlaps, source);
        var count = Math.Max(1, overlaps.Length);
        var width = Math.Max(52, (dayWidth - 12) / count);
        return new(
            source,
            Math.Max(0, (start - day).TotalMinutes * 0.8),
            Math.Max(24, (end - start).TotalMinutes * 0.8),
            5 + Math.Max(0, column) * width,
            width - 4);
    }

    private static bool OccursOn(CalendarEvent calendarEvent, DateTimeOffset day) =>
        calendarEvent.StartsAt < day.AddDays(1) && calendarEvent.EndsAt > day;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private Task OpenNewEventAsync()
    {
        _editingEvent = null;
        SelectedEditorCalendar = EditableCalendars.FirstOrDefault();
        var start = SelectedDate.Date == _now().Date ? _now().AddMinutes(30) : StartOfDay(SelectedDate).AddHours(9);
        start = new DateTimeOffset(start.Year, start.Month, start.Day, start.Hour, start.Minute / 30 * 30, 0, start.Offset);
        EditorSubject = "";
        EditorLocation = "";
        EditorAttendees = "";
        SetEditorTimes(start, start.AddHours(1));
        EditorReminderOn = true;
        EditorReminderMinutes = 15;
        SetRecurrence(null);
        EditorError = null;
        IsEditorOpen = true;
        RaisePropertyChanged(nameof(IsEditing));
        RaisePropertyChanged(nameof(EditorTitle));
        RaisePropertyChanged(nameof(EditorAvailabilityText));
        ((AsyncCommand)DeleteEventCommand).Refresh();
        return Task.CompletedTask;
    }

    private Task OpenEditEventAsync(CalendarEventItem item)
    {
        _editingEvent = item;
        SelectedEditorCalendar = EditableCalendars.FirstOrDefault(option =>
            option.Account.AccountId == item.Source.Account.AccountId &&
            option.Calendar.Info.ProviderId == item.Source.Calendar.Info.ProviderId);
        EditorSubject = item.Source.Event.Subject;
        EditorLocation = item.Source.Event.Location ?? "";
        EditorAttendees = string.Join("; ", (item.Source.Event.Attendees ?? []).Select(attendee => attendee.Address.Address));
        SetEditorTimes(item.Source.Event.StartsAt, item.Source.Event.EndsAt);
        EditorReminderOn = item.Source.Event.IsReminderOn;
        EditorReminderMinutes = item.Source.Event.ReminderMinutesBeforeStart;
        SetRecurrence(item.Source.Event.Recurrence);
        EditorError = null;
        IsEditorOpen = true;
        RaisePropertyChanged(nameof(IsEditing));
        RaisePropertyChanged(nameof(EditorTitle));
        RaisePropertyChanged(nameof(EditorAvailabilityText));
        ((AsyncCommand)DeleteEventCommand).Refresh();
        return Task.CompletedTask;
    }

    private void SetEditorTimes(DateTimeOffset start, DateTimeOffset end)
    {
        start = start.ToLocalTime();
        end = end.ToLocalTime();
        EditorStartDate = start.Date;
        EditorEndDate = end.Date;
        EditorStartTime = start.TimeOfDay;
        EditorEndTime = end.TimeOfDay;
    }

    private void SetRecurrence(CalendarRecurrence? recurrence)
    {
        EditorRecurs = recurrence is not null;
        EditorPattern = recurrence?.PatternType ?? CalendarRecurrencePatternType.Weekly;
        EditorInterval = recurrence?.Interval ?? 1;
        EditorRange = recurrence?.RangeType ?? CalendarRecurrenceRangeType.NoEnd;
        EditorRecurrenceEndDate = recurrence?.EndDate is { } end
            ? new DateTime(end.Year, end.Month, end.Day)
            : null;
        EditorOccurrences = recurrence?.NumberOfOccurrences ?? 10;
        EditorDaysOfWeek = string.Join(", ", recurrence?.DaysOfWeek ?? []);
        EditorDayOfMonth = recurrence?.DayOfMonth ?? 1;
        EditorRecurrenceIndex = recurrence?.Index ?? CalendarRecurrenceIndex.First;
        EditorMonth = recurrence?.Month ?? 1;
        EditorFirstDayOfWeek = recurrence?.FirstDayOfWeek ?? DayOfWeek.Monday;
    }

    private async Task SaveEventAsync()
    {
        EditorError = null;
        try
        {
            var option = SelectedEditorCalendar ?? throw new InvalidOperationException("Choose a calendar.");
            if (!option.Calendar.Info.CanEdit)
            {
                throw new InvalidOperationException("The selected calendar is read-only.");
            }
            var draft = BuildDraft(option.Calendar.Info.ProviderId);
            if (_editingEvent is null)
            {
                await _provider.CreateEventAsync(option.Account, draft);
            }
            else
            {
                await _provider.UpdateEventAsync(option.Account, _editingEvent.Source.Event.ProviderId, draft);
            }
            IsEditorOpen = false;
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            EditorError = ex.Message;
        }
    }

    internal CalendarEventDraft BuildDraft(string calendarId)
    {
        if (string.IsNullOrWhiteSpace(EditorSubject))
        {
            throw new InvalidOperationException("Subject is required.");
        }
        var start = Combine(EditorStartDate, EditorStartTime, "Start");
        var end = Combine(EditorEndDate, EditorEndTime, "End");
        if (end <= start)
        {
            throw new InvalidOperationException("End must be after start.");
        }
        if (EditorReminderMinutes < 0)
        {
            throw new InvalidOperationException("Reminder minutes cannot be negative.");
        }
        var attendees = EditorAttendees.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(address =>
            {
                if (!address.Contains('@', StringComparison.Ordinal) || address.Any(char.IsWhiteSpace))
                {
                    throw new InvalidOperationException($"Invalid attendee address: {address}");
                }
                return new CalendarAttendee(new MailAddress("", address));
            }).ToArray();
        return new(
            calendarId,
            EditorSubject.Trim(),
            start,
            end,
            string.IsNullOrWhiteSpace(EditorLocation) ? null : EditorLocation.Trim(),
            attendees,
            EditorReminderOn,
            EditorReminderMinutes,
            EditorRecurs ? BuildRecurrence(DateOnly.FromDateTime(start.Date)) : null);
    }

    private CalendarRecurrence BuildRecurrence(DateOnly startDate)
    {
        if (EditorInterval < 1)
        {
            throw new InvalidOperationException("Recurrence interval must be positive.");
        }
        var days = EditorDaysOfWeek.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Enum.TryParse<DayOfWeek>(value, true, out var day)
                ? day
                : throw new InvalidOperationException($"Invalid recurrence day: {value}"))
            .Distinct()
            .ToArray();
        DateOnly? endDate = EditorRecurrenceEndDate is { } end
            ? DateOnly.FromDateTime(end.Date)
            : null;
        return new(
            EditorPattern,
            EditorInterval,
            startDate,
            EditorRange,
            EditorRange == CalendarRecurrenceRangeType.EndDate ? endDate : null,
            EditorRange == CalendarRecurrenceRangeType.Numbered ? EditorOccurrences : null,
            days,
            EditorDayOfMonth,
            EditorRecurrenceIndex,
            EditorMonth,
            EditorFirstDayOfWeek);
    }

    private static DateTimeOffset Combine(DateTime? date, TimeSpan? time, string field)
    {
        if (date is null || time is null)
        {
            throw new InvalidOperationException($"{field} date and time are required.");
        }
        return new DateTimeOffset(DateTime.SpecifyKind(date.Value.Date.Add(time.Value), DateTimeKind.Local));
    }

    private async Task DeleteEventAsync()
    {
        if (_editingEvent is null)
        {
            return;
        }
        EditorError = null;
        try
        {
            await _provider.DeleteEventAsync(
                _editingEvent.Source.Account,
                _editingEvent.Source.Calendar.Info.ProviderId,
                _editingEvent.Source.Event.ProviderId);
            IsEditorOpen = false;
            await RefreshEventsAsync();
        }
        catch (Exception ex)
        {
            EditorError = ex.Message;
        }
    }

    private Task CloseEditorAsync()
    {
        IsEditorOpen = false;
        EditorError = null;
        return Task.CompletedTask;
    }

    private static string ResolveColor(string? color, int index)
    {
        if (!string.IsNullOrWhiteSpace(color) && color.StartsWith('#') &&
            (color.Length == 7 || color.Length == 9))
        {
            return color;
        }
        return Palette[index % Palette.Length];
    }

    private sealed record AccountLoadResult(CalendarAccountGroup Group, string? Error);
    private sealed record EventLoadResult(IReadOnlyList<CalendarEventSource> Events, string? Error);
}

public sealed record CalendarAccountGroup(MailAccount Account, IReadOnlyList<CalendarChoice> Calendars)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Account.DisplayName) ? Account.EmailAddress : Account.DisplayName;
}

public sealed class CalendarChoice : ViewModelBase
{
    private readonly Action _changed;
    private bool _isVisible = true;

    public CalendarChoice(CalendarInfo info, string color, Action changed)
    {
        Info = info;
        Color = color;
        _changed = changed;
    }

    public CalendarInfo Info { get; }
    public string Color { get; }
    public string Name => Info.Name;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value))
            {
                _changed();
            }
        }
    }
}

public sealed record EditorCalendarOption(MailAccount Account, CalendarChoice Calendar)
{
    public string DisplayName => $"{Account.EmailAddress} — {Calendar.Name}";
}

public sealed record CalendarEventSource(MailAccount Account, CalendarChoice Calendar, CalendarEvent Event);

public sealed record CalendarEventItem(
    CalendarEventSource Source,
    double Top,
    double Height,
    double Left,
    double Width)
{
    public string Subject => Source.Event.Subject;
    public string Time => $"{Source.Event.StartsAt.ToLocalTime():HH:mm}–{Source.Event.EndsAt.ToLocalTime():HH:mm}";
    public string AvailabilityText => Source.Event.Availability switch
    {
        CalendarAvailability.Free => "Free",
        CalendarAvailability.WorkingElsewhere => "Working elsewhere",
        CalendarAvailability.Tentative => "Tentative",
        CalendarAvailability.Busy => "Busy",
        CalendarAvailability.OutOfOffice => "Out of office",
        _ => "Status unknown"
    };
    public string TimeAndAvailability => $"{Time} · {AvailabilityText}";
    public string Color => Source.Calendar.Color;
    public string CalendarIdentity => $"{Source.Account.EmailAddress} / {Source.Calendar.Name}";

    public static CalendarEventItem ForMonth(CalendarEventSource source) =>
        new(source, 0, 22, 0, 0);
}

public sealed record CalendarDayColumn(
    DateTimeOffset Date,
    double Width,
    IReadOnlyList<CalendarEventItem> Events)
{
    public string Heading => Date.ToString("ddd d");
}

public sealed record CalendarMonthCell(
    DateTimeOffset Date,
    bool IsCurrentMonth,
    IReadOnlyList<CalendarEventItem> Events)
{
    public string DayNumber => Date.Day.ToString(CultureInfo.InvariantCulture);
}
