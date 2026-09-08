# BetterTunnels MCP setup

BetterMail includes BetterTunnels under **Settings > MCP > BetterTunnels public MCP link**.
A **Senior** account is required for a stable HTTPS link without visitor sign-in.
Local MCP does not require a BetterTunnels account.

## Connect

1. Enable MCP, choose allowed mailboxes and permissions, and **Apply MCP settings**.
2. Select **Sign in to BetterTunnels** and complete sign-in in your browser.
   BetterMail checks your account package with BetterTunnels.
3. Select **Create public link** to explicitly enable remote access.
4. Once connected, select **Copy public link**. Add that entire HTTPS URL as a
   Streamable HTTP MCP server in your client.
5. Set the HTTP header name to `Authorization`. Use **Copy header value** for its
   value (`Bearer ` followed by your current BetterMail access key).
6. Save and restart your client's MCP connection.

The URL ends in `/bm/<installation-token>`. That private path is generated once
from 32 cryptographically random bytes and saved in the encrypted mail database.
Restarts, app updates, port changes, and access-key rotation preserve it. The MCP
key is never included in the URL. Changing the local port can change the tunnel
hostname, so copy the new complete URL afterward.

Attaching the BetterMail desktop window selects computer control; it does not
register an MCP server. Use your client's MCP server setup instead.

## Stop, reconnect, and sign out

- **Stop public link** immediately disconnects remote access and disables automatic
  reconnection. Local MCP stays available. It also cancels pending browser sign-in.
- **Sign out** stops the link and removes BetterMail's saved BetterTunnels credential.
- Disabling MCP or exiting BetterMail disconnects the tunnel. An explicitly enabled
  public link resumes when MCP runs again, after verifying Senior access.
- Temporary network failures reconnect automatically. The app checks Senior access
  on every reconnect and once per minute while connected; an invalid session or
  lost entitlement stops access. If the account session expires or the network's
  public IP changes, sign in again and create the link again.
- BetterMail must remain open and online. A stable hostname does not host mail
  independently of the app.

## Security and implementation

BetterTunnels credentials are stored in the encrypted mail database separately
from the rotatable MCP key. No CLI installation, child process, token arguments,
or plaintext token file is required. BetterMail uses .NET's HTTP and WebSocket
clients with the BetterTunnels v1 HTTP relay protocol.

Forwarding is restricted to the exact private MCP path and fixed loopback target.
The relay sets the local Host header correctly, preserving MCP's loopback checks.
MCP still verifies the client's bearer key, allowed mailboxes, and write/send
permissions. Origin headers are preserved: an arbitrary browser origin is rejected.
Clients must support server-side Streamable HTTP and a configured authorization
header; this does not enable direct cross-origin browser JavaScript access.

Requests and frames are bounded, HTTP responses stream through the tunnel, and
cancellation/shutdown closes pending requests. HTTP redirects are not followed.
The integration does not expose other local services or relay arbitrary WebSockets.

Automated tests use a local BetterTunnels protocol server and a real BetterMail MCP
listener to verify sign-in, Senior gating, authenticated initialization, bad-key and
Origin rejection, key rotation, path restrictions, reconnect, and entitlement loss.
A live Senior-account browser sign-in and external ChatGPT connection still need
verification against the hosted service.
