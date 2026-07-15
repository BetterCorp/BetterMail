using Avalonia.Controls;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class SharedMailboxWindow : Window
{
    public SharedMailboxWindow()
    {
        InitializeComponent();
    }

    public SharedMailboxWindow(
        MailAccount account,
        Func<MailAccount, string, string, Task> add) : this()
    {
        var viewModel = new SharedMailboxWindowViewModel(account, add);
        viewModel.Added += (_, _) => Close(true);
        DataContext = viewModel;
    }

    private void CancelClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}
