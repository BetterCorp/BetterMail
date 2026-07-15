using System.Collections.Concurrent;
using System.Net;
using BetterMail.Core;

namespace BetterMail.Microsoft365;

internal sealed class Microsoft365RequestScheduler
{
    internal const int MailboxConcurrencyLimit = 4;
    internal const int RetryLimit = 3;
    internal static Microsoft365RequestScheduler Shared { get; } = new();

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mailboxes = new(StringComparer.Ordinal);

    internal async Task<T> RunAsync<T>(
        MailAccount account,
        string endpoint,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var gate = _mailboxes.GetOrAdd(
            MailboxKey(account, endpoint),
            static _ => new SemaphoreSlim(MailboxConcurrencyLimit, MailboxConcurrencyLimit));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task RunAsync(
        MailAccount account,
        string endpoint,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken) =>
        await RunAsync(account, endpoint, async token =>
        {
            await action(token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

    internal async Task<HttpResponseMessage> SendAsync(
        MailAccount account,
        string endpoint,
        Func<int, CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        return await RunAsync(account, endpoint, async token =>
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var response = await send(attempt, token).ConfigureAwait(false);
                    if (attempt >= RetryLimit || !IsTransient(response.StatusCode))
                    {
                        return response;
                    }

                    var delay = RetryDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
                catch (HttpRequestException exception) when (
                    attempt < RetryLimit &&
                    (exception.StatusCode is null || IsTransient(exception.StatusCode.Value)))
                {
                    await Task.Delay(FallbackDelay(attempt), token).ConfigureAwait(false);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string MailboxKey(MailAccount account, string endpoint)
    {
        var path = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : endpoint.Split('?', 2)[0];
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var start = segments.Length > 0 && segments[0] is "v1.0" or "beta" ? 1 : 0;
        var mailbox = segments.Length > start && segments[start].Equals("users", StringComparison.OrdinalIgnoreCase) &&
                      segments.Length > start + 1
            ? $"users/{segments[start + 1]}"
            : "me";
        return $"{account.ProviderId}:{account.AccountId}:{mailbox.ToLowerInvariant()}";
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }
        return FallbackDelay(attempt);
    }

    private static TimeSpan FallbackDelay(int attempt) => TimeSpan.FromSeconds(1 << attempt);

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
}
