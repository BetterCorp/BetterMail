using BetterMail.App;
using Velopack.Sources;

namespace BetterMail.Tests;

public sealed class AppUpdaterTests
{
    [Fact]
    public void SecondDistinctReleaseRequiresRestart()
    {
        var tracker = new UpdateReleaseTracker();

        Assert.False(tracker.Observe("1.1.0"));
        Assert.False(tracker.Observe("1.1.0"));
        Assert.True(tracker.Observe("1.2.0"));
        Assert.True(tracker.Observe("1.2.0"));
    }

    [Fact]
    public async Task ReusesOneDownloadPerReleaseAndSerializesNewReleases()
    {
        var staging = new UpdateStaging();
        var releaseFirst = new TaskCompletionSource<bool>();
        var starts = 0;
        var first = staging.Start("1.1.0", async _ =>
        {
            Interlocked.Increment(ref starts);
            await releaseFirst.Task;
            return null;
        });

        var duplicate = staging.Start("1.1.0", _ =>
            throw new InvalidOperationException("Duplicate download started."));
        var second = staging.Start("1.2.0", _ =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult<Exception?>(null);
        });

        Assert.Same(first, duplicate);
        Assert.Equal(1, starts);
        releaseFirst.SetResult(true);
        await second.Completion;
        Assert.Equal(2, starts);
    }

    [Fact]
    public void UsesProductionReleaseFeedAndDailyChecks()
    {
        var source = Assert.IsType<SimpleWebSource>(AppUpdater.CreateSource());

        Assert.Equal("https://github.com/BetterCorp/BetterMail/releases/latest/download", AppUpdater.UpdateFeedUrl);
        Assert.Equal(AppUpdater.UpdateFeedUrl, source.BaseUri.ToString().TrimEnd('/'));
        Assert.Equal(TimeSpan.FromHours(24), AppUpdater.CheckInterval);
    }

    [Fact]
    public void StartsOneBackgroundDownloadBeforeOfferingNowOrLater()
    {
        var root = FindRepositoryRoot();
        var updater = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "AppUpdater.cs"));
        var window = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "UpdateWindow.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "BetterMail.App", "Program.cs"));

        Assert.True(updater.IndexOf("var download = StartStaging(update)", StringComparison.Ordinal) <
            updater.IndexOf("UpdateWindow.PromptAsync", StringComparison.Ordinal));
        Assert.Contains("await download.Completion", updater);
        Assert.Contains("Later keeps downloading", window);
        Assert.Contains("SetAutoApplyOnStartup(true)", program);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BetterMail.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
