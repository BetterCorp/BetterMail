param(
    [ValidateSet("win-x64", "linux-x64", "all")]
    [string]$Runtime = "all"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\BetterMail.App\BetterMail.App.csproj"
$runtimes = if ($Runtime -eq "all") { @("win-x64", "linux-x64") } else { @($Runtime) }

foreach ($rid in $runtimes) {
    $output = Join-Path $PSScriptRoot "..\artifacts\$rid"
    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    dotnet publish $project `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:UsedAvaloniaProducts= `
        --output $output `
        --disable-build-servers `
        -m:1

    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }
}

$legacyWindowsOutput = Join-Path $PSScriptRoot "..\artifacts\win-x64-update"
if (Test-Path -LiteralPath $legacyWindowsOutput) {
    Remove-Item -LiteralPath $legacyWindowsOutput -Recurse -Force
}
