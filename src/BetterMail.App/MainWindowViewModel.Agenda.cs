using System.Collections.ObjectModel;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    private IReadOnlyList<CalendarEventSource> _dayAgenda = [];
    private int _agendaLoadVersion;
    private DateTime _agendaDate = DateTime.Today;
    public ObservableCollection<DayAgendaItem> DayAgenda { get; } = [];
    public string AgendaDate => DateTime.Now.ToString("dddd, d MMMM");
    public bool HasDayEvents => _dayAgenda.Count > 0;
    public string AgendaNotice { get; private set; } = "";

    internal async Task RefreshDayAgendaAsync()
    {
        var version = ++_agendaLoadVersion;
        if (_store is null) { UpdateAgendaClock(); return; }
        var start = new DateTimeOffset(DateTime.Today);
        var end = new DateTimeOffset(DateTime.Today.AddDays(1));
        try
        {
            var sources = new List<CalendarEventSource>();
            foreach (var account in AccountsWith(ProviderCapabilities.Calendar).ToArray())
            foreach (var calendar in await _store.GetCalendarInfosAsync(account.AccountId))
            {
                var color = !string.IsNullOrWhiteSpace(calendar.Color) && calendar.Color.StartsWith('#')
                    ? calendar.Color : AccountColors.For(calendar.ProviderId);
                var choice = new CalendarChoice(calendar, color, () => { });
                foreach (var item in await _store.GetCalendarEventsAsync(account.AccountId, calendar.ProviderId, start, end))
                    if (!item.IsCancelled) sources.Add(new(account, choice, item));
            }
            if (version != _agendaLoadVersion || start.Date != DateTime.Today) return;
            _agendaDate = start.Date;
            _dayAgenda = sources.OrderBy(item => item.Event.StartsAt).ToArray();
            AgendaNotice = sources.Count == 0 ? "No events today in the local cache." : "";
        }
        catch (Exception error)
        {
            if (version != _agendaLoadVersion) return;
            AgendaNotice = $"Could not refresh today's agenda: {error.Message}";
        }
        RaisePropertyChanged(nameof(AgendaNotice));
        UpdateAgendaClock();
    }

    internal void UpdateAgendaClock()
    {
        var now = DateTimeOffset.Now;
        if (_agendaDate != now.LocalDateTime.Date)
        {
            _agendaDate = now.LocalDateTime.Date;
            _dayAgenda = [];
            _ = RefreshDayAgendaAsync();
        }
        var items = _dayAgenda.Select(source => new DayAgendaItem(source, now)).ToList();
        items.Add(new(null, now));
        CollectionUpdates.Reconcile(DayAgenda, items.OrderBy(item => item.SortAt).ToArray(), item => item.Key);
        RaisePropertyChanged(nameof(AgendaDate));
        RaisePropertyChanged(nameof(HasDayEvents));
        RaisePropertyChanged(nameof(NextCalendarEventTime));
    }
    internal void OpenAgendaEvent(DayAgendaItem item)
    {
        if (item.Source is { } source) CalendarEventDetailsRequested?.Invoke(source);
    }
}

public sealed record DayAgendaItem(CalendarEventSource? Source, DateTimeOffset Now)
{
    public bool IsNow => Source is null;
    public bool IsEvent => Source is not null;
    public string Key => Source is null ? "now" : $"{Source.Account.AccountId}:{Source.Event.CalendarId}:{Source.Event.ProviderId}";
    public DateTimeOffset SortAt => Source?.Event.IsAllDay == true ? new DateTimeOffset(Now.LocalDateTime.Date) : Source?.Event.StartsAt ?? Now;
    public string Title => Source?.Event.Subject ?? $"Now · {Now.ToLocalTime():HH:mm}";
    public string Time => Source is null ? "" : Source.Event.IsAllDay ? "All day" : $"{Source.Event.StartsAt.ToLocalTime():HH:mm}–{Source.Event.EndsAt.ToLocalTime():HH:mm}";
    public string Detail => Source is null ? "" : $"{Source.Calendar.Name} · {(Source.Event.EndsAt <= Now ? "Ended" : Source.Event.StartsAt <= Now ? "In progress" : "Upcoming")}";
    public double Opacity => Source?.Event.EndsAt <= Now ? 0.55 : 1;
}
