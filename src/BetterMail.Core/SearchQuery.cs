using System.Globalization;
using System.Text;

namespace BetterMail.Core;

/// <summary>Provider-neutral search syntax. Repeated date constraints are ANDed.</summary>
public sealed record SearchQuery(IReadOnlyList<string> Terms, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<SearchDate> Dates)
{
    public static readonly string[] Keys = ["type", "account", "in", "notin", "from", "to", "cc", "subject", "date", "has", "is", "importance", "category", "archives"];
    public string Text => string.Join(' ', Terms);
    public string? this[string key] => Fields.GetValueOrDefault(key);
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Multiple { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyList<string> Values(string key) => Multiple.TryGetValue(key, out var values) ? values : this[key] is { } value ? [value] : [];
    public bool HasMailFilters => Dates.Count > 0 || Fields.Keys.Any(key => key is "from" or "to" or "cc" or "subject" or "has" or "is" or "importance" or "category" or "archives") ||
        (Values("in").Count + Values("notin").Count > 0 && !IsDrivePathSearch);
    public bool IsDrivePathSearch => Values("type").Count == 1 && Values("type")[0] == "Drive";
    public IReadOnlyList<string> Scopes => Values("type").Count > 0 ? Values("type") : [HasMailFilters ? "Mail" : "Everything"];
    public bool Includes(string scope) => Scopes.Contains("Everything") || Scopes.Contains(scope);
    public string Scope => Scopes.Count == 1 ? Scopes[0] : "Multiple";
    public string FtsText => string.Join(' ', Terms.Select(term => "\"" + term.Replace("\"", "\"\"") + "\"*"));

    public static SearchQuery Parse(string text)
    {
        var terms = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dates = new List<SearchDate>();
        var multiple = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            if (index == text.Length) break;
            var start = index;
            while (index < text.Length && char.IsLetter(text[index])) index++;
            string? key = null;
            if (index < text.Length && text[index] == ':' && index > start &&
                text[start..index] is not ("http" or "https"))
            {
                key = text[start..index].ToLowerInvariant();
                if (!Keys.Contains(key)) throw new FormatException($"Unknown search key '{key}'. Open Advanced filter for available keys.");
                index++;
            }
            else index = start;
            var value = ReadValue(text, ref index);
            if (value.Length == 0) throw new FormatException($"Enter a value for {key ?? "the search term"}.");
            if (key is null) terms.Add(value);
            else if (key == "date") dates.Add(SearchDate.Parse(value));
            else
            {
                if (!fields.TryAdd(key, value) && key is not ("type" or "account" or "in" or "notin" or "is" or "category"))
                    throw new FormatException($"Use '{key}' once.");
                if (!multiple.TryGetValue(key, out var values)) multiple[key] = values = [];
                values.Add(value);
            }
        }
        if (multiple.TryGetValue("type", out var types))
            for (var i = 0; i < types.Count; i++)
                types[i] = types[i].ToLowerInvariant() switch
            {
                "everything" or "all" => "Everything", "mail" => "Mail", "people" or "contacts" => "People",
                "calendar" => "Calendar", "todo" or "todos" or "tasks" or "to do" => "To Do",
                "drive" or "files" => "Drive", "notes" => "Notes",
                _ => throw new FormatException("Type must be mail, people, calendar, todo, drive, notes, or everything.")
            };
        Validate("has", ["attachment", "attachments", "noattachments"]);
        Validate("is", ["read", "unread", "flagged", "pinned"]);
        Validate("importance", ["low", "normal", "high"]);
        Validate("archives", ["true", "false"]);
        foreach (var pair in multiple) fields[pair.Key] = pair.Value[0];
        var query = new SearchQuery(terms, fields, dates) { Multiple = multiple.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase) };
        if (query.Values("is").Contains("read") && query.Values("is").Contains("unread"))
            throw new FormatException("Choose read or unread; a message cannot be both.");
        if (query.HasMailFilters && !query.Includes("Mail"))
            throw new FormatException("Folder, address, subject, date, attachment, state, importance, category and archive filters require type:mail (or omit type).");
        return query;

        void Validate(string key, string[] allowed)
        {
            if (!multiple.TryGetValue(key, out var values)) return;
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i].ToLowerInvariant();
                if (!allowed.Contains(value)) throw new FormatException($"{key}: use {string.Join(", ", allowed)}.");
                values[i] = value;
            }
        }
    }

    public static IReadOnlyList<string> Tokens(string text)
    {
        var result = new List<string>();
        var start = 0; char close = '\0'; var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (escaped) { escaped = false; continue; }
            if (c == '\\' && close != '\0') { escaped = true; continue; }
            if (close != '\0') { if (c == close) close = '\0'; continue; }
            if (c is '{' or '"') { close = c == '{' ? '}' : '"'; continue; }
            if (char.IsWhiteSpace(c)) { if (i > start) result.Add(text[start..i]); start = i + 1; }
        }
        if (start < text.Length) result.Add(text[start..]);
        return result;
    }

    private static string ReadValue(string text, ref int index)
    {
        if (index >= text.Length || char.IsWhiteSpace(text[index])) return "";
        var close = text[index] switch { '{' => '}', '"' => '"', _ => '\0' };
        if (close != '\0') index++;
        var result = new StringBuilder();
        while (index < text.Length)
        {
            var character = text[index++];
            if (close != '\0' && character == close)
            {
                if (index < text.Length && !char.IsWhiteSpace(text[index])) throw new FormatException("Separate search fields with spaces.");
                return result.ToString();
            }
            if (close == '\0' && char.IsWhiteSpace(character)) return result.ToString();
            if (character == '\\' && index < text.Length && (text[index] == close || text[index] == '\\')) character = text[index++];
            result.Append(character);
        }
        if (close != '\0') throw new FormatException($"Close the search value with {close}.");
        return result.ToString();
    }

    public static string Encode(string value) => value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c is ':' or '{' or '}' or '"' or '\\')
        ? value : "{" + value.Replace("\\", "\\\\").Replace("}", "\\}") + "}";
    public string Serialize() => string.Join(' ', Terms.Select(Encode)
        .Concat(Fields.Keys.SelectMany(key => Values(key).Select(value => key + ":" + Encode(value))))
        .Concat(Dates.Select(date => "date:" + Encode(date.Source))));
}

public sealed record SearchDate(string Source, string Operator, DateTimeOffset? Instant, TimeOnly? Time, bool WholeDay)
{
    public static SearchDate Parse(string value)
    {
        var source = value;
        var op = "=";
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "=" })
            if (value.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; value = value[candidate.Length..]; break; }
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return new(source, op, new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local)), null, true);
        if (TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return new(source, op, null, time, false);
        if (DateTimeOffset.TryParseExact(value,
            ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mmzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
            CultureInfo.InvariantCulture, value.EndsWith('Z') ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal, out var instant))
            return new(source, op, instant, null, false);
        throw new FormatException("Date: use YYYY-MM-DD, YYYY-MM-DDTHH:mm (optional Z or ±HH:mm), or HH:mm; prefix with >, >=, <, <= or =.");
    }
}
