function Select-BetterMailSigningCertificate($Certificates) {
    $candidates = @($Certificates | Where-Object {
        $eku = @($_.Extensions | Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] })
        $ca = @($_.Extensions | Where-Object {
            $_ -is [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension] -and $_.CertificateAuthority
        })
        $_.HasPrivateKey -and $ca.Count -eq 0 -and
            @($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -gt 0
    } | Sort-Object Thumbprint -Unique)
    if ($candidates.Count -ne 1) {
        throw 'The Windows PFX must contain exactly one non-CA code-signing certificate with a private key.'
    }
    return $candidates[0]
}

function Initialize-BetterMailSigning([string]$Runtime) {
    $state = @{ Arguments = @(); Certificates = @(); Keychain = $null; TemporaryDirectory = $null }
    $names = switch ($Runtime) {
        'win-x64' { @('BETTERMAIL_WINDOWS_CERTIFICATE', 'BETTERMAIL_WINDOWS_CERTIFICATE_PASSWORD') }
        'osx-arm64' { @('BETTERMAIL_MACOS_CERTIFICATE', 'BETTERMAIL_MACOS_CERTIFICATE_PASSWORD', 'BETTERMAIL_MACOS_APP_IDENTITY', 'BETTERMAIL_MACOS_INSTALLER_IDENTITY', 'BETTERMAIL_APPLE_ID', 'BETTERMAIL_APPLE_TEAM_ID', 'BETTERMAIL_APPLE_APP_PASSWORD') }
        default { @() }
    }
    $configured = @($names | Where-Object { -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_)) })
    if ($configured.Count -eq 0) {
        Write-Host "Signing is not configured for $Runtime; creating an unsigned development package."
        return $state
    }
    if ($configured.Count -ne $names.Count) { throw "Signing configuration for $Runtime is incomplete." }
    $state.TemporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('bettermail-sign-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $state.TemporaryDirectory | Out-Null
    if ($IsMacOS) { chmod 700 $state.TemporaryDirectory }
    try {
        $certificateFile = Join-Path $state.TemporaryDirectory 'certificate.pfx'
        if ($Runtime -eq 'win-x64') {
            [IO.File]::WriteAllBytes($certificateFile, [Convert]::FromBase64String($env:BETTERMAIL_WINDOWS_CERTIFICATE))
            $password = ConvertTo-SecureString $env:BETTERMAIL_WINDOWS_CERTIFICATE_PASSWORD -AsPlainText -Force
            $existing = @(Get-ChildItem Cert:\CurrentUser\My | Select-Object -ExpandProperty Thumbprint)
            try {
                $certificates = @(Import-PfxCertificate -FilePath $certificateFile -CertStoreLocation Cert:\CurrentUser\My -Password $password -ErrorAction Stop)
            }
            finally {
                # Include chain certificates and partial imports, while preserving pre-existing entries.
                $state.Certificates = @(Get-ChildItem Cert:\CurrentUser\My |
                    Where-Object { $_.Thumbprint -notin $existing } |
                    Select-Object -ExpandProperty Thumbprint -Unique)
            }
            $certificate = Select-BetterMailSigningCertificate $certificates
            $state.Arguments = @('--signParams', "/sha1 $($certificate.Thumbprint) /fd sha256 /td sha256 /tr https://timestamp.digicert.com")
        }
        else {
            [IO.File]::WriteAllBytes($certificateFile, [Convert]::FromBase64String($env:BETTERMAIL_MACOS_CERTIFICATE))
            chmod 600 $certificateFile
            $state.Keychain = Join-Path $state.TemporaryDirectory 'signing.keychain-db'
            $password = [Guid]::NewGuid().ToString('N')
            security create-keychain -p $password $state.Keychain | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not create signing keychain.' }
            security unlock-keychain -p $password $state.Keychain | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not unlock signing keychain.' }
            security import $certificateFile -k $state.Keychain -P $env:BETTERMAIL_MACOS_CERTIFICATE_PASSWORD -T /usr/bin/codesign -T /usr/bin/productbuild | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not import signing identities.' }
            security set-key-partition-list -S apple-tool:,apple: -s -k $password $state.Keychain | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not authorize signing tools.' }
            xcrun notarytool store-credentials bettermail --keychain $state.Keychain --apple-id $env:BETTERMAIL_APPLE_ID --team-id $env:BETTERMAIL_APPLE_TEAM_ID --password $env:BETTERMAIL_APPLE_APP_PASSWORD | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not configure notarization.' }
            $state.Arguments = @('--signAppIdentity', $env:BETTERMAIL_MACOS_APP_IDENTITY,
                '--signInstallIdentity', $env:BETTERMAIL_MACOS_INSTALLER_IDENTITY,
                '--keychain', $state.Keychain, '--notaryProfile', 'bettermail')
        }
        return $state
    }
    catch {
        Remove-BetterMailSigning $state
        throw
    }
}

function Remove-BetterMailSigning($State) {
    foreach ($thumbprint in $State.Certificates) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
    }
    if ($State.Keychain) { security delete-keychain $State.Keychain | Out-Null }
    if ($State.TemporaryDirectory) { Remove-Item -LiteralPath $State.TemporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
