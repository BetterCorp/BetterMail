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
