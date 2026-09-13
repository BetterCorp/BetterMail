using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    public string GlobalSearchPlaceholder => $"Search { (ActiveModule == "Files" ? "Drive" : ActiveModule) } first, then all workspaces";

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

    private IReadOnlyList<MailFolderKey> ResolveQueryFolders(SearchQuery query)
    {
        static string NormalizePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        var path = query["in"] is { } rawPath ? NormalizePath(rawPath) : null;
        var mailboxes = Mailboxes.Where(MailboxMatchesSearchFilters).Select(mailbox => mailbox.Id).ToHashSet(StringComparer.Ordinal);
        var folders = Folders.Where(folder => mailboxes.Contains(folder.MailboxId));
        if (path is not null)
        {
            // Exact mailbox-relative path, not a leaf-name match. An explicit path can target archive/trash/junk.
            folders = folders.Where(folder => string.Equals(NormalizePath(MailFolderPath(folder.MailboxId, folder.ProviderId)), path, StringComparison.OrdinalIgnoreCase));
        }
        else folders = folders.Where(folder => IsFolderIncludedInSearch(folder, folder.MailboxId));
        var result = folders.Select(folder => new MailFolderKey(folder.MailboxId, folder.ProviderId)).ToArray();
        if (path is not null && result.Length == 0)
            throw new FormatException($"Folder '{path}' was not found. Use its full path, for example in:{{Inbox/Projects}}. Add account: to choose a mailbox.");
        return result;
    }
}
