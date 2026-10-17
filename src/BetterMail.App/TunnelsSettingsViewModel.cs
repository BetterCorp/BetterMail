using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterMail.Core;

namespace BetterMail.App;

public sealed class WebhookAccountChoice : ViewModelBase
{
    private readonly Func<WebhookAccountChoice, Task> _save;
    private bool _enabled;
    private bool _canEnable;
    private string _status = "Polling for mail.";
    internal WebhookAccountChoice(MailAccount account, bool supported, bool enabled, Func<WebhookAccountChoice, Task> save)
    { Account = account; _canEnable = supported; _enabled = enabled; _save = save; }
    public MailAccount Account { get; }
    public string Id => Account.ProviderId + ":" + Account.AccountId;
    public bool CanEnable => _canEnable;
    internal void RefreshSupport(bool supported)
    {
        if (SetProperty(ref _canEnable, supported, nameof(CanEnable))) RaisePropertyChanged(nameof(Scope));
    }
    public bool Enabled
    {
        get => _enabled;
        set { if (CanEnable && SetProperty(ref _enabled, value)) _ = _save(this); }
    }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string Scope => CanEnable ? "Updates the signed-in mailbox. Shared mailboxes continue polling."
        : Account.ProviderId == "google-workspace"
            ? "Gmail webhooks need Google Cloud Pub/Sub setup; this account continues polling."
            : "Webhooks are unavailable for this account; mail continues polling.";
}

// Owns public connectivity independently of the local MCP listener and account subscriptions.
public sealed class TunnelsSettingsViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly EncryptedMailStore? _store;
    private readonly Func<IMailWebhookProvider?> _provider;
    private readonly Func<MailAccount, Task<bool>> _syncAccount;
    private readonly Uri? _server;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _subscriptions = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, bool> _renewRequested = new();
    private WebhookAccountChoice[] _choices = [];
    private BetterTunnelsConfiguration _configuration = new();
    private MailWebhookEndpoint? _webhooks;
    private CancellationTokenSource? _loginCancellation;
    private CancellationTokenSource? _tunnelCancellation;
    private Task? _tunnelTask;
    private Task? _worker;
    private Uri? _mcpEndpoint;
    private Uri? _identity;
    private string _publicBaseUrl = "";
    private string _status = "Public connection stopped. A BetterTunnels Senior account is required.";
    private bool _initialized;
    private volatile bool _disposed;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    public TunnelsSettingsViewModel(EncryptedMailStore? store, Func<IMailWebhookProvider?>? provider = null,
        Func<MailAccount, Task<bool>>? syncAccount = null, Uri? server = null)
    {
        _store = store;
        _provider = provider ?? (() => null);
        _syncAccount = syncAccount ?? (_ => Task.FromResult(false));
        _server = server;
        SignInCommand = new(SignInAsync, () => IsAvailable);
        ConnectCommand = new(ConnectAsync, () => IsAvailable);
        DisconnectCommand = new(() => StopAsync(false), () => IsAvailable);
        SignOutCommand = new(() => StopAsync(true), () => IsAvailable);
    }
    public bool IsAvailable => _store is not null && _initialized && !_disposed;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string AccountEmail => _configuration.Email;
    public string PublicBaseUrl { get => _publicBaseUrl; private set { if (SetProperty(ref _publicBaseUrl, value)) RaiseConnectionChanged(); } }
    public string PublicMcpUrl => PublicBaseUrl.Length == 0 || _mcpEndpoint is null ? "" : PublicBaseUrl + _mcpEndpoint.AbsolutePath;
    public string Usage => $"Public MCP: {(_mcpEndpoint is null ? "off" : "enabled")} · Mail webhooks: {_choices.Count(choice => choice.Enabled)} account(s) enabled";
    public ObservableCollection<WebhookAccountChoice> Accounts { get; } = [];
    public AsyncCommand SignInCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand SignOutCommand { get; }
    public WebhookAccountChoice? ForAccount(MailAccount account) => _choices.FirstOrDefault(choice => choice.Id == account.ProviderId + ":" + account.AccountId);

    public async Task InitializeAsync()
    {
        if (_store is null || _initialized || _disposed) return;
        try
        {
            _configuration = await _store.GetBetterTunnelsConfigurationAsync();
            var mcp = await _store.GetMcpConfigurationAsync();
            // Preserve the target port used to assign existing stable public hostnames.
            if (_configuration.Port == 0)
            {
                _configuration = _configuration with { Port = mcp.Configuration.Port };
                await _store.SaveBetterTunnelsConfigurationAsync(_configuration);
            }
            if (_configuration.Port is < 1024 or > 65535) throw new InvalidDataException("Invalid saved tunnel port.");
            _identity = new($"http://127.0.0.1:{_configuration.Port}{mcp.EndpointPath}");
            _webhooks = new("/webhooks/" + mcp.EndpointPath[4..] + "/microsoft365",
                () => !_disposed && _configuration.Enabled && _choices.Any(choice => choice.CanEnable && choice.Enabled), ReceiveAsync);
            await _webhooks.StartAsync();
            _initialized = true;
            await RefreshAccountsAsync();
            RaisePropertyChanged(nameof(IsAvailable));
            RaisePropertyChanged(nameof(AccountEmail));
            foreach (var command in new[] { SignInCommand, ConnectCommand, DisconnectCommand, SignOutCommand }) command.Refresh();
            _worker = WorkAsync(_lifetime.Token);
            await RestoreAsync();
        }
        catch (Exception) { Status = "Public connectivity could not start. Local mail and polling remain available."; }
    }

    public async Task RefreshAccountsAsync()
    {
        try { await RefreshAccountsCoreAsync(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Status = "Could not refresh account webhook settings. Polling remains available."; }
    }

    private async Task RefreshAccountsCoreAsync()
    {
        if (_store is null || !_initialized || _disposed) return;
        await _subscriptions.WaitAsync(_lifetime.Token);
        try
        {
            var choices = new List<WebhookAccountChoice>();
            foreach (var account in await _store.GetAccountsAsync())
            {
                var previous = ForAccount(account);
                var saved = await _store.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId);
                var supported = _provider()?.SupportsMailWebhooks(account) == true;
                var choice = previous ?? new(account, supported, saved.Enabled, SaveChoiceAsync);
                choice.RefreshSupport(supported);
                choices.Add(choice);
            }
            _choices = choices.ToArray();
            CollectionUpdates.Reconcile(Accounts, choices, choice => choice.Id);
            RaisePropertyChanged(nameof(Usage));
        }
        finally { _subscriptions.Release(); }
        Wake();
    }

    internal void ConfigureMcp(Uri? endpoint)
    {
        _mcpEndpoint = endpoint;
        RaiseConnectionChanged();
    }

    private void RaiseConnectionChanged()
    {
        RaisePropertyChanged(nameof(PublicMcpUrl));
        RaisePropertyChanged(nameof(Usage));
        Wake();
    }

    internal void NotifySyncIdle() => Wake();

    private void Wake() { if (_wake.CurrentCount == 0) try { _wake.Release(); } catch (SemaphoreFullException) { } }

    private async Task SaveChoiceAsync(WebhookAccountChoice choice)
    {
        try
        {
            await _subscriptions.WaitAsync(_lifetime.Token);
            try
            {
                var saved = await _store!.GetMailWebhookConfigurationAsync(choice.Account.ProviderId, choice.Account.AccountId);
                await _store.SaveMailWebhookConfigurationAsync(choice.Account.ProviderId, choice.Account.AccountId,
                    saved with { Enabled = choice.Enabled });
                choice.Status = choice.Enabled ? "Waiting for public connection…" : "Polling for mail.";
            }
            finally { _subscriptions.Release(); }
            RaisePropertyChanged(nameof(Usage));
            Wake();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { choice.Status = "Could not save webhook settings. Try again; polling remains active."; }
    }

    private async Task SignInAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _loginCancellation = cancellation;
        try
        {
            Status = "Complete BetterTunnels sign-in in your browser.";
            using var client = new BetterTunnelsClient(_server);
            var account = await client.SignInAsync(uri => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), cancellation.Token);
            var package = await client.GetPackageAsync(account.Token, cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            try
            {
                await DisconnectAsync();
                _configuration = account with { Port = _configuration.Port };
                await _store!.SaveBetterTunnelsConfigurationAsync(_configuration, cancellation.Token);
                RaisePropertyChanged(nameof(AccountEmail));
                Status = $"Signed in {account.Email} · {package}. " + (package == "senior" ? "Connect to enable public features." : "Public features require the Senior package.");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "BetterTunnels sign-in cancelled or expired."; }
        catch (Exception) { Status = "Could not sign in to BetterTunnels. Try again."; }
        finally { _loginCancellation = null; Wake(); }
    }

    private async Task ConnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_configuration.Token.Length == 0) { Status = "Sign in to a BetterTunnels Senior account first."; return; }
            using var client = new BetterTunnelsClient(_server);
            if (await client.GetPackageAsync(_configuration.Token, _lifetime.Token) != "senior")
            {
                await DisconnectAsync();
                Status = "Public features require the BetterTunnels Senior package.";
                return;
            }
            _configuration = _configuration with { Enabled = true };
            await _store!.SaveBetterTunnelsConfigurationAsync(_configuration);
            await RestoreAsync();
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception) { Status = "Could not connect. Check your BetterTunnels sign-in and network."; }
        finally { _gate.Release(); Wake(); }
    }

    private async Task RestoreAsync()
    {
        await DisconnectAsync();
        if (!_configuration.Enabled || _configuration.Token.Length == 0 || _identity is null) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _tunnelCancellation = cancellation;
        _tunnelTask = RunAsync();
        async Task RunAsync()
        {
            using var client = new BetterTunnelsClient(_server, frame =>
            {
                if (!_configuration.Enabled || _disposed) throw new InvalidDataException("Public connectivity is stopped.");
                if (frame.Path?.StartsWith(_webhooks!.Address.AbsolutePath, StringComparison.Ordinal) == true)
                    return MailWebhookEndpoint.CreateLocalRequest(frame, _webhooks.Address);
                return BetterTunnelsClient.CreateLocalRequest(frame, _mcpEndpoint ?? throw new InvalidDataException("Public MCP is disabled."));
            });
            await client.RunAsync(_configuration.Token, _identity, (status, url) =>
            {
                if (_tunnelCancellation != cancellation || _disposed) return;
                Status = status.Replace("Public link", "Public connection");
                if (url.Length != 0 && PublicBaseUrl.Length == 0)
                    foreach (var choice in _choices.Where(choice => choice.Enabled))
                        _renewRequested.AddOrUpdate(choice.Id, false, (_, removed) => removed);
                PublicBaseUrl = url.Length == 0 ? "" : new Uri(url).GetLeftPart(UriPartial.Authority);
            }, cancellation.Token);
        }
    }

    private async Task StopAsync(bool signOut)
    {
        _loginCancellation?.Cancel();
        _configuration = _configuration with { Enabled = false };
        _tunnelCancellation?.Cancel();
        await _gate.WaitAsync();
        try
        {
            await DisconnectAsync();
            _configuration = signOut ? new(Port: _configuration.Port) : _configuration with { Enabled = false };
            await _store!.SaveBetterTunnelsConfigurationAsync(_configuration);
            RaisePropertyChanged(nameof(AccountEmail));
            Status = signOut ? "Signed out. Public connectivity stopped." : "Public connectivity stopped. Feature settings are saved; local MCP and polling remain available.";
        }
        catch (Exception) { Status = "Public connectivity stopped, but its saved setting could not be updated. Try again."; }
        finally { _gate.Release(); Wake(); }
    }

    private async Task DisconnectAsync()
    {
        var cancellation = _tunnelCancellation;
        _tunnelCancellation = null;
        if (cancellation is not null)
        {
            await cancellation.CancelAsync();
            if (_tunnelTask is not null) await _tunnelTask;
            cancellation.Dispose();
        }
        _tunnelTask = null;
        PublicBaseUrl = "";
    }

    internal async Task<bool> ReceiveAsync(JsonElement events, CancellationToken ct)
    {
        var accepted = false;
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("clientState", out var state) || state.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("subscriptionId", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var supplied = state.GetString()!;
            foreach (var choice in _choices)
            {
                if (!choice.Enabled || !choice.CanEnable) continue;
                var saved = await _store!.GetMailWebhookConfigurationAsync(choice.Account.ProviderId, choice.Account.AccountId, ct);
                if (!saved.Enabled || saved.ClientState.Length != 64 || supplied.Length != 64 ||
                    !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(saved.ClientState), Encoding.UTF8.GetBytes(supplied)) ||
                    (saved.SubscriptionId.Length > 0 && saved.SubscriptionId != id.GetString())) continue;
                await _store.MarkMailWebhookPendingAsync(choice.Account.ProviderId, choice.Account.AccountId, ct);
                if (item.TryGetProperty("lifecycleEvent", out var lifecycle) && lifecycle.ValueKind == JsonValueKind.String)
                    _renewRequested.AddOrUpdate(choice.Id, lifecycle.GetString() == "subscriptionRemoved",
                        (_, previous) => previous || lifecycle.GetString() == "subscriptionRemoved");
                accepted = true;
                break;
            }
        }
        if (accepted) Wake();
        return accepted;
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Coalesce a delivery burst without delaying acknowledgement of individual requests.
                await Task.Delay(200, ct);
                foreach (var choice in _choices)
                {
                    try
                    {
                        await MaintainAccountAsync(choice, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { choice.Status = "Webhook subscription needs attention; polling remains active. Check sign-in and public connectivity."; }
                    try
                    {
                        if (choice.Enabled)
                        {
                            var version = await _store!.GetPendingMailWebhookVersionAsync(choice.Account.ProviderId, choice.Account.AccountId, ct);
                            if (version != 0 && await _syncAccount(choice.Account))
                                await _store.CompleteMailWebhookVersionAsync(choice.Account.ProviderId, choice.Account.AccountId, version, ct);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { choice.Status = "Webhook refresh needs attention; polling remains active. Check sign-in and public connectivity."; }
                }
                await _wake.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task MaintainAccountAsync(WebhookAccountChoice choice, CancellationToken ct)
    {
        await _subscriptions.WaitAsync(ct);
        try
        {
            var provider = _provider();
            if (!choice.CanEnable || provider is null) return;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            operation.CancelAfter(TimeSpan.FromSeconds(20));
            var token = operation.Token;
            var account = choice.Account;
            var saved = await _store!.GetMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, token);
            var desired = choice.Enabled && saved.Enabled && _configuration.Enabled;
            if (!desired)
            {
                if (saved.SubscriptionId.Length > 0)
                {
                    await provider.DeleteMailWebhookAsync(account, saved.SubscriptionId, token);
                    await _store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId,
                        saved with { SubscriptionId = "", ExpiresAt = null, ClientState = "", NotificationUrl = "" }, token);
                }
                choice.Status = choice.Enabled ? "Waiting for public connection. Polling remains active." : "Polling for mail.";
                return;
            }
            if (PublicBaseUrl.Length == 0) { choice.Status = "Waiting for public connection. Polling remains active."; return; }
            var url = PublicBaseUrl + _webhooks!.Address.AbsolutePath;
            var forceRenew = _renewRequested.TryGetValue(choice.Id, out var removed);
            if (saved.SubscriptionId.Length > 0 && (saved.NotificationUrl != url || removed))
            {
                await provider.DeleteMailWebhookAsync(account, saved.SubscriptionId, token);
                saved = saved with { SubscriptionId = "", ExpiresAt = null, ClientState = "" };
                await _store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, saved, token);
            }
            if (saved.SubscriptionId.Length > 0 && (forceRenew || saved.ExpiresAt is null || saved.ExpiresAt < DateTimeOffset.UtcNow.AddDays(1)))
            {
                try
                {
                    var renewed = await provider.RenewMailWebhookAsync(account, saved.SubscriptionId, token);
                    saved = saved with { SubscriptionId = renewed.Id, ExpiresAt = renewed.ExpiresAt };
                    await _store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, saved, token);
                }
                catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
                {
                    saved = saved with { SubscriptionId = "", ExpiresAt = null };
                }
            }
            if (saved.SubscriptionId.Length == 0)
            {
                // Reuse a persisted creation intent after a lost response or restart. Rotating its
                // secret here would invalidate notifications from an already-created subscription.
                saved = saved with
                {
                    ClientState = saved.NotificationUrl == url && saved.ClientState.Length == 64
                        ? saved.ClientState : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    NotificationUrl = url
                };
                // Save the secret before Graph validates and delivers the initial notifications.
                await _store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, saved, token);
                var created = await provider.CreateMailWebhookAsync(account, url, saved.ClientState, token);
                saved = saved with { SubscriptionId = created.Id, ExpiresAt = created.ExpiresAt };
                await _store.SaveMailWebhookConfigurationAsync(account.ProviderId, account.AccountId, saved, token);
                await _store.MarkMailWebhookPendingAsync(account.ProviderId, account.AccountId, token);
            }
            if (forceRenew)
            {
                await _store.MarkMailWebhookPendingAsync(account.ProviderId, account.AccountId, token);
                _renewRequested.TryRemove(new KeyValuePair<string, bool>(choice.Id, removed));
            }
            choice.Status = "Webhooks active · polling recovery remains enabled.";
        }
        finally { _subscriptions.Release(); }
    }

    public async Task RemoveAccountAsync(MailAccount account)
    {
        if (ForAccount(account) is not { } choice) return;
        choice.Enabled = false;
        await SaveChoiceAsync(choice);
        try { await MaintainAccountAsync(choice, _lifetime.Token); } catch (Exception) { /* Local disable is authoritative; a remote subscription will expire. */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        _loginCancellation?.Cancel();
        await _lifetime.CancelAsync();
        await _gate.WaitAsync();
        try { await DisconnectAsync(); }
        finally { _gate.Release(); }
        if (_worker is not null) await _worker;
        if (Interlocked.Exchange(ref _webhooks, null) is { } webhooks) await webhooks.DisposeAsync();
    }
}
