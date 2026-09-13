using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    public string GlobalSearchPlaceholder => $"Search { (ActiveModule == "Files" ? "Drive" : ActiveModule) } first, then all workspaces";

    private IReadOnlyList<string>? _latestMailMailboxIds;
    private IReadOnlyList<string>? _displayedMailMailboxIds;
    private SearchAccountFilter? _latestMailAccount;
    private SearchAccountFilter? _displayedMailAccount;
    private SearchQuery? _latestMailQuery;
    private IReadOnlyList<MailFolderKey> _latestMailFolders = [];
    private SearchQuery? _displayedMailQuery;
    private IReadOnlyList<MailFolderKey> _displayedMailFolders = [];
    private string _mailSearchSummary = "";
    private string? _searchError;
    private string _searchNotice = "";
    public string? SearchError { get => _searchError; private set => SetProperty(ref _searchError, value); }
    public string SearchNotice { get => _searchNotice; private set => SetProperty(ref _searchNotice, value); }

    private SearchAccountFilter? ResolveQueryAccount(string? value)
    {
        if (value is null) return null;
        var accounts = Accounts.Where(account =>
            string.Equals(value, account.EmailAddress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, account.DisplayName, StringComparison.OrdinalIgnoreCase) || value == account.AccountId)
            .Select(account => new SearchAccountFilter(account.DisplayName, account.AccountId));
        var shared = Mailboxes.Where(mailbox => mailbox.IsShared &&
            (string.Equals(value, mailbox.Address, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(value, mailbox.DisplayName, StringComparison.OrdinalIgnoreCase) || value == mailbox.Id))
            .Select(mailbox => new SearchAccountFilter(mailbox.DisplayName, mailbox.AccountId, mailbox.Id));
        var matches = accounts.Concat(shared).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new FormatException($"No linked account or shared mailbox matches '{value}'. Use its email address."),
            _ => throw new FormatException($"Account '{value}' is ambiguous. Use its email address.")
        };
    }

    internal string SearchAccountIdentity(string value)
    {
        try { var match = ResolveQueryAccount(value)!; return match.MailboxId ?? match.AccountId!; }
        catch (FormatException) { return value; }
    }
    internal string SearchFolderPath(MailFolderItem folder) => NormalizeSearchPath(MailFolderPath(folder.MailboxId, folder.ProviderId));
    internal Task<IReadOnlyList<string>> SearchCategoriesAsync() => _store?.GetMailCategoriesAsync() ?? Task.FromResult<IReadOnlyList<string>>(Messages.SelectMany(message => message.Categories).Distinct().ToArray());
    internal void ValidateSearchReferences(SearchQuery query)
    {
        var accounts = query.Values("account").Select(value => ResolveQueryAccount(value)!).ToArray();
        if (accounts.Any(item => item.MailboxId is not null) && !query.Includes("Mail"))
            throw new FormatException("Shared mailboxes require Mail among the selected types.");
        if (query.IsDrivePathSearch)
        {
            foreach (var path in query.Values("in").Concat(query.Values("notin")))
            {
                var parts = path.Split("::", 2);
                if (parts.Length == 2 && !Accounts.Any(account => account.AccountId == parts[0] && account.Capabilities.HasFlag(ProviderCapabilities.Files) &&
                    (accounts.Length == 0 || accounts.Any(filter => filter.MailboxId is null && filter.AccountId == account.AccountId))))
                    throw new FormatException($"Drive folder '{path}' does not match a selected Drive account.");
            }
            return;
        }
        foreach (var path in query.Values("in").Concat(query.Values("notin")))
            if (!Folders.Any(folder => SearchPathMatches(path, folder.MailboxId, MailFolderPath(folder.MailboxId, folder.ProviderId)) &&
                (accounts.Length == 0 || accounts.Any(account => account.MailboxId is { } id ? id == folder.MailboxId : Mailboxes.Any(mailbox => mailbox.Id == folder.MailboxId && mailbox.AccountId == account.AccountId)))))
                throw new FormatException($"Folder '{path}' does not match a folder in the selected accounts.");
    }

    internal void OpenSearchInput()
    {
        IsSearchEditing = true;
        if (HasSearchText) SearchCommand.Execute(null);
        else
        {
            SearchError = null;
            SearchNotice = "Type to search, or choose Advanced filter.";
            IsGlobalSearchOpen = true;
        }
    }

    private bool _isSearchEditing;
    public bool IsSearchEditing
    {
        get => _isSearchEditing;
        set { if (SetProperty(ref _isSearchEditing, value)) { RaisePropertyChanged(nameof(ShowSearchBadges)); RaisePropertyChanged(nameof(SearchBadges)); } }
    }
    public bool ShowSearchBadges => HasSearchText && !IsSearchEditing;
    public IReadOnlyList<SearchFilterBadge> SearchBadges
    {
        get
        {
            SearchQuery? whole = null;
            try { whole = SearchQuery.Parse(SearchText); } catch (FormatException) { }
            var badges = new List<SearchFilterBadge>();
            foreach (var token in SearchQuery.Tokens(SearchText))
            {
                var key = token.Contains(':') ? token[..token.IndexOf(':')].ToLowerInvariant() : "";
                var filter = key.Length > 0 && key.All(char.IsLetter) && key is not ("http" or "https");
                string? error = null;
                try
                {
                    var part = SearchQuery.Parse(token);
                    if (filter)
                    {
                        if (key == "account") _ = ResolveQueryAccount(part["account"]);
                        if (key is "in" or "notin")
                        {
                            var context = (whole?.Values("type") ?? []).Select(value => "type:" + SearchQuery.Encode(value))
                                .Concat((whole?.Values("account") ?? []).Select(value => "account:" + SearchQuery.Encode(value)));
                            ValidateSearchReferences(SearchQuery.Parse(string.Join(' ', context.Append(token))));
                        }
                        if (whole is null && SearchQuery.Tokens(SearchText).All(item => { try { SearchQuery.Parse(item); return true; } catch (FormatException) { return false; } }))
                            SearchQuery.Parse(SearchText);
                    }
                }
                catch (FormatException exception) { error = exception.Message; }
                badges.Add(new(token, filter, error));
            }
            return badges;
        }
    }

    private IReadOnlyList<SearchAccountFilter> _queryAccounts = [];
    private bool QueryAccountMatches(string accountId) => _queryAccounts.Count == 0 || _queryAccounts.Any(item => item.AccountId == accountId && item.MailboxId is null);
    private bool QueryMailboxMatches(Mailbox mailbox) => _queryAccounts.Count == 0 || _queryAccounts.Any(item =>
        item.MailboxId is { } id ? mailbox.Id == id : mailbox.AccountId == item.AccountId);

    internal static string NormalizeSearchPath(string path) => string.Join('/', path.Replace("/drive/root:", "", StringComparison.OrdinalIgnoreCase).Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    internal static bool SearchPathMatches(string selection, string owner, string path, bool descendants = false)
    {
        var parts = selection.Split("::", 2);
        if (parts.Length == 2 && !string.Equals(parts[0], owner, StringComparison.OrdinalIgnoreCase)) return false;
        var expected = NormalizeSearchPath(parts[^1]);
        var actual = NormalizeSearchPath(path);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) || descendants &&
            (expected.Length == 0 || actual.StartsWith(expected + "/", StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<MailFolderKey> ResolveQueryFolders(SearchQuery query)
    {
        var included = query.IsDrivePathSearch ? [] : query.Values("in");
        var excluded = query.IsDrivePathSearch ? [] : query.Values("notin");
        var folders = Folders.Where(folder => Mailboxes.Any(mailbox => mailbox.Id == folder.MailboxId && QueryMailboxMatches(mailbox))).ToArray();
        foreach (var path in included.Concat(excluded))
            if (!folders.Any(folder => SearchPathMatches(path, folder.MailboxId, MailFolderPath(folder.MailboxId, folder.ProviderId))))
                throw new FormatException($"Folder '{path}' was not found in the selected accounts. Choose its full path in Advanced filter.");
        return folders.Where(folder =>
            (included.Count > 0 ? included.Any(path => SearchPathMatches(path, folder.MailboxId, MailFolderPath(folder.MailboxId, folder.ProviderId))) : IsFolderIncludedInSearch(folder, folder.MailboxId)) &&
            !excluded.Any(path => SearchPathMatches(path, folder.MailboxId, MailFolderPath(folder.MailboxId, folder.ProviderId), descendants: true)))
            .Select(folder => new MailFolderKey(folder.MailboxId, folder.ProviderId)).ToArray();
    }

    private async Task<IReadOnlyList<T>> SearchSelectedWorkspaceCacheAsync<T>(string kind, string text, int limit, CancellationToken token)
    {
        if (_queryAccounts.Count == 0) return await _store!.SearchWorkspaceItemsAsync<T>(kind, text, limit, null, token);
        var ids = _queryAccounts.Where(item => item.MailboxId is null).Select(item => item.AccountId!).Distinct().ToArray();
        var results = await Task.WhenAll(ids.Select(id => _store!.SearchWorkspaceItemsAsync<T>(kind, text, limit, id, token)));
        return results.SelectMany(items => items).Take(limit).ToArray();
    }
}

public sealed record SearchFilterBadge(string Text, bool IsFilter, string? Error)
{
    public bool IsValid => Error is null;
    public bool IsPlain => !IsFilter && Error is null;
    public string Background => Error is not null ? "#FDE7E9" : IsFilter ? "#DFF6DD" : "Transparent";
    public string Foreground => Error is not null ? "#A4262C" : IsFilter ? "#0B5A18" : "#52616F";
    public string Help => Error ?? (IsFilter ? "Valid filter" : "Search text");
}
