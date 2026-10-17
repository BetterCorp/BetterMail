# Mail webhook delivery

**Settings > Accounts > Enable mail webhooks** opts each supported account into
provider change notifications. **Manage Tunnels/Web** opens the shared BetterTunnels
connection settings. A connected Senior account and an open, online BetterMail app
are required. Remote MCP does not need to be enabled.

Microsoft 365 signed-in mailboxes are supported. Their normal delegated mail token
creates a basic Graph subscription to `me/messages` for created, updated, and deleted
messages. Shared mailboxes continue polling: their subscriptions require application
permissions, which BetterMail's delegated sign-in does not provide. Gmail continues
polling; Gmail push requires Google Cloud Pub/Sub provisioning, which is not yet
implemented.

Notifications contain change metadata, not downloaded message content. BetterMail
validates the subscription/client secret, persists a versioned pending-refresh marker,
and acknowledges the webhook before running incremental sync for the signed-in
mailbox. Existing delta/history cursors remain authoritative. Pending work is retained
while the app is busy, on refresh failure, and across restart; an event received during
a refresh cannot be cleared by the earlier refresh completing.

Subscriptions renew before expiry and respond to lifecycle reauthorization/removal
notifications. Reconnection, expired subscriptions, and changed notification URLs are
reconciled automatically. Disabling account webhooks removes the remote subscription;
removing an account attempts cleanup before signing out. Disconnecting Tunnels/Web
preserves the account's opt-in choice and stops public delivery. If remote cleanup
fails, local validation still rejects disabled accounts and remote subscriptions expire.

Periodic mail sync remains enabled at its existing interval as recovery for missed,
delayed, or unavailable webhook delivery. Webhooks do not guarantee instant mail.

Provider references:

- [Microsoft Graph Outlook change notifications](https://learn.microsoft.com/en-us/graph/outlook-change-notifications-overview)
- [Graph webhook validation and acknowledgement](https://learn.microsoft.com/en-us/graph/change-notifications-delivery-webhooks)
- [Graph subscription lifetime and latency](https://learn.microsoft.com/en-us/graph/api/resources/subscription)
- [Gmail push and Pub/Sub requirements](https://developers.google.com/workspace/gmail/api/guides/push)
