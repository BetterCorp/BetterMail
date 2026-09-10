# Release signing

Release packaging supports optional signing through Velopack. Existing builds remain unsigned until
credentials are configured. Partial configuration fails the build; certificates and temporary keychains
are removed in a `finally` block. PR checks never receive signing or OAuth credentials.

## Windows

Set GitHub Actions secrets `BETTERMAIL_WINDOWS_CERTIFICATE` (base64 of a PFX with private key) and
`BETTERMAIL_WINDOWS_CERTIFICATE_PASSWORD`. The packaging script imports it into the current user's
certificate store, selects exactly one non-CA certificate with a private key and code-signing EKU,
passes that single thumbprint to Velopack, and timestamps signatures with SHA-256. A PFX may
include its chain; bundles with no eligible signer or multiple eligible signers fail explicitly.
Cleanup removes each newly imported certificate separately, including chain entries and partial
imports, while preserving certificates already present in the store.
This path requires an exportable certificate; hardware/cloud-managed signing requires adapting the
signing integration to your provider's supported signing tool.

## macOS

Set these GitHub Actions secrets:

- `BETTERMAIL_MACOS_CERTIFICATE`: base64 of a PKCS#12 bundle containing both Developer ID Application
  and Developer ID Installer identities and their private keys.
- `BETTERMAIL_MACOS_CERTIFICATE_PASSWORD`: password for that bundle.
- `BETTERMAIL_MACOS_APP_IDENTITY` and `BETTERMAIL_MACOS_INSTALLER_IDENTITY`: the signing identity names.
- `BETTERMAIL_APPLE_ID`, `BETTERMAIL_APPLE_TEAM_ID`, `BETTERMAIL_APPLE_APP_PASSWORD`: notarization credentials.

Packaging creates a private temporary keychain, imports the identities, stores the notary profile there,
and lets Velopack sign and notarize all relevant assets. No production credentials are committed.

## Verification

After provisioning credentials, run a build and verify Windows installer Authenticode signatures and
macOS `codesign --verify --deep --strict`, `spctl --assess`, and stapled notarization tickets on downloaded
artifacts. Signed packaging cannot be validated without those credentials. The current unsigned path
continues to support development releases.

Reference: [Velopack signing and notarization](https://docs.velopack.io/packaging/signing).
