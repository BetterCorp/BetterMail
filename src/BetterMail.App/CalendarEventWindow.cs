using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BetterMail.Core;

namespace BetterMail.App;

internal sealed partial class CalendarEventWindow : Window
{
    private static readonly string[] MeetingDomains =
        ["teams.microsoft.com", "teams.live.com", "teams.cloud.microsoft", "meet.google.com", "zoom.us"];

    public CalendarEventWindow(CalendarEventSource source, WindowIcon? icon)
    {
        Source = source;
        var calendarEvent = source.Event;
        Title = calendarEvent.Subject;
        Icon = icon;
        Width = 620;
        Height = 560;
        MinWidth = 380;
        MinHeight = 280;

        Width = 560;
        Height = 640;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var heading = new StackPanel { Spacing = 8, Margin = new Thickness(24, 20, 24, 18) };
        Add(heading, source.Calendar.Name, 12);
        Add(heading, calendarEvent.Subject, 24, FontWeight.SemiBold);
        if (calendarEvent.IsCancelled) Add(heading, "Cancelled", 14, FontWeight.SemiBold);
        Add(heading, calendarEvent.StartsAt.ToLocalTime().ToString("dddd, d MMMM yyyy"), 15);
        Add(heading, calendarEvent.IsAllDay ? "All day" :
            $"{calendarEvent.StartsAt.ToLocalTime():HH:mm} – {calendarEvent.EndsAt.ToLocalTime():HH:mm} · {(calendarEvent.EndsAt - calendarEvent.StartsAt).TotalMinutes:0} min", 15);
        var header = new Border { BorderBrush = Brush.Parse(source.Calendar.Color), BorderThickness = new Thickness(4, 0, 0, 0), Child = heading };
        root.Children.Add(header);

        var details = new StackPanel { Margin = new Thickness(24, 4, 24, 20), Spacing = 18 };
        if (!calendarEvent.IsCancelled && FindJoinUri(calendarEvent) is { } join)
        {
            var joinButton = LinkButton("Join meeting", join);
            joinButton.Classes.Add("primary");
            joinButton.HorizontalAlignment = HorizontalAlignment.Left;
            details.Children.Add(joinButton);
        }
        Detail(details, "LOCATION", calendarEvent.Location);
        Detail(details, "ORGANIZER", calendarEvent.Organizer?.ToString());
        var attendees = calendarEvent.Attendees ?? [];
        if (attendees.Count > 0)
        {
            var guests = new StackPanel { Spacing = 7 };
            Add(guests, $"ATTENDEES · {attendees.Count}", 11, FontWeight.SemiBold);
            foreach (var attendee in attendees) Add(guests, attendee.Address.ToString(), 13);
            details.Children.Add(guests);
        }
        Detail(details, "DETAILS", PlainText(calendarEvent.Body, calendarEvent.BodyIsHtml));
        var metadata = new StackPanel { Spacing = 6 };
        Add(metadata, $"{AvailabilityText(calendarEvent.Availability)} · {source.Account.EmailAddress}", 12);
        if (calendarEvent.IsReminderOn) Add(metadata, $"Reminder {calendarEvent.ReminderMinutesBeforeStart} minutes before", 12);
        if (calendarEvent.Recurrence is { } recurrence) Add(metadata, $"Repeats: {recurrence.PatternType}, every {recurrence.Interval}", 12);
        details.Children.Add(metadata);
        var scroll = new ScrollViewer { Content = details };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(20, 12) };
        if (HttpUri(calendarEvent.WebLink) is { } webLink) actions.Children.Add(LinkButton("Open in calendar", webLink));
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        actions.Children.Add(close);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Content = root;
    }

    private static void Detail(StackPanel target, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var section = new StackPanel { Spacing = 6 };
        Add(section, label, 11, FontWeight.SemiBold);
        Add(section, value, 14);
        target.Children.Add(section);
    }

    public CalendarEventSource Source { get; }
    public CalendarEventWindowSession Session => new(
        Source.Account.AccountId, Source.Event.CalendarId, Source.Event.ProviderId);

    internal static Uri? FindJoinUri(CalendarEvent calendarEvent)
    {
        if (MeetingUri(calendarEvent.OnlineMeetingUrl) is { } explicitLink)
        {
            return explicitLink;
        }
        foreach (Match match in UrlPattern().Matches($"{calendarEvent.Location}\n{calendarEvent.Body}"))
        {
            if (MeetingUri(match.Value.TrimEnd('.', ',', ';', ')', ']')) is { } link)
            {
                return link;
            }
        }
        return null;
    }

    private static Uri? MeetingUri(string? value)
    {
        var uri = HttpUri(value);
        return uri is not null && MeetingDomains.Any(domain =>
            uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;
    }

    private static Uri? HttpUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" ? uri : null;

    private static Button LinkButton(string text, Uri uri)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        return button;
    }

    private static void Add(StackPanel target, string text, double fontSize = 14, FontWeight? weight = null) =>
        target.Children.Add(new SelectableTextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = weight ?? FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap
        });

    private static void AddIf(StackPanel target, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Add(target, $"{label}: {value}");
        }
    }

    private static string PlainText(string? body, bool isHtml) => string.IsNullOrWhiteSpace(body)
        ? ""
        : isHtml
            ? WebUtility.HtmlDecode(HtmlTagPattern().Replace(body, " ")).Trim()
            : body.Trim();

    private static string AvailabilityText(CalendarAvailability availability) => availability switch
    {
        CalendarAvailability.Free => "Free",
        CalendarAvailability.WorkingElsewhere => "Working elsewhere",
        CalendarAvailability.Tentative => "Tentative",
        CalendarAvailability.Busy => "Busy",
        CalendarAvailability.OutOfOffice => "Out of office",
        _ => "Unknown"
    };

    [GeneratedRegex("""https?://[^\s<>"']+""", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagPattern();
}
