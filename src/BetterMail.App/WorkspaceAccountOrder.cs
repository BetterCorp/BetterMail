using System.Collections.ObjectModel;
using BetterMail.Core;

namespace BetterMail.App;

internal static class WorkspaceAccountOrder
{
    // Only reorder when the account records are unchanged; lifecycle changes still reload.
    public static bool TryApply<T>(ObservableCollection<T> items, IReadOnlyList<MailAccount> accounts,
        Func<T, MailAccount> accountFor)
    {
        if (items.Count != accounts.Count ||
            !items.Select(accountFor).ToHashSet().SetEquals(accounts)) return false;
        var ordered = accounts.Select(account => items.Single(item => accountFor(item) == account)).ToArray();
        CollectionUpdates.Reconcile(items, ordered, accountFor);
        return true;
    }
}
