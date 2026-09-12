using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class SearchOptionsWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly Action<string>? _apply;
    private string _loadedQuery = "";
    public const string Help = """
        type:{mail|people|calendar|todo|drive|notes|everything} — one type; | means a choice, not query syntax.
        account:{name or email} — exact linked account; shared mailbox addresses require type:mail.
        Mail keys (imply type:mail):
        in:{Inbox/Projects} — exact full folder path; use account: to disambiguate. Explicit paths include archive, junk or trash.
        from:{name or email}  to:{name or email}  cc:{name or email}
        subject:{words} — subject contains these words.
        date:{>=2026-09-01} date:{<2026-10-01} — received date range.
        date:{>2026-09-01T14:30} — date and time; Z or ±HH:mm may specify a timezone.
        date:{>14:30} — local time of day on any date.
        Date operators: =, >, >=, <, <=. No operator means equals. Dates use YYYY-MM-DD. A date without time compares whole local days; a time without a date compares HH:mm:ss (missing seconds = 00).
        has:{attachments|noattachments}  is:{read|unread|flagged|pinned}
        importance:{low|normal|high}  category:{exact category}
        archives:{true|false} — false by default; junk/trash need an explicit in: path.
        Repeat date: to make a range. Other keys may appear once. Values are case-insensitive. Plain words search content; quotes/braces keep a phrase together for mail. Advanced mail filters search the synced cache.
        Example: budget type:mail account:{alex@work.example} in:{Inbox/Projects} from:{Jamie} has:attachments date:{>=2026-09-01}
        """;

    public SearchOptionsWindow() => InitializeComponent();
    public SearchOptionsWindow(string query, Action<string> apply) : this()
    {
        _apply = apply;
        SyntaxHelp.Text = Help;
        AddField("words", "Words / phrases", "budget \"quarterly review\"");
        foreach (var (key, label, hint) in new[]
        {
            ("type", "Type", "mail, people, calendar, todo, drive, notes"),
            ("account", "Account / shared mailbox", "Name or email address"),
            ("in", "Folder path (mail)", "Inbox/Projects"),
            ("from", "From (mail)", "Name or email address"),
            ("to", "To (mail)", "Name or email address"),
            ("cc", "Cc (mail)", "Name or email address"),
            ("subject", "Subject contains (mail)", "Words in the subject"),
            ("date", "Received dates / times (mail)", ">=2026-09-01; <2026-10-01 (separate constraints with ; )"),
            ("has", "Attachments (mail)", "attachments or noattachments"),
            ("is", "State (mail)", "read, unread, flagged, pinned"),
            ("importance", "Importance (mail)", "low, normal, high"),
            ("category", "Category (mail)", "Exact category name"),
            ("archives", "Include archives (mail)", "true or false")
        }) AddField(key, label, hint);
        QueryText.Text = query;
        ReadQuery();
    }
    private void AddField(string key, string label, string hint)
    {
        var input = new TextBox { PlaceholderText = hint };
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        _fields.Add(key, input);
        FieldsPanel.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label }, input } });
    }
    private void ReadQuery()
    {
        try
        {
            var parsed = SearchQuery.Parse(QueryText.Text ?? "");
            foreach (var (key, input) in _fields)
                input.Text = key switch { "words" => string.Join(' ', parsed.Terms.Select(SearchQuery.Encode)), "date" => string.Join("; ", parsed.Dates.Select(date => date.Source)), _ => parsed[key] ?? "" };
            _loadedQuery = QueryText.Text ?? "";
            FormError.Text = "";
            ApplyButton.IsEnabled = true;
        }
        catch (FormatException exception) { FormError.Text = exception.Message; ApplyButton.IsEnabled = false; }
    }
    private void ReadClicked(object? sender, RoutedEventArgs args) => ReadQuery();
    private void ApplyClicked(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (_loadedQuery != (QueryText.Text ?? "")) throw new FormatException("The query changed. Choose 'Read query into fields' before applying the form.");
            var result = BuildQuery(_fields.ToDictionary(field => field.Key, field => field.Value.Text ?? ""));
            _apply?.Invoke(result);
            Close();
        }
        catch (FormatException exception) { FormError.Text = exception.Message; }
    }
    internal static string BuildQuery(IReadOnlyDictionary<string, string> values)
    {
        var words = SearchQuery.Parse(values.GetValueOrDefault("words") ?? "");
        if (words.Fields.Count > 0 || words.Dates.Count > 0) throw new FormatException("Put search keys in their fields or in the Query box; Words / phrases is for content.");
        var fields = values.Where(field => field.Key is not ("words" or "date") && !string.IsNullOrWhiteSpace(field.Value));
        var text = string.Join(' ', words.Terms.Select(SearchQuery.Encode)
            .Concat(fields.Select(field => field.Key + ":" + SearchQuery.Encode(field.Value.Trim())))
            .Concat((values.GetValueOrDefault("date") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(date => "date:" + SearchQuery.Encode(date))));
        return SearchQuery.Parse(text).Serialize();
    }
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close();
}
