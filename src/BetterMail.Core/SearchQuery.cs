using System.Globalization;
using System.Text;

namespace BetterMail.Core;

/// <summary>Provider-neutral search syntax. Repeated date constraints are ANDed.</summary>
public sealed record SearchQuery(IReadOnlyList<string> Terms, IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<SearchDate> Dates)
{
    public static readonly string[] Keys = ["type", "account", "in", "from", "to", "cc", "subject", "date", "has", "is", "importance", "category", "archives"];
    public string Text => string.Join(' ', Terms);
    public string? this[string key] => Fields.GetValueOrDefault(key);
    public bool HasMailFilters => Dates.Count > 0 || Fields.Keys.Any(key => key is not ("type" or "account"));
    public string Scope => this["type"] ?? (HasMailFilters ? "Mail" : "Everything");
    public string FtsText => string.Join(' ', Terms.Select(term => "\"" + term.Replace("\"", "\"\"") + "\"*"));

    public static SearchQuery Parse(string text)
    {
        var terms = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dates = new List<SearchDate>();
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
                if (!Keys.Contains(key)) throw new FormatException($"Unknown search key '{key}'. Open Search options for available keys.");
                index++;
            }
            else index = start;
            var value = ReadValue(text, ref index);
            if (value.Length == 0) throw new FormatException($"Enter a value for {key ?? "the search term"}.");
            if (key is null) terms.Add(value);
            else if (key == "date") dates.Add(SearchDate.Parse(value));
            else if (!fields.TryAdd(key, value)) throw new FormatException($"Use '{key}' once. Date is the only repeatable key.");
        }
        if (fields.TryGetValue("type", out var type))
            fields["type"] = type.ToLowerInvariant() switch
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
        var query = new SearchQuery(terms, fields, dates);
        if (query.HasMailFilters && query.Scope != "Mail")
            throw new FormatException("Folder, address, subject, date, attachment, state, importance, category and archive filters require type:mail (or omit type).");
        return query;

        void Validate(string key, string[] allowed)
        {
            if (!fields.TryGetValue(key, out var value)) return;
            value = value.ToLowerInvariant();
            if (!allowed.Contains(value)) throw new FormatException($"{key}: use {string.Join(", ", allowed)}.");
            fields[key] = value;
        }
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
        .Concat(Fields.Select(field => field.Key + ":" + Encode(field.Value)))
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
