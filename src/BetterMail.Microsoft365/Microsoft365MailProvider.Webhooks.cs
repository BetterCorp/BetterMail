using System.Net;
using BetterMail.Core;

namespace BetterMail.Microsoft365;

public sealed partial class Microsoft365MailProvider
{
    public bool SupportsMailWebhooks(MailAccount account) => account.ProviderId == Microsoft365AuthService.Id;

    public async Task<MailWebhookSubscription> CreateMailWebhookAsync(MailAccount account, string notificationUrl,
        string clientState, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(notificationUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Mail webhooks require an HTTPS notification URL.", nameof(notificationUrl));
        try
        {
            return await CreateMailWebhookCoreAsync(account, notificationUrl, clientState, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Conflict)
        {
            if (!await RemoveMatchingMailWebhookAsync(account, notificationUrl, cancellationToken).ConfigureAwait(false)) throw;
            return await CreateMailWebhookCoreAsync(account, notificationUrl, clientState, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<MailWebhookSubscription> CreateMailWebhookCoreAsync(MailAccount account, string notificationUrl,
        string clientState, CancellationToken cancellationToken)
    {
        // Basic notifications keep message content on the normal authenticated Graph/delta path.
        using var document = await SendJsonForResponseAsync(account, HttpMethod.Post, "subscriptions", new
        {
            changeType = "created,updated,deleted",
            notificationUrl,
            lifecycleNotificationUrl = notificationUrl,
            resource = "me/messages",
            expirationDateTime = DateTimeOffset.UtcNow.AddDays(6),
            clientState,
            includeResourceData = false
        }, cancellationToken).ConfigureAwait(false);
        return new(document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("expirationDateTime").GetDateTimeOffset());
    }

    private async Task<bool> RemoveMatchingMailWebhookAsync(MailAccount account, string notificationUrl, CancellationToken ct)
    {
        var collection = new Uri(_httpClient.BaseAddress!, "subscriptions");
        string? endpoint = "subscriptions";
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; endpoint is not null && page < 100 && visited.Add(endpoint); page++)
        {
            using var document = await GetJsonAsync(account, endpoint, ct).ConfigureAwait(false);
            foreach (var subscription in document.RootElement.GetProperty("value").EnumerateArray())
            {
                if (subscription.GetProperty("notificationUrl").GetString() != notificationUrl ||
                    subscription.GetProperty("resource").GetString() != "me/messages" ||
                    !new HashSet<string>((subscription.GetProperty("changeType").GetString() ?? "").Split(','), StringComparer.OrdinalIgnoreCase)
                        .SetEquals(["created", "updated", "deleted"])) continue;
                // Graph omits clientState on reads. Recreate only this installation's exact URL/resource
                // instead of adopting a subscription whose validation secret cannot be verified.
                await DeleteMailWebhookAsync(account, subscription.GetProperty("id").GetString()!, ct).ConfigureAwait(false);
                return true;
            }
            endpoint = document.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            if (endpoint is not null && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var nextUri) ||
                nextUri.Scheme != collection.Scheme || nextUri.Authority != collection.Authority ||
                nextUri.AbsolutePath != collection.AbsolutePath || nextUri.UserInfo.Length != 0 || nextUri.Fragment.Length != 0))
                throw new InvalidDataException("Graph returned an invalid subscription page link.");
        }
        return false;
    }

    public async Task<MailWebhookSubscription> RenewMailWebhookAsync(MailAccount account, string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        using var document = await SendJsonForResponseAsync(account, HttpMethod.Patch,
            "subscriptions/" + Uri.EscapeDataString(subscriptionId),
            new { expirationDateTime = DateTimeOffset.UtcNow.AddDays(6) }, cancellationToken).ConfigureAwait(false);
        return new(document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("expirationDateTime").GetDateTimeOffset());
    }

    public async Task DeleteMailWebhookAsync(MailAccount account, string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(account, HttpMethod.Delete,
            "subscriptions/" + Uri.EscapeDataString(subscriptionId), null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound)
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }
}
