# BetterTunnels public connectivity

BetterMail manages BetterTunnels in **Settings > Tunnels/Web**. A **Senior** account
is required for public HTTPS access without visitor sign-in. BetterMail must remain
open and online; the tunnel does not host mail independently of the app.

## Connect

1. Open **Settings > Tunnels/Web**, select **Sign in to BetterTunnels**, and complete
   browser sign-in. BetterMail checks your account package.
2. Select **Connect**. The page shows the public connection status and which features
   use it. Enabled connections resume on startup and reconnect after network failures.
3. Enable the features you want separately:
   - **MCP:** enable the local endpoint, choose permitted mailboxes/accounts and
     permissions, enable **Public MCP access**, and apply MCP settings. Copy the
     complete public MCP URL from the MCP page. Configure your MCP client's
     `Authorization` header with **Copy header value** (`Bearer ` plus the access key).
   - **Accounts:** enable **mail webhooks** for each supported account. See
     [mail webhook delivery](mail-webhooks.md).

Both feature pages link to Tunnels/Web. Public MCP, account webhooks, both, or neither
can be enabled. Local MCP works independently of public connectivity.

## Stop, reconnect, and sign out

- Disabling public MCP removes public MCP access while leaving webhooks connected.
- Disabling an account's webhooks removes its subscription while leaving MCP connected.
- **Disconnect** stops both public features and disables automatic reconnection.
  Their individual choices are preserved. Local MCP and periodic mail sync continue.
- **Sign out** also removes the saved BetterTunnels credential.
- Temporary network failures reconnect automatically. BetterMail checks Senior access
  on each reconnect and once per minute while connected. Lost entitlement or an
  invalid session stops the connection. If the session expires or your public IP
  changes, sign in again and connect.
- Closing BetterMail disconnects public access. On resume, normal sync catches up and
  enabled webhook subscriptions are renewed or recreated as necessary.

Existing enabled public MCP links migrate with public MCP access enabled. Their
private path and saved tunnel identity remain stable. Changing the local MCP port
does not change the tunnel identity. MCP permissions and the bearer key still apply.

## Security and implementation

BetterTunnels credentials and webhook subscription secrets are stored in the encrypted
mail database, separately from the rotatable MCP key. No CLI installation, child
process, token arguments, or plaintext token file is required. BetterMail uses .NET
HTTP/WebSocket clients with the BetterTunnels v1 HTTP relay protocol.

The shared connection forwards only explicitly permitted routes to fixed loopback
targets. Public MCP must be enabled independently; evidence routes remain GET-only
with validated record IDs. Webhooks have their own POST-only route and provider
validation, without access to MCP tools or its key. Only the webhook validation
query `validationToken` is permitted; arbitrary paths and query strings are rejected.
Origin headers are preserved and arbitrary browser origins are rejected.

Requests/frames are bounded, responses stream through the relay, and shutdown closes
pending requests. Redirects are not followed. Other local services and arbitrary
WebSockets are not exposed.

Automated tests use local HTTP/WebSocket servers and real listeners to check all four
feature combinations, independent disablement, subscription lifecycle recovery,
validation, bearer authentication, forwarding restrictions, reconnect, and entitlement
loss. Hosted BetterTunnels/Graph delivery still needs verification with a real account.
