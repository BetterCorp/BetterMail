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

        var details = new StackPanel { Margin = new Thickness(24), Spacing = 10 };
        Add(details, calendarEvent.Subject, 24, FontWeight.SemiBold);
        Add(details, calendarEvent.TimeText);
        Add(details, $"Calendar: {source.Account.EmailAddress} / {source.Calendar.Name}");
        AddIf(details, "Location", calendarEvent.Location);
        AddIf(details, "Organizer", calendarEvent.Organizer?.ToString());
        AddIf(details, "Attendees", string.Join(", ", (calendarEvent.Attendees ?? [])
            .Select(static attendee => attendee.Address.ToString())));
        Add(details, $"Availability: {AvailabilityText(calendarEvent.Availability)}");
        if (calendarEvent.IsReminderOn)
        {
            Add(details, $"Reminder: {calendarEvent.ReminderMinutesBeforeStart} minutes before");
        }
        if (calendarEvent.Recurrence is { } recurrence)
        {
            Add(details, $"Recurrence: {recurrence.PatternType}, every {recurrence.Interval}");
        }
        AddIf(details, "Description", PlainText(calendarEvent.Body, calendarEvent.BodyIsHtml));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };
        if (FindJoinUri(calendarEvent) is { } join)
        {
            actions.Children.Add(LinkButton("Join meeting", join));
        }
        if (HttpUri(calendarEvent.WebLink) is { } webLink)
        {
            actions.Children.Add(LinkButton("Open in Outlook", webLink));
        }
        if (actions.Children.Count > 0)
        {
            details.Children.Add(actions);
        }
        Content = new ScrollViewer { Content = details };
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
        uri.Scheme is "http" or "https" ? uri : null;

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
