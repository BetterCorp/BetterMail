function Initialize-BetterMailSigning([string]$Runtime) {
    $state = @{ Arguments = @(); Certificate = $null; Keychain = $null; TemporaryDirectory = $null }
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
            $certificate = Import-PfxCertificate -FilePath $certificateFile -CertStoreLocation Cert:\CurrentUser\My -Password $password
            if ($certificate.Thumbprint -notin $existing) { $state.Certificate = $certificate.Thumbprint }
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
    if ($State.Certificate) { Remove-Item "Cert:\CurrentUser\My\$($State.Certificate)" -ErrorAction SilentlyContinue }
    if ($State.Keychain) { security delete-keychain $State.Keychain | Out-Null }
    if ($State.TemporaryDirectory) { Remove-Item -LiteralPath $State.TemporaryDirectory -Recurse -Force -ErrorAction SilentlyContinue }
}
