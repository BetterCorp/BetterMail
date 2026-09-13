using Avalonia.Controls;
using Avalonia.Interactivity;
using BetterMail.Core;
using System.Globalization;

namespace BetterMail.App;

public sealed partial class SearchOptionsWindow : Window
{
    private readonly Action<string>? _apply;
    private readonly MainWindowViewModel? _owner;
    private readonly Dictionary<string, TextBox> _text = [];
    private readonly Dictionary<string, SearchMultiChoice> _choices = [];
    private readonly Dictionary<string, ComboBox> _single = [];
    private readonly Dictionary<string, List<string>> _paths = [];
    private readonly List<SearchDateRow> _dates = [];
    private readonly StackPanel _mail = new() { Spacing = 12 };
    private readonly StackPanel _datePanel = new() { Spacing = 8 };
    private readonly Button _include = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private readonly Button _exclude = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
    private bool _loading = true;
    public const string Help = """
        type:mail type:people — choose multiple types (mail, people, calendar, todo, drive, notes). Omit type to search everything.
        account:{name, email or ID} — repeat to select accounts/shared mailboxes. Shared mailboxes apply to mail.
        in:{Inbox/Projects} — repeat to include full mail folder paths. notin:{Inbox/Old} excludes a folder and its descendants.
        A picker-qualified path is ownerID::path and targets that exact mailbox/account. Unqualified paths match across the selected accounts.
        With type:drive only, in: and notin: select Drive paths including descendants. Drive path searches use the synced file index.
        Mail-only conditions apply to mail when multiple types are selected:
        from:{name or email} to:{name or email} cc:{name or email} subject:{words}
        date:{>=2026-09-01} date:{<2026-10-01} — combine date conditions.
        date:{>2026-09-01T14:30} — date/time (optional Z or ±HH:mm). date:{>14:30} — local time on any day.
        Operators: =, >, >=, <, <=. Dates use YYYY-MM-DD; times use HH:mm or HH:mm:ss.
        has:attachments or has:noattachments; omit for either.
        is:unread is:flagged — all chosen states must match (read, unread, flagged, pinned). Read and unread conflict.
        category:Finance category:Projects — any selected category. importance:low|normal|high (choose one).
        archives:true — include archives; junk/trash require an explicit in: path.
        Plain words search content. Braces or quotes keep phrases together. Unknown keys/values are errors.
        """;

    public SearchOptionsWindow() => InitializeComponent();
    public SearchOptionsWindow(string query, Action<string> apply, MainWindowViewModel? owner = null) : this()
    {
        _apply = apply; _owner = owner; SyntaxHelp.Text = Help;
        _include.Classes.Add("filterChoice"); _exclude.Classes.Add("filterChoice");
        SearchQuery parsed;
        try { parsed = SearchQuery.Parse(query); }
        catch (FormatException error)
        {
            QueryText.Text = query;
            FormError.Text = error.Message + " Correct the text in the main search box, then reopen Advanced filter.";
            ApplyButton.IsEnabled = false;
            return;
        }
        AddText(FieldsPanel, "words", "Words / phrases", string.Join(' ', parsed.Terms.Select(SearchQuery.Encode)));
        AddChoices(FieldsPanel, "type", "Types", new[] { "Mail", "People", "Calendar", "To Do", "Drive", "Notes" }.Select(value => new SearchChoice(value, value)), parsed.Values("type").Where(value => value != "Everything"));
        var accounts = owner?.Accounts.Select(account => new SearchChoice(account.AccountId, account.DisplayName + " · " + account.EmailAddress)) ?? [];
        var shared = owner?.Mailboxes.Where(mailbox => mailbox.IsShared).Select(mailbox => new SearchChoice(mailbox.Id, "Shared · " + mailbox.DisplayName + " · " + mailbox.Address)) ?? [];
        var selectedAccounts = parsed.Values("account").Select(value => owner?.SearchAccountIdentity(value) ?? value);
        AddChoices(FieldsPanel, "account", "Accounts / shared mailboxes", accounts.Concat(shared), selectedAccounts);
        _paths["Mail:in"] = []; _paths["Mail:notin"] = []; _paths["Drive:in"] = []; _paths["Drive:notin"] = [];
        var context = parsed.IsDrivePathSearch ? "Drive" : "Mail";
        _paths[context + ":in"].AddRange(parsed.Values("in")); _paths[context + ":notin"].AddRange(parsed.Values("notin"));
        FieldsPanel.Children.Add(_include); FieldsPanel.Children.Add(_exclude);
        _include.Click += async (_, _) => await ChoosePathsAsync("in");
        _exclude.Click += async (_, _) => await ChoosePathsAsync("notin");
        _mail.Children.Add(new TextBlock { Text = "Mail", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        foreach (var (key, label) in new[] { ("from", "From"), ("to", "To"), ("cc", "Cc"), ("subject", "Subject contains") })
            AddText(_mail, key, label, parsed[key] ?? "");
        AddSingle("has", "Attachments", ["Not set", "Yes", "No"], parsed["has"] is "attachments" or "attachment" ? "Yes" : parsed["has"] == "noattachments" ? "No" : "Not set");
        AddChoices(_mail, "is", "State (all selected conditions)", new[] { "read", "unread", "flagged", "pinned" }.Select(value => new SearchChoice(value, value)), parsed.Values("is"));
        AddChoices(_mail, "category", "Categories", (owner?.Messages.SelectMany(message => message.Categories).Distinct(StringComparer.OrdinalIgnoreCase) ?? []).Select(value => new SearchChoice(value, value)), parsed.Values("category"));
        AddSingle("importance", "Importance", ["Not set", "low", "normal", "high"], parsed["importance"] ?? "Not set");
        AddSingle("archives", "Include archives", ["Not set", "true", "false"], parsed["archives"] ?? "Not set");
        _mail.Children.Add(new TextBlock { Text = "Received date / time", FontWeight = Avalonia.Media.FontWeight.SemiBold });
        _mail.Children.Add(_datePanel);
        foreach (var date in parsed.Dates) AddDate(date);
        var addDate = new Button { Content = "+ Date condition" }; addDate.Click += (_, _) => { AddDate(null); Changed(); };
        _mail.Children.Add(addDate); FieldsPanel.Children.Add(_mail);
        _loading = false; Changed();
        if (owner is not null) _ = LoadCategoriesAsync();
    }
    private async Task LoadCategoriesAsync()
    {
        try { _choices["category"].AddOptions((await _owner!.SearchCategoriesAsync()).Select(value => new SearchChoice(value, value))); }
        catch (Exception error) { FormError.Text = "Some cached categories could not be loaded: " + error.Message; }
    }
    private void AddText(StackPanel panel, string key, string label, string value)
    {
        var input = new TextBox { Text = value }; _text[key] = input;
        Avalonia.Automation.AutomationProperties.SetName(input, label);
        panel.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label }, input } });
        input.TextChanged += (_, _) => Changed();
    }
    private void AddChoices(StackPanel panel, string key, string label, IEnumerable<SearchChoice> options, IEnumerable<string> selected)
    {
        var control = new SearchMultiChoice(label, options, selected, Changed); _choices[key] = control; panel.Children.Add(control);
    }
    private void AddSingle(string key, string label, string[] options, string selected)
    {
        var input = new ComboBox { ItemsSource = options, SelectedItem = selected, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        _single[key] = input; Avalonia.Automation.AutomationProperties.SetName(input, label);
        _mail.Children.Add(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label }, input } });
        input.SelectionChanged += (_, _) => Changed();
    }
    private bool DriveOnly => _choices["type"].Selected.SequenceEqual(new[] { "Drive" });
    private bool IncludesMail => _choices["type"].Selected.Count == 0 || _choices["type"].Selected.Contains("Mail");
    private void AddDate(SearchDate? date)
    {
        SearchDateRow row = null!;
        row = new SearchDateRow(date, Changed, () => { _dates.Remove(row); _datePanel.Children.Remove(row); Changed(); });
        _dates.Add(row); _datePanel.Children.Add(row);
    }
    private async Task ChoosePathsAsync(string key)
    {
        var context = DriveOnly ? "Drive" : "Mail";
        var paths = _paths[context + ":" + key];
        var picker = new SearchFoldersWindow(_owner, DriveOnly, _choices["account"].SelectedAvailable, paths, key == "notin");
        var result = await picker.ShowDialog<IReadOnlyList<string>?>(this);
        if (result is not null) { paths.Clear(); paths.AddRange(result); Changed(); }
    }
    private string Query()
    {
        var words = SearchQuery.Parse(_text["words"].Text ?? "");
        if (words.Fields.Count > 0 || words.Dates.Count > 0) throw new FormatException("Use Words / phrases for content, not search keys.");
        var tokens = words.Terms.Select(SearchQuery.Encode).ToList();
        void Add(string key, IEnumerable<string> values) => tokens.AddRange(values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => key + ":" + SearchQuery.Encode(value.Trim())));
        Add("type", _choices["type"].Selected); Add("account", _choices["account"].SelectedAvailable);
        if (IncludesMail || DriveOnly)
        {
            var context = DriveOnly ? "Drive" : "Mail";
            Add("in", _paths[context + ":in"]); Add("notin", _paths[context + ":notin"]);
        }
        if (IncludesMail)
        {
            foreach (var pair in _text.Where(pair => pair.Key != "words")) Add(pair.Key, [pair.Value.Text ?? ""]);
            foreach (var key in new[] { "is", "category" }) Add(key, _choices[key].Selected);
            foreach (var (key, input) in _single)
            {
                var value = input.SelectedItem as string;
                if (value is not null && value != "Not set") Add(key, [key == "has" ? value == "Yes" ? "attachments" : "noattachments" : value]);
            }
            Add("date", _dates.Select(row => row.Value()));
        }
        return SearchQuery.Parse(string.Join(' ', tokens)).Serialize();
    }
    private void Changed()
    {
        if (_loading) return;
        _mail.IsVisible = IncludesMail;
        _choices["account"].SetAvailable(value => IncludesMail || _owner?.Mailboxes.Any(mailbox => mailbox.IsShared && mailbox.Id == value) != true);
        _include.IsVisible = _exclude.IsVisible = IncludesMail || DriveOnly;
        var context = DriveOnly ? "Drive" : "Mail";
        _include.Content = $"Include {context.ToLowerInvariant()} folders · {_paths[context + ":in"].Count} selected";
        _exclude.Content = $"Exclude {context.ToLowerInvariant()} folders · {_paths[context + ":notin"].Count} selected";
        try { QueryText.Text = Query(); FormError.Text = ""; ApplyButton.IsEnabled = true; }
        catch (FormatException error) { FormError.Text = error.Message; ApplyButton.IsEnabled = false; }
    }
    private void ApplyClicked(object? sender, RoutedEventArgs args)
    {
        try
        {
            var query = Query(); _owner?.ValidateSearchReferences(SearchQuery.Parse(query));
            _apply?.Invoke(query); Close();
        }
        catch (FormatException error) { FormError.Text = error.Message; }
    }
    internal static string BuildQuery(IReadOnlyDictionary<string, string> values)
    {
        var words = SearchQuery.Parse(values.GetValueOrDefault("words") ?? "");
        if (words.Fields.Count > 0 || words.Dates.Count > 0) throw new FormatException("Words / phrases is for content.");
        return SearchQuery.Parse(string.Join(' ', words.Terms.Select(SearchQuery.Encode)
            .Concat(values.Where(pair => pair.Key is not ("words" or "date") && !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key + ":" + SearchQuery.Encode(pair.Value.Trim())))
            .Concat((values.GetValueOrDefault("date") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(value => "date:" + SearchQuery.Encode(value))))).Serialize();
    }
    private void CancelClicked(object? sender, RoutedEventArgs args) => Close();
}

public sealed record SearchChoice(string Value, string Label);
public sealed class SearchMultiChoice : StackPanel
{
    private readonly List<(SearchChoice Choice, CheckBox Box)> _options = [];
    private readonly Button _button = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    private readonly StackPanel _items = new() { Spacing = 3 };
    private readonly Action _changed;
    private Func<string, bool> _available = _ => true;
    private string _find = "";
    public IReadOnlyList<string> SelectedAvailable => Selected.Where(_available).ToArray();
    public void SetAvailable(Func<string, bool> available)
    {
        _available = available; RefreshOptions(); Update();
    }
    private void RefreshOptions()
    {
        foreach (var item in _options) item.Box.IsVisible = _available(item.Choice.Value) && item.Choice.Label.Contains(_find, StringComparison.OrdinalIgnoreCase);
    }
    public IReadOnlyList<string> Selected => _options.Where(item => item.Box.IsChecked == true).Select(item => item.Choice.Value).ToArray();
    public SearchMultiChoice(string label, IEnumerable<SearchChoice> options, IEnumerable<string> selected, Action changed)
    {
        _changed = changed; Spacing = 4;
        _button.Classes.Add("filterChoice");
        Children.Add(new TextBlock { Text = label }); Children.Add(_button);
        var find = new TextBox { PlaceholderText = "Find options" };
        find.TextChanged += (_, _) => { _find = find.Text ?? ""; RefreshOptions(); };
        _button.Flyout = new Flyout { Content = new StackPanel { Width = 380, Spacing = 8, Children = { find, new ScrollViewer { MaxHeight = 280, Content = _items } } } };
        Avalonia.Automation.AutomationProperties.SetName(_button, label);
        var values = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddOptions(options.Concat(values.Select(value => new SearchChoice(value, value))));
        foreach (var item in _options) item.Box.IsChecked = values.Contains(item.Choice.Value);
        Update();
    }
    public void AddOptions(IEnumerable<SearchChoice> options)
    {
        foreach (var choice in options)
        {
            if (_options.Any(item => item.Choice.Value.Equals(choice.Value, StringComparison.OrdinalIgnoreCase))) continue;
            var box = new CheckBox { Content = choice.Label }; _options.Add((choice, box)); _items.Children.Add(box);
            box.IsCheckedChanged += (_, _) => { Update(); _changed(); };
        }
    }
    private void Update()
    {
        var label = new TextBlock { Text = SelectedAvailable.Count == 0 ? "Any" : string.Join(", ", _options.Where(item => item.Box.IsChecked == true && _available(item.Choice.Value)).Select(item => item.Choice.Label)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var arrow = new TextBlock { Text = "⌄", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        Grid.SetColumn(arrow, 1);
        _button.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        _button.Content = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10, Children = { label, arrow } };
    }
}

internal sealed class SearchDateRow : StackPanel
{
    private readonly ComboBox _mode = new() { ItemsSource = new[] { "Date", "Date and time", "Time" }, Width = 145 };
    private readonly ComboBox _operator = new() { ItemsSource = new[] { "=", ">", ">=", "<", "<=" }, Width = 65 };
    private readonly DatePicker _date = new() { Width = 280 };
    private readonly TimePicker _time = new() { ClockIdentifier = "24HourClock" };
    private string? _original;
    public SearchDateRow(SearchDate? value, Action changed, Action remove)
    {
        Spacing = 5;
        _mode.SelectedIndex = value?.Time is not null ? 2 : value is { WholeDay: false } ? 1 : 0;
        _operator.SelectedItem = value?.Operator ?? ">=";
        _date.SelectedDate = value?.Instant?.ToLocalTime() ?? DateTimeOffset.Now;
        _time.SelectedTime = value?.Time?.ToTimeSpan() ?? value?.Instant?.LocalDateTime.TimeOfDay ?? TimeSpan.Zero;
        var delete = new Button { Content = "Remove" }; delete.Click += (_, _) => remove();
        Children.Add(new WrapPanel { Children = { _mode, _operator, delete } });
        Children.Add(new WrapPanel { Children = { _date, _time } });
        void Update() { _date.IsVisible = _mode.SelectedIndex != 2; _time.IsVisible = _mode.SelectedIndex != 0; }
        Update(); _original = value?.Source;
        _mode.SelectionChanged += (_, _) => { _original = null; Update(); changed(); };
        _operator.SelectionChanged += (_, _) => { _original = null; changed(); };
        _date.SelectedDateChanged += (_, _) => { _original = null; changed(); };
        _time.SelectedTimeChanged += (_, _) => { _original = null; changed(); };
    }
    public string Value()
    {
        if (_original is not null) return _original;
        if (_mode.SelectedIndex != 2 && _date.SelectedDate is null || _mode.SelectedIndex != 0 && _time.SelectedTime is null)
            throw new FormatException("Choose a date/time for each condition.");
        var date = _date.SelectedDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var time = (_time.SelectedTime ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        return (_operator.SelectedItem as string ?? "=") + (_mode.SelectedIndex == 0 ? date : _mode.SelectedIndex == 2 ? time : date + "T" + time);
    }
}
