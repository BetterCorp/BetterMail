using System.Diagnostics;
using BetterMail.Core;

namespace BetterMail.App;

public sealed partial class McpSettingsViewModel
{
    private CancellationTokenSource? _loginCancellation;
    private CancellationTokenSource? _tunnelCancellation;
    private Task? _tunnelTask;
    private string _tunnelStatus = "Public link stopped. A BetterTunnels Senior account is required.";
    private string _publicEndpointUrl = "";
    public string TunnelStatus { get => _tunnelStatus; private set => SetProperty(ref _tunnelStatus, value); }
    public string PublicEndpointUrl { get => _publicEndpointUrl; private set => SetProperty(ref _publicEndpointUrl, value); }
    public AsyncCommand SignInTunnelCommand { get; }
    public AsyncCommand StartTunnelCommand { get; }
    public AsyncCommand StopTunnelCommand { get; }
    public AsyncCommand SignOutTunnelCommand { get; }

    private void RefreshTunnelCommands()
    {
        SignInTunnelCommand.Refresh();
        StartTunnelCommand.Refresh();
        StopTunnelCommand.Refresh();
        SignOutTunnelCommand.Refresh();
    }

    private async Task SignInTunnelAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _loginCancellation = cancellation;
        try
        {
            TunnelStatus = "Complete BetterTunnels sign-in in your browser. Stop public link cancels sign-in.";
            using var client = new BetterTunnelsClient();
            var account = await client.SignInAsync(uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }), cancellation.Token);
            var package = await client.GetPackageAsync(account.Token, cancellation.Token);
            await _gate.WaitAsync(cancellation.Token);
            try
            {
                if (_store is null || _disposed) return;
                await DisconnectTunnelAsync();
                await _store.SaveBetterTunnelsConfigurationAsync(account, cancellation.Token);
                TunnelStatus = $"Signed in {account.Email} · {package}. " + (package == "senior" ? "Create a public link to enable remote MCP access." : "Public links require the Senior package.");
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { if (!_disposed) TunnelStatus = "BetterTunnels sign-in cancelled or expired."; }
        catch (UnauthorizedAccessException exception) { TunnelStatus = exception.Message; }
        catch (Exception) { TunnelStatus = "Could not sign in to BetterTunnels. Try again."; }
        finally { _loginCancellation = null; }
    }

    private async Task StartTunnelAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_store is null || _disposed) return;
            if (_endpoint is null || !_active.Enabled) { TunnelStatus = "Enable MCP and apply settings before creating a public link."; return; }
            var account = await _store.GetBetterTunnelsConfigurationAsync();
            if (account.Token.Length == 0) { TunnelStatus = "Sign in to a BetterTunnels Senior account first."; return; }
            using var client = new BetterTunnelsClient();
            TunnelStatus = "Checking Senior access…";
            if (await client.GetPackageAsync(account.Token, CancellationToken.None) != "senior")
            {
                await DisconnectTunnelAsync();
                TunnelStatus = "Public MCP links require the BetterTunnels Senior package.";
                return;
            }
            await _store.SaveBetterTunnelsConfigurationAsync(account with { Enabled = true });
            await RestoreTunnelAsync();
        }
        catch (UnauthorizedAccessException exception) { TunnelStatus = exception.Message; }
        catch (Exception) { TunnelStatus = "Could not create the public link. Check your connection and try again."; }
        finally { _gate.Release(); }
    }

    private async Task RestoreTunnelAsync()
    {
        await DisconnectTunnelAsync();
        var account = await _store!.GetBetterTunnelsConfigurationAsync();
        if (!account.Enabled || account.Token.Length == 0 || _endpoint is null) return;
        var cancellation = new CancellationTokenSource();
        _tunnelCancellation = cancellation;
        var endpoint = new Uri(_endpoint.Address);
        _tunnelTask = RunTunnelAsync();
        async Task RunTunnelAsync()
        {
            using var client = new BetterTunnelsClient();
            await client.RunAsync(account.Token, endpoint, (status, url) =>
            {
                if (_tunnelCancellation != cancellation || _disposed) return;
                TunnelStatus = status;
                PublicEndpointUrl = url;
            }, cancellation.Token);
        }
    }

    private async Task StopTunnelAsync(bool signOut)
    {
        _loginCancellation?.Cancel();
        // Revoke the connection immediately, even if another settings operation is awaiting the network.
        _tunnelCancellation?.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_store is null || _disposed) return;
            await DisconnectTunnelAsync();
            var account = await _store.GetBetterTunnelsConfigurationAsync();
            await _store.SaveBetterTunnelsConfigurationAsync(signOut ? new() : account with { Enabled = false });
            TunnelStatus = signOut ? "Signed out of BetterTunnels. Public link stopped." : "Public link stopped.";
        }
        catch (Exception) { TunnelStatus = "Public link stopped, but its saved setting could not be updated. Try again."; }
        finally { _gate.Release(); }
    }

    private async Task DisconnectTunnelAsync()
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
        PublicEndpointUrl = "";
        TunnelStatus = "Public link stopped.";
    }
}
