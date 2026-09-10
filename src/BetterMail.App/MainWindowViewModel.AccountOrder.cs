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
    private IEnumerable<Mailbox> OrderedMailboxes => Mailboxes.OrderBy(mailbox =>
        Array.FindIndex(OrderedAccounts.ToArray(), account => account.AccountId == mailbox.AccountId)).ThenBy(mailbox => mailbox.IsShared);
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
        await RefreshOwnedWorkspaceAccountsIfCreatedAsync();
        await LoadFoldersAsync();
        ((AsyncCommand<MailAccount>)MoveAccountUpCommand).Refresh();
        ((AsyncCommand<MailAccount>)MoveAccountDownCommand).Refresh();
    }
}
