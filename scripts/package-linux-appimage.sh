#!/usr/bin/env bash
set -euo pipefail

command -v linuxdeploy >/dev/null || { echo "linuxdeploy is required" >&2; exit 1; }
command -v appimagetool >/dev/null || { echo "appimagetool is required" >&2; exit 1; }

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
appdir="$root/artifacts/BetterMail.AppDir"
rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/scalable/apps"

dotnet publish "$root/src/BetterMail.App/BetterMail.App.csproj" \
  --configuration Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false --output "$appdir/usr/bin"

install -m 755 "$root/scripts/AppRun" "$appdir/AppRun"
install -m 644 "$root/packaging/BetterMail.desktop" "$appdir/BetterMail.desktop"
install -m 644 "$root/packaging/BetterMail.svg" "$appdir/BetterMail.svg"
install -m 644 "$root/packaging/BetterMail.svg" "$appdir/usr/share/icons/hicolor/scalable/apps/BetterMail.svg"

linuxdeploy --appdir "$appdir" --desktop-file "$appdir/BetterMail.desktop" --icon-file "$appdir/BetterMail.svg"
ARCH=x86_64 appimagetool "$appdir" "$root/artifacts/BetterMail-x86_64.AppImage"
