# BetterTunnels integration proposal

## Current connection issue

The released v0.2.41 BetterMail endpoint accepts authenticated MCP initialization on
`http://127.0.0.1:47831/mcp`. The configured HTTPS tunnel returns an empty HTTP 403,
including with the correct access key. A local request using the tunnel's public
Host header reproduces that rejection: BetterMail accepts only its loopback host
and port.

BetterTunnels already supports overriding the Host header forwarded to the local
service. For the current CLI, use a `.bettertunnel.json` entry and run `btunnel up`
from its directory:

```json
{
  "tunnels": [
    {
      "name": "BetterMail MCP",
      "host": "127.0.0.1",
      "port": 47831,
      "host_header": "127.0.0.1:47831"
    }
  ]
}
```

Use the port configured in BetterMail for both `port` and `host_header`. Log in to
a Senior account first. Senior policy automatically removes visitor validation;
do not set `validation: "none"` in this CLI config, whose explicit validation
values currently accept only `cookie` or `ip`.

The client still sends `Authorization: Bearer <BetterMail access key>`. Copy the
generated public URL, append the entire `/bm/<installation-token>` path copied
from BetterMail settings, then update and restart the client's MCP connection.
Version 0.2.41 uses `/mcp`; version 0.2.42 and later use the private path.
This tunnel configuration is a proposed fix; it has not yet been applied to
the running tunnel or verified end to end.

## Proposed BetterMail setup

Keep local MCP free and independent. Add an optional **Public MCP link** section
under Settings > MCP:

1. **Connect BetterTunnels account** opens the existing browser sign-in flow.
2. Read `/api/client/profile` and show the account package. Enable link creation
   only for `package: "senior"`. Other packages see the Senior requirement.
3. **Create public link** explicitly enables remote access and starts the tunnel
   to BetterMail's configured loopback MCP port.
4. Show connection status, the public `/bm/<installation-token>` URL, **Copy link**, and the existing
   **Copy header value** action. Keep the access key out of the URL.
5. **Stop public link** disconnects the tunnel without disabling local MCP.
6. Stop the tunnel when MCP is disabled or BetterMail exits. Restore an explicitly
   enabled link on startup after checking the account entitlement again.

The private path is generated once per mail database using 32 cryptographically
random bytes. It is independent of the tunnel hostname and the rotatable MCP key;
link reconnects, app updates, and key rotation must never regenerate it. There is
no generic `/mcp` alias. A fresh database creates a new installation identity.

Changing a port restarts the tunnel. Reconnect after temporary network failures;
recheck entitlement on reconnect and stop public access if Senior access is lost.
Local mail continues working while the tunnel is unavailable.

## Implementation boundary

Reuse the existing single-file `btunnel` client as a bundled, version-pinned helper
for Windows, Linux, and macOS. BetterMail owns its lifetime and launches it without
a terminal window. Avoid implementing the tunnel relay protocol again in C#.

The CLI currently prints human-readable status. Add a small machine-readable mode
for sign-in, account/package status, connected URL, and connection errors before
integrating it; do not parse console prose. Keep BetterMail's integration credentials
in encrypted storage, separate from the MCP access key. The helper integration must
support that storage boundary rather than introducing plaintext tokens in arguments
or config files.

The local forwarding target and Host override are fixed to BetterMail's MCP listener.
Keep MCP bearer authentication and the existing mailbox/edit/send permissions. Do
not broadly relax Host or Origin validation. Clients that supply an Origin header
need an explicitly designed validation path; verify this with the target ChatGPT
client before publishing integration support.

The BetterTunnels backend already enforces Senior's stable, non-expiring,
visitor-validation-free policy (`src/accounts.ts`). BetterMail should use the
server-reported entitlement, not a local package selector. A stable link remains
reachable only while BetterMail and the tunnel are running.

## Verification before release

- Senior sign-in creates a working HTTPS MCP link; other packages cannot create it.
- An actual external MCP client initializes, discovers tools, and performs read-only
  mailbox calls through the tunnel without visitor-validation interstitials.
- Missing/incorrect MCP keys and unauthorized mailbox calls remain rejected; key
  replacement invalidates the old key through both local and public endpoints.
- Port changes, reconnects, entitlement loss, disabling MCP, and app exit manage the
  helper without leaving a public listener or an orphan process.
- UI setup distinguishes MCP server registration from attaching a desktop window.
