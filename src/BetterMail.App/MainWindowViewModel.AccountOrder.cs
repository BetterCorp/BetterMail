using BetterMail.Core;
using System.Windows.Input;

namespace BetterMail.App;

public sealed partial class MainWindowViewModel
{
    private List<string> _accountOrder = [];
    public int AccountOrderVersion { get; private set; }
    public ICommand MoveAccountUpCommand { get; private set; } = null!;
    public ICommand MoveAccountDownCommand { get; private set; } = null!;
    private static string AccountOrderKey(MailAccount account) => $"{account.ProviderId}:{account.AccountId}";
    private int AccountRank(MailAccount account)
    {
        var rank = _accountOrder.IndexOf(AccountOrderKey(account));
        return rank < 0 ? int.MaxValue : rank;
    }
    private IEnumerable<MailAccount> OrderedAccounts => Accounts.OrderBy(AccountRank);
    private List<string> _mailboxOrder = [];
    private HashSet<string> _collapsedMailboxes = new(StringComparer.Ordinal);
    private IEnumerable<Mailbox> OrderedMailboxes => Mailboxes
        .OrderBy(mailbox => _mailboxOrder.IndexOf(mailbox.Id) is var index && index >= 0 ? index : int.MaxValue)
        .ThenBy(mailbox => Array.FindIndex(OrderedAccounts.ToArray(), account => account.AccountId == mailbox.AccountId))
        .ThenBy(mailbox => mailbox.IsShared);
    public IEnumerable<MailboxLayoutItem> SettingsMailboxes => OrderedMailboxes.Select(mailbox => new MailboxLayoutItem(
        mailbox, Accounts.FirstOrDefault(account => account.AccountId == mailbox.AccountId)?.EmailAddress ?? "",
        _collapsedMailboxes.Contains(mailbox.Id), collapsed => SetMailboxCollapsed(mailbox.Id, collapsed)));
    public ICommand MoveMailboxUpCommand { get; private set; } = null!;
    public ICommand MoveMailboxDownCommand { get; private set; } = null!;
    public List<string> GetMailboxOrderPreferences() => [.. _mailboxOrder];
    public List<string> GetCollapsedMailboxPreferences() => [.. _collapsedMailboxes];
    public void ConfigureMailboxLayout(IEnumerable<string>? order, IEnumerable<string>? collapsed)
    {
        _mailboxOrder = order?.Distinct().ToList() ?? [];
        _collapsedMailboxes = new(collapsed ?? [], StringComparer.Ordinal);
        RaisePropertyChanged(nameof(SettingsMailboxes));
    }
    private void SetMailboxCollapsed(string id, bool collapsed)
    {
        if (collapsed) _collapsedMailboxes.Add(id); else _collapsedMailboxes.Remove(id);
        var group = FolderGroups.FirstOrDefault(item => item.Mailbox.Id == id);
        if (group is not null) group.IsExpanded = !collapsed;
        MailboxLayoutChanged();
    }
    private Task MoveMailboxAsync(MailboxLayoutItem item, int direction)
    {
        var mailboxes = OrderedMailboxes.ToList();
        var index = mailboxes.FindIndex(mailbox => mailbox.Id == item.Mailbox.Id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= mailboxes.Count) return Task.CompletedTask;
        (mailboxes[index], mailboxes[target]) = (mailboxes[target], mailboxes[index]);
        _mailboxOrder = mailboxes.Select(mailbox => mailbox.Id).ToList();
        var groups = FolderGroups.ToDictionary(group => group.Mailbox.Id);
        CollectionUpdates.Reconcile(FolderGroups, mailboxes.Where(mailbox => groups.ContainsKey(mailbox.Id)).Select(mailbox => groups[mailbox.Id]).ToArray(), group => group.Mailbox.Id);
        RaisePropertyChanged(nameof(SettingsMailboxes));
        MailboxLayoutChanged();
        return Task.CompletedTask;
    }
    private void MailboxLayoutChanged()
    {
        AccountOrderVersion++;
        RaisePropertyChanged(nameof(AccountOrderVersion));
        ((AsyncCommand<MailboxLayoutItem>)MoveMailboxUpCommand).Refresh();
        ((AsyncCommand<MailboxLayoutItem>)MoveMailboxDownCommand).Refresh();
    }
    public List<string> GetAccountOrderPreferences() => [.. _accountOrder];
    public void ConfigureAccountOrder(IEnumerable<string>? order) => _accountOrder = order?.Distinct().ToList() ?? [];
    private async Task MoveAccountAsync(MailAccount account, int direction)
    {
        var accounts = OrderedAccounts.ToList();
        var index = accounts.IndexOf(account);
        var destination = index + direction;
        if (index < 0 || destination < 0 || destination >= accounts.Count) return;
        (accounts[index], accounts[destination]) = (accounts[destination], accounts[index]);
        _accountOrder = accounts.Select(AccountOrderKey).ToList();
        AccountOrderVersion++;
        RaisePropertyChanged(nameof(AccountOrderVersion));
        RaisePropertyChanged(nameof(SettingsAccounts));
        RaisePropertyChanged(nameof(SettingsMailboxes));
        await RefreshOwnedWorkspaceAccountsIfCreatedAsync();
        await LoadFoldersAsync();
        ((AsyncCommand<MailAccount>)MoveAccountUpCommand).Refresh();
        ((AsyncCommand<MailAccount>)MoveAccountDownCommand).Refresh();
    }
}

public sealed class MailboxLayoutItem(Mailbox mailbox, string accountAddress, bool collapsed, Action<bool> changeCollapsed) : ViewModelBase
{
    private bool _startCollapsed = collapsed;
    public Mailbox Mailbox => mailbox;
    public string Name => mailbox.DisplayName;
    public string Detail => mailbox.IsShared ? $"{mailbox.Address} · Shared via {accountAddress}" : mailbox.Address;
    public bool StartCollapsed
    {
        get => _startCollapsed;
        set { if (SetProperty(ref _startCollapsed, value)) changeCollapsed(value); }
    }
}
