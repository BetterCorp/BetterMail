$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/signing.ps1"

function Assert-True($Value, [string]$Message) {
    if (-not $Value) { throw $Message }
}
function New-TestCertificate([string]$Name, [string]$Usage, [bool]$IsCa = $false) {
    $key = [System.Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=$Name", $key, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $oids = [System.Security.Cryptography.OidCollection]::new()
        $oids.Add([System.Security.Cryptography.Oid]::new($Usage)) | Out-Null
        $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $false))
        $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($IsCa, $false, 0, $true))
        return $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddDays(-1), [DateTimeOffset]::UtcNow.AddDays(1))
    }
    finally { $key.Dispose() }
}

# Certificate selection uses real X509 extensions. The certificate-store boundary is mocked;
# this suite never imports a certificate or requires production signing credentials.
$leaf = New-TestCertificate 'signer' '1.3.6.1.5.5.7.3.3'
$second = New-TestCertificate 'second-signer' '1.3.6.1.5.5.7.3.3'
$chain = New-TestCertificate 'chain' '1.3.6.1.5.5.7.3.3' $true
$tls = New-TestCertificate 'tls' '1.3.6.1.5.5.7.3.1'
$publicOnly = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($leaf.RawData)
$oldCertificate = $env:BETTERMAIL_WINDOWS_CERTIFICATE
$oldPassword = $env:BETTERMAIL_WINDOWS_CERTIFICATE_PASSWORD
$script:store = @()
$script:imports = @()
$script:removed = [System.Collections.Generic.List[string]]::new()
$script:failImport = $false
function Get-ChildItem { param($Path) $script:store }
function Import-PfxCertificate {
    param($FilePath, $CertStoreLocation, $Password, $ErrorAction)
    $script:store = @($script:store) + @($script:imports)
    if ($script:failImport) { throw 'Simulated partial import failure' }
    $script:imports
}
function Remove-Item {
    param($LiteralPath, [switch]$Recurse, [switch]$Force, $ErrorAction)
    if ($LiteralPath.StartsWith('Cert:')) { $script:removed.Add($LiteralPath) }
    else { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $LiteralPath -Recurse:$Recurse -Force:$Force -ErrorAction SilentlyContinue }
}
try {
    Assert-True ((Select-BetterMailSigningCertificate @($chain, $tls, $leaf)).Thumbprint -eq $leaf.Thumbprint) 'Did not select the code-signing leaf.'
    foreach ($invalid in @(@($chain, $tls), @($publicOnly), @($leaf, $second))) {
        $rejected = $false
        try { Select-BetterMailSigningCertificate $invalid | Out-Null } catch { $rejected = $true }
        Assert-True $rejected 'Invalid or ambiguous signing bundle was accepted.'
    }
    $env:BETTERMAIL_WINDOWS_CERTIFICATE = [Convert]::ToBase64String([byte[]](1, 2, 3))
    $env:BETTERMAIL_WINDOWS_CERTIFICATE_PASSWORD = 'test-only'
    $script:store = @($tls)
    $script:imports = @($leaf, $chain)
    $state = Initialize-BetterMailSigning 'win-x64'
    Assert-True ($state.Arguments[1].StartsWith("/sha1 $($leaf.Thumbprint) /fd")) 'SignTool must receive exactly one thumbprint.'
    Assert-True ($state.Certificates.Count -eq 2) 'Did not track both new certificates.'
    Remove-BetterMailSigning $state
    Assert-True ($script:removed.Count -eq 2) 'Did not clean up each new certificate.'
    Assert-True ($script:removed -contains "Cert:\CurrentUser\My\$($leaf.Thumbprint)") 'Leaf was not removed separately.'
    Assert-True ($script:removed -contains "Cert:\CurrentUser\My\$($chain.Thumbprint)") 'Chain was not removed separately.'
    Assert-True (-not (Test-Path $state.TemporaryDirectory)) 'Temporary PFX directory remains.'

    # Preserve a pre-existing signer while removing newly imported chain entries.
    $script:removed.Clear()
    $script:store = @($leaf)
    $state = Initialize-BetterMailSigning 'win-x64'
    Remove-BetterMailSigning $state
    Assert-True ($script:removed.Count -eq 1 -and $script:removed[0].EndsWith($chain.Thumbprint)) 'Pre-existing certificate was removed.'

    # Both selection failures and partial import failures must still clean up new entries.
    foreach ($partial in @($false, $true)) {
        $script:store = @($tls)
        $script:imports = @($leaf, $second, $chain)
        $script:removed.Clear()
        $script:failImport = $partial
        $rejected = $false
        try { Initialize-BetterMailSigning 'win-x64' | Out-Null } catch { $rejected = $true }
        Assert-True $rejected 'Expected bundle rejection/import failure.'
        Assert-True ($script:removed.Count -eq 3) 'Failure did not clean up every newly imported certificate.'
    }
    Write-Host 'Signing regression checks passed.'
}
finally {
    $env:BETTERMAIL_WINDOWS_CERTIFICATE = $oldCertificate
    $env:BETTERMAIL_WINDOWS_CERTIFICATE_PASSWORD = $oldPassword
    foreach ($certificate in @($leaf, $second, $chain, $tls, $publicOnly)) { $certificate.Dispose() }
}
