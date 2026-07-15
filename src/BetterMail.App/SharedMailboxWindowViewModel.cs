using System.Windows.Input;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class SharedMailboxWindowViewModel : ViewModelBase
{
    private readonly Func<MailAccount, string, string, Task> _add;
    private readonly MailAccount _account;
    private string _address = "";
    private string _selectedPermissionMode = "Read only";
    private string? _error;

    public SharedMailboxWindowViewModel(MailAccount account, Func<MailAccount, string, string, Task> add)
    {
        _account = account;
        _add = add;
        AddCommand = new AsyncCommand(AddAsync);
    }

    public event EventHandler? Added;
    public string AccountAddress => _account.EmailAddress;
    public IReadOnlyList<string> PermissionModes { get; } = ["Read only", "Send As", "Send on behalf"];
    public ICommand AddCommand { get; }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string SelectedPermissionMode
    {
        get => _selectedPermissionMode;
        set => SetProperty(ref _selectedPermissionMode, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private async Task AddAsync()
    {
        Error = null;
        try
        {
            await _add(_account, Address.Trim(), SelectedPermissionMode);
            Added?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = exception.Message;
        }
    }
}
