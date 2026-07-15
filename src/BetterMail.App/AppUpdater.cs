using Avalonia.Controls;
using Velopack;
using Velopack.Sources;

namespace BetterMail.App;

internal sealed class AppUpdater : IDisposable
{
    internal const string RepositoryUrl = "https://github.com/BetterCorp/BetterMail";
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly UpdateManager _manager = CreateManager();
    private readonly Func<Task> _gracefulShutdown;
    private readonly UpdateReleaseTracker _releases = new();
    private readonly UpdateStaging _staging = new();
    private readonly CancellationTokenSource _stopping = new();

    private AppUpdater(Func<Task> gracefulShutdown)
    {
        _gracefulShutdown = gracefulShutdown;
    }

    public static AppUpdater? Create(Func<Task> gracefulShutdown)
    {
        var updater = new AppUpdater(gracefulShutdown);
        if (updater._manager.IsInstalled)
        {
            return updater;
        }

        updater.Dispose();
        return null;
    }

    public async Task StartAsync(Window owner)
    {
        var update = await CheckQuietlyAsync();
        if (update is not null)
        {
            _releases.Observe(update.TargetFullRelease.Version.ToString());
            var download = StartStaging(update);
            if (await UpdateWindow.PromptAsync(owner, update.TargetFullRelease.Version.ToString()))
            {
                await FinishUpdateNowAsync(owner, update.TargetFullRelease, download);
            }
        }

        _ = MonitorAsync();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private static UpdateManager CreateManager() => new(
        new GithubSource(RepositoryUrl, null, false, null),
        null,
        null);

    private async Task<UpdateInfo?> CheckQuietlyAsync()
    {
        try
        {
            return await _manager.CheckForUpdatesAsync();
        }
        catch
        {
            return null;
        }
    }

    private UpdateDownload StartStaging(UpdateInfo update) => _staging.Start(
        update.TargetFullRelease.Version.ToString(),
        progress => DownloadAsync(update, progress));

    private async Task<Exception?> DownloadAsync(UpdateInfo update, Action<int> progress)
    {
        try
        {
            await _manager.DownloadUpdatesAsync(update, progress, _stopping.Token);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task FinishUpdateNowAsync(
        Window owner,
        VelopackAsset release,
        UpdateDownload download)
    {
        var progressWindow = new UpdateWindow(release.Version.ToString());
        download.ProgressChanged += progressWindow.ReportProgress;
        progressWindow.ReportProgress(download.Progress);
        var closed = progressWindow.ShowDialog(owner);
        var error = await download.Completion;
        if (error is not null)
        {
            progressWindow.ShowError(error.Message);
            await closed;
            return;
        }

        await RestartAsync(release);
    }

    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token))
            {
                var update = await CheckQuietlyAsync();
                if (update is null)
                {
                    continue;
                }

                var mustRestart = _releases.Observe(update.TargetFullRelease.Version.ToString());
                var error = await StartStaging(update).Completion;
                if (error is null && mustRestart && _manager.UpdatePendingRestart is { } pending)
                {
                    await RestartAsync(pending);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RestartAsync(VelopackAsset release)
    {
        _manager.WaitExitThenApplyUpdates(
            release,
            silent: true,
            restart: true,
            Environment.GetCommandLineArgs()[1..]);
        await _gracefulShutdown();
    }
}

internal sealed class UpdateReleaseTracker
{
    private readonly HashSet<string> _versions = new(StringComparer.Ordinal);

    public bool Observe(string version)
    {
        _versions.Add(version);
        return _versions.Count >= 2;
    }
}

internal sealed class UpdateStaging
{
    private readonly object _sync = new();
    private UpdateDownload? _current;

    public UpdateDownload Start(
        string version,
        Func<Action<int>, Task<Exception?>> download)
    {
        lock (_sync)
        {
            if (_current?.Version == version)
            {
                return _current;
            }

            _current = new UpdateDownload(version, _current?.Completion, download);
            return _current;
        }
    }
}

internal sealed class UpdateDownload
{
    private int _progress;

    public UpdateDownload(
        string version,
        Task<Exception?>? previous,
        Func<Action<int>, Task<Exception?>> download)
    {
        Version = version;
        Completion = RunAsync(previous, download);
    }

    public event Action<int>? ProgressChanged;

    public string Version { get; }
    public int Progress => Volatile.Read(ref _progress);
    public Task<Exception?> Completion { get; }

    private async Task<Exception?> RunAsync(
        Task<Exception?>? previous,
        Func<Action<int>, Task<Exception?>> download)
    {
        if (previous is not null)
        {
            await previous;
        }

        return await download(ReportProgress);
    }

    private void ReportProgress(int progress)
    {
        Volatile.Write(ref _progress, progress);
        ProgressChanged?.Invoke(progress);
    }
}
