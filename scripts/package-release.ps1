param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "linux-x64", "osx-arm64")]
    [string]$Runtime,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputDirectory = "artifacts/release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repositoryRoot "artifacts/publish/$Runtime"
$releaseDirectory = Join-Path $repositoryRoot "$OutputDirectory/$Runtime"
$mainExecutable = if ($Runtime -eq "win-x64") { "BetterMail.exe" } else { "BetterMail" }
$icon = switch ($Runtime) {
    "win-x64" { Join-Path $repositoryRoot "asset-pack/04-desktop/windows/BetterMail.ico" }
    "linux-x64" { Join-Path $repositoryRoot "asset-pack/04-desktop/linux/hicolor/512x512/apps/bettermail.png" }
    "osx-arm64" { Join-Path $repositoryRoot "asset-pack/04-desktop/macos/BetterMail.icns" }
}

Remove-Item -Recurse -Force $publishDirectory -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $releaseDirectory -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $publishDirectory, $releaseDirectory | Out-Null

dotnet publish (Join-Path $repositoryRoot "src/BetterMail.App/BetterMail.App.csproj") `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $publishDirectory `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Runtime" }

if ($Runtime -ne "win-x64") {
    chmod +x (Join-Path $publishDirectory $mainExecutable)
}

dotnet tool run vpk -- pack `
    --packId BetterCorp.BetterMail `
    --packVersion $Version `
    --packDir $publishDirectory `
    --mainExe $mainExecutable `
    --packTitle BetterMail `
    --packAuthors BetterCorp `
    --icon $icon `
    --runtime $Runtime `
    --outputDir $releaseDirectory `
    --delta None

if ($LASTEXITCODE -ne 0) { throw "Velopack packaging failed for $Runtime" }
