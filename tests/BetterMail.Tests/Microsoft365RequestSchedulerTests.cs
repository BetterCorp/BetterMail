using System.Net;
using BetterMail.Core;
using BetterMail.Microsoft365;

namespace BetterMail.Tests;

public sealed class Microsoft365RequestSchedulerTests
{
    private static readonly MailAccount Account = new(
        "microsoft365", "account", "tenant", "person@example.com", "Person", ProviderCapabilities.Mail);

    [Fact]
    public async Task LimitsOneMailboxToFourConcurrentRequests()
    {
        var scheduler = new Microsoft365RequestScheduler();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximum = 0;
        var entered = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => scheduler.RunAsync(
            Account,
            "me/messages",
            async token =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                Interlocked.Increment(ref entered);
                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
                return true;
            },
            TestContext.Current.CancellationToken)).ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref entered) == 4);
        Assert.Equal(Microsoft365RequestScheduler.MailboxConcurrencyLimit, maximum);
        release.SetResult();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task RetriesThrottledResponses()
    {
        var scheduler = new Microsoft365RequestScheduler();
        var attempts = 0;

        using var response = await scheduler.SendAsync(
            Account,
            "me/messages",
            (_, _) =>
            {
                attempts++;
                var result = new HttpResponseMessage(attempts == 1
                    ? HttpStatusCode.TooManyRequests
                    : HttpStatusCode.OK);
                if (attempts == 1)
                {
                    result.Headers.RetryAfter = new(TimeSpan.Zero);
                }
                return Task.FromResult(result);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ResolvesPrimaryAndSharedMailboxKeys()
    {
        var primary = Microsoft365RequestScheduler.MailboxKey(Account, "me/messages?$top=25");
        var shared = Microsoft365RequestScheduler.MailboxKey(Account, "users/shared@example.com/messages");
        var nextPage = Microsoft365RequestScheduler.MailboxKey(
            Account,
            "https://graph.microsoft.com/v1.0/users/shared@example.com/messages?$skiptoken=next");

        Assert.NotEqual(primary, shared);
        Assert.Equal(shared, nextPage);
    }

    private static void UpdateMaximum(ref int maximum, int current)
    {
        var observed = Volatile.Read(ref maximum);
        while (current > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, current, observed);
            if (previous == observed)
            {
                return;
            }
            observed = previous;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
