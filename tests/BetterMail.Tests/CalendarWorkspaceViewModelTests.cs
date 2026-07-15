using BetterMail.App;
using BetterMail.Core;

namespace BetterMail.Tests;

public sealed class CalendarWorkspaceViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 9, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public async Task AggregatesAccountsIsolatesFailuresAndLaysOutOverlaps()
    {
        var provider = new FakeCalendarProvider();
        var viewModel = new CalendarWorkspaceViewModel(provider, Accounts(), () => Now);
        viewModel.SetViewportWidth(1400);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, viewModel.CalendarGroups.Count);
        Assert.Contains(viewModel.LoadIssues, issue => issue.Contains("broken@example.com", StringComparison.Ordinal));
        Assert.Contains(viewModel.LoadIssues, issue => issue.Contains("Unavailable", StringComparison.Ordinal));
        Assert.Equal(2, viewModel.EditableCalendars.Count);
        var events = viewModel.DayColumns.SelectMany(day => day.Events).ToArray();
        Assert.Equal(3, events.Length);
        var overlapping = events.Where(item => item.Source.Calendar.Info.ProviderId == "work").ToArray();
        Assert.Equal(2, overlapping.Length);
        Assert.NotEqual(overlapping[0].Left, overlapping[1].Left);
        Assert.All(overlapping, item => Assert.True(item.Width > 0 && item.Height >= 24));

        var work = viewModel.CalendarGroups[0].Calendars.Single(item => item.Info.ProviderId == "work");
        work.IsVisible = false;
        Assert.DoesNotContain(
            viewModel.DayColumns.SelectMany(day => day.Events),
            item => item.Source.Calendar.Info.ProviderId == "work");
    }

    [Fact]
    public async Task NavigatesModesAndBuildsFullEditableEventPayload()
    {
        var provider = new FakeCalendarProvider();
        var viewModel = new CalendarWorkspaceViewModel(provider, Accounts()[..1], () => Now);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        viewModel.MonthCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsMonthView && viewModel.MonthCells.Count == 42);
        viewModel.TodayCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedDate.Date == Now.Date);

        viewModel.NewEventCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsEditorOpen);
        viewModel.EditorSubject = "Planning";
        viewModel.EditorLocation = "Boardroom";
        viewModel.EditorAttendees = "ada@example.com; grace@example.com";
        viewModel.EditorStartDate = Now.LocalDateTime.Date;
        viewModel.EditorEndDate = Now.LocalDateTime.Date;
        viewModel.EditorStartTime = TimeSpan.FromHours(13);
        viewModel.EditorEndTime = TimeSpan.FromHours(14);
        viewModel.EditorReminderOn = true;
        viewModel.EditorReminderMinutes = 30;
        viewModel.EditorRecurs = true;
        viewModel.EditorPattern = CalendarRecurrencePatternType.Weekly;
        viewModel.EditorInterval = 2;
        viewModel.EditorRange = CalendarRecurrenceRangeType.Numbered;
        viewModel.EditorOccurrences = 8;
        viewModel.EditorDaysOfWeek = "Tuesday, Thursday";

        var draft = viewModel.BuildDraft("work");

        Assert.Equal("Planning", draft.Subject);
        Assert.Equal("Boardroom", draft.Location);
        Assert.Equal(2, draft.Attendees!.Count);
        Assert.Equal(30, draft.ReminderMinutesBeforeStart);
        Assert.Equal(CalendarRecurrencePatternType.Weekly, draft.Recurrence!.PatternType);
        Assert.Equal(2, draft.Recurrence.Interval);
        Assert.Equal(8, draft.Recurrence.NumberOfOccurrences);
        Assert.Equal([DayOfWeek.Tuesday, DayOfWeek.Thursday], draft.Recurrence.DaysOfWeek);

        viewModel.SaveEventCommand.Execute(null);
        await WaitUntilAsync(() => provider.CreatedDraft is not null && !viewModel.IsEditorOpen);
        Assert.Equal("Planning", provider.CreatedDraft!.Subject);

        var created = viewModel.MonthCells.SelectMany(day => day.Events)
            .Single(item => item.Source.Event.ProviderId == "created");
        viewModel.EditEventCommand.Execute(created);
        await WaitUntilAsync(() => viewModel.IsEditorOpen && viewModel.IsEditing);
        viewModel.EditorSubject = "Updated planning";
        viewModel.SaveEventCommand.Execute(null);
        await WaitUntilAsync(() => provider.UpdatedDraft is not null && !viewModel.IsEditorOpen);
        Assert.Equal("Updated planning", provider.UpdatedDraft!.Subject);

        viewModel.EditEventCommand.Execute(created);
        await WaitUntilAsync(() => viewModel.IsEditorOpen);
        viewModel.DeleteEventCommand.Execute(null);
        await WaitUntilAsync(() => provider.DeletedEventId == "created" && !viewModel.IsEditorOpen);
    }

    [Fact]
    public void ResponsiveBreakpointKeepsOneComponent()
    {
        Assert.True(CalendarWorkspaceView.IsCompactWidth(480));
        Assert.False(CalendarWorkspaceView.IsCompactWidth(1200));
        Assert.True(CalendarWorkspaceView.IsPhoneWidth(559));
        Assert.False(CalendarWorkspaceView.IsPhoneWidth(560));
    }

    [Fact]
    public void CalendarPickerUsesLocalDateWithoutCastingDateTimeOffset()
    {
        var viewModel = new CalendarWorkspaceViewModel(new FakeCalendarProvider(), Accounts()[..1], () => Now);

        Assert.IsType<DateTime>(viewModel.CalendarSelectedDate);
        viewModel.CalendarSelectedDate = new DateTime(2026, 7, 17);

        Assert.Equal(new DateTime(2026, 7, 17), viewModel.SelectedDate.LocalDateTime.Date);
    }

    [Fact]
    public async Task EventEditorDatesUseDateTimeAndSaveInTheLocalTimezone()
    {
        var viewModel = new CalendarWorkspaceViewModel(new FakeCalendarProvider(), Accounts()[..1], () => Now);
        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);
        viewModel.NewEventCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsEditorOpen);

        Assert.IsType<DateTime>(viewModel.EditorStartDate);
        viewModel.EditorSubject = "Local meeting";
        viewModel.EditorStartDate = new DateTime(2026, 7, 17);
        viewModel.EditorEndDate = new DateTime(2026, 7, 17);
        viewModel.EditorStartTime = new TimeSpan(9, 30, 0);
        viewModel.EditorEndTime = new TimeSpan(10, 30, 0);

        var draft = viewModel.BuildDraft("work");
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(draft.StartsAt.DateTime), draft.StartsAt.Offset);
        Assert.Equal(9, draft.StartsAt.Hour);
    }

    [Fact]
    public void EventTimesUseTheLocalTimezone()
    {
        var source = new CalendarEventSource(
            Account("one", "one@example.com"),
            new CalendarChoice(new("work", "Work", null, true, "one"), "#0F6CBD", () => { }),
            new CalendarEvent(
                "event", "work", "Meeting",
                new DateTimeOffset(2026, 7, 17, 7, 30, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 17, 8, 30, 0, TimeSpan.Zero),
                null, null, false, 0, null, "one", CalendarAvailability.Busy));

        Assert.Equal(
            $"{source.Event.StartsAt.ToLocalTime():HH:mm}–{source.Event.EndsAt.ToLocalTime():HH:mm}",
            CalendarEventItem.ForMonth(source).Time);
        Assert.Equal("Busy", CalendarEventItem.ForMonth(source).AvailabilityText);
    }

    private static MailAccount[] Accounts() =>
    [
        Account("one", "one@example.com"),
        Account("two", "two@example.com"),
        Account("broken", "broken@example.com")
    ];

    private static MailAccount Account(string id, string email) =>
        new("microsoft365", id, "tenant", email, email, ProviderCapabilities.Calendar);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class FakeCalendarProvider : ICalendarProvider
    {
        private readonly List<CalendarEvent> _events =
        [
            Event("one", "work", "a", "Stand-up", Now.AddMinutes(30), Now.AddHours(2)),
            Event("one", "work", "b", "Review", Now.AddHours(1), Now.AddHours(2.5)),
            Event("two", "personal", "c", "Lunch", Now.AddHours(3), Now.AddHours(4))
        ];

        public CalendarEventDraft? CreatedDraft { get; private set; }
        public CalendarEventDraft? UpdatedDraft { get; private set; }
        public string? DeletedEventId { get; private set; }

        public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(
            MailAccount account,
            CancellationToken cancellationToken = default)
        {
            if (account.AccountId == "broken")
            {
                throw new InvalidOperationException("Account unavailable");
            }
            IReadOnlyList<CalendarInfo> calendars = account.AccountId == "one"
                ? [new("work", "Work", "#0F6CBD", true, account.AccountId),
                   new("unavailable", "Unavailable", null, false, account.AccountId)]
                : [new("personal", "Personal", "#8764B8", true, account.AccountId)];
            return Task.FromResult(calendars);
        }

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
            MailAccount account,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>(
                _events.Where(item => item.AccountId == account.AccountId && item.StartsAt < to && item.EndsAt > from).ToArray());

        public Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(
            MailAccount account,
            string calendarId,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            if (calendarId == "unavailable")
            {
                throw new InvalidOperationException("Calendar unavailable");
            }
            return Task.FromResult<IReadOnlyList<CalendarEvent>>(
                _events.Where(item =>
                    item.AccountId == account.AccountId &&
                    item.CalendarId == calendarId &&
                    item.StartsAt < to &&
                    item.EndsAt > from).ToArray());
        }

        public Task<CalendarEvent> CreateEventAsync(
            MailAccount account,
            CalendarEventDraft draft,
            CancellationToken cancellationToken = default)
        {
            CreatedDraft = draft;
            var created = new CalendarEvent(
                "created", draft.CalendarId, draft.Subject, draft.StartsAt, draft.EndsAt,
                draft.Location, draft.Attendees, draft.IsReminderOn,
                draft.ReminderMinutesBeforeStart, draft.Recurrence, account.AccountId);
            _events.Add(created);
            return Task.FromResult(created);
        }

        public Task<CalendarEvent> UpdateEventAsync(
            MailAccount account,
            string eventId,
            CalendarEventDraft draft,
            CancellationToken cancellationToken = default)
        {
            UpdatedDraft = draft;
            return Task.FromResult(new CalendarEvent(
                eventId, draft.CalendarId, draft.Subject, draft.StartsAt, draft.EndsAt,
                draft.Location, draft.Attendees, draft.IsReminderOn,
                draft.ReminderMinutesBeforeStart, draft.Recurrence, account.AccountId));
        }

        public Task DeleteEventAsync(
            MailAccount account,
            string calendarId,
            string eventId,
            CancellationToken cancellationToken = default)
        {
            DeletedEventId = eventId;
            _events.RemoveAll(item => item.ProviderId == eventId && item.AccountId == account.AccountId);
            return Task.CompletedTask;
        }

        private static CalendarEvent Event(
            string accountId,
            string calendarId,
            string id,
            string subject,
            DateTimeOffset start,
            DateTimeOffset end) =>
            new(id, calendarId, subject, start, end, null, AccountId: accountId);
    }
}
