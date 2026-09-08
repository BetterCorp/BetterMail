using System.Collections.ObjectModel;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class McpMailboxChoice(Mailbox mailbox, bool selected) : ViewModelBase
{
    private bool _selected = selected;
    public string Id => mailbox.Id;
    public string Label => new MailAddress(mailbox.DisplayName, mailbox.Address).ToString();
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
}

public sealed partial class McpSettingsViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly EncryptedMailStore? _store;
    private readonly Func<Task> _refreshAndSync;
    private readonly Func<ComposeSender, string, DraftMessage, Task> _queueSend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpEndpoint? _endpoint;
    private McpConfiguration _active = new();
    private string _accessKey = "";
    private string _endpointPath = "";
    private bool _enabled;
    private bool _allowWrites;
    private bool _allowSending;
    private int _port = 47831;
    private string _status = "Disabled";
    private bool _disposed;
    private bool _initialized;

    public McpSettingsViewModel(EncryptedMailStore? store, Func<Task> refreshAndSync, Func<ComposeSender, string, DraftMessage, Task> queueSend)
    {
        _store = store;
        _refreshAndSync = refreshAndSync;
        _queueSend = queueSend;
        ApplyCommand = new(ApplyAsync, () => IsAvailable);
        RotateKeyCommand = new(RotateKeyAsync, () => IsAvailable);
        SignInTunnelCommand = new(SignInTunnelAsync, () => IsAvailable);
        StartTunnelCommand = new(StartTunnelAsync, () => IsAvailable);
        StopTunnelCommand = new(() => StopTunnelAsync(false), () => IsAvailable);
        SignOutTunnelCommand = new(() => StopTunnelAsync(true), () => IsAvailable);
    }

    public bool IsAvailable => _store is not null && _initialized && !_disposed;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool AllowWrites { get => _allowWrites; set => SetProperty(ref _allowWrites, value); }
    public bool AllowSending { get => _allowSending; set => SetProperty(ref _allowSending, value); }
    public int Port { get => _port; set { if (SetProperty(ref _port, value)) RaisePropertyChanged(nameof(EndpointUrl)); } }
    public string EndpointUrl => _endpointPath.Length == 0 ? "" : $"http://127.0.0.1:{Port}{_endpointPath}";
    public string AccessKey => _accessKey;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ObservableCollection<McpMailboxChoice> Mailboxes { get; } = [];
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand RotateKeyCommand { get; }

    public async Task InitializeAsync()
    {
        if (_store is null) { Status = "The encrypted mail database is unavailable."; return; }
        try
        {
            var saved = await _store.GetMcpConfigurationAsync();
            _accessKey = saved.AccessKey;
            _endpointPath = saved.EndpointPath;
            RaisePropertyChanged(nameof(AccessKey));
            RaisePropertyChanged(nameof(EndpointUrl));
            Enabled = saved.Configuration.Enabled;
            Port = saved.Configuration.Port;
            AllowWrites = saved.Configuration.AllowWrites;
            AllowSending = saved.Configuration.AllowSending;
            await RefreshMailboxesAsync(saved.Configuration.MailboxIds ?? []);
            await ReconfigureAsync(saved.Configuration, persist: false);
            _initialized = true;
            RaisePropertyChanged(nameof(IsAvailable));
            ApplyCommand.Refresh();
            RotateKeyCommand.Refresh();
            RefreshTunnelCommands();
        }
        catch (Exception exception) { Status = "MCP unavailable: " + exception.Message; }
    }

    public async Task RefreshMailboxesAsync(string[]? selected = null)
    {
        if (_store is null || _disposed) return;
        selected ??= Mailboxes.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
        var mailboxes = await _store.GetMailboxesAsync();
        CollectionUpdates.Reconcile(Mailboxes,
            mailboxes.Select(mailbox =>
            {
                var choice = Mailboxes.FirstOrDefault(item => item.Id == mailbox.Id) ?? new(mailbox, false);
                choice.IsSelected = selected.Contains(mailbox.Id);
                return choice;
            }),
            static item => item.Id);
    }

    private Task ApplyAsync() => ReconfigureAsync(new(Enabled, Port, AllowWrites, AllowWrites && AllowSending,
        Mailboxes.Where(item => item.IsSelected).Select(item => item.Id).ToArray()), persist: true);

    private async Task ReconfigureAsync(McpConfiguration configuration, bool persist)
    {
        await _gate.WaitAsync();
        try
        {
            if (_store is null || _disposed) return;
            // Revoke new requests before waiting for the old listener to drain.
            Volatile.Write(ref _active, new());
            await DisconnectTunnelAsync();
            if (_endpoint is { } previous)
            {
                _endpoint = null;
                await previous.DisposeAsync();
            }
            if (persist) await _store.SaveMcpConfigurationAsync(configuration);
            if (!configuration.Enabled) { Status = "Disabled — no MCP listener is running."; return; }
            if (configuration.Port is < 1024 or > 65535) throw new InvalidOperationException("Choose a port from 1024 to 65535.");
            var saved = await _store.GetMcpConfigurationAsync();
            Volatile.Write(ref _accessKey, saved.AccessKey);
            _endpointPath = saved.EndpointPath;
            RaisePropertyChanged(nameof(AccessKey));
            RaisePropertyChanged(nameof(EndpointUrl));
            Volatile.Write(ref _active, configuration);
            var tools = new McpMailTools(_store, () => Volatile.Read(ref _active), _refreshAndSync, _queueSend);
            _endpoint = new(tools, configuration.Port, _endpointPath, () => Volatile.Read(ref _active).Enabled, () => Volatile.Read(ref _accessKey));
            await _endpoint.StartAsync();
            Status = $"Listening at {_endpoint.Address} · {configuration.MailboxIds?.Length ?? 0} allowed mailboxes";
            await RestoreTunnelAsync();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _active, new());
            if (_endpoint is { } failed)
            {
                _endpoint = null;
                await failed.DisposeAsync();
            }
            Status = "MCP stopped: " + exception.Message;
        }
        finally { _gate.Release(); }
    }

    private async Task RotateKeyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_store is null || _disposed) return;
            Volatile.Write(ref _accessKey, await _store.RotateMcpAccessKeyAsync());
            RaisePropertyChanged(nameof(AccessKey));
            Status = "Access key replaced. Update your MCP client. " + (_endpoint is null ? "MCP is stopped." : $"Listening at {_endpoint.Address}");
        }
        catch (Exception exception) { Status = "Could not replace the access key: " + exception.Message; }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _loginCancellation?.Cancel();
        Volatile.Write(ref _active, new());
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            await DisconnectTunnelAsync();
            if (_endpoint is { } endpoint)
            {
                _endpoint = null;
                await endpoint.DisposeAsync();
            }
        }
        finally { _gate.Release(); }
    }
}
