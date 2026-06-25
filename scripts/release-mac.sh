#!/usr/bin/env bash
# release-mac.sh — local Mac dev release: clean, publish, install, launch.
#
# Mirrors the spirit of release-win.sh but runs everything locally since
# this is the Mac dev box. Uses uninstall-mac.sh for the scrub, dotnet
# publish for the build, then copies the produced BoltMate.app bundle to
# /Applications and launches it.
#
# Usage:
#   ./scripts/release-mac.sh                # arm64, Release, install + launch
#   CONFIG=Debug ./scripts/release-mac.sh   # debug build
#   ./scripts/release-mac.sh --no-clean     # skip uninstall-mac.sh
#   ./scripts/release-mac.sh --no-install   # build only, no copy to /Applications
#   ./scripts/release-mac.sh --no-launch    # install but don't open BoltMate.app
#
# Architecture is auto-detected from `uname -m` so the same script works
# on Apple Silicon and Intel boxes.

set -euo pipefail

CONFIG=${CONFIG:-Release}
SCRIPT_DIR=$(cd "$(dirname "$0")" && pwd)
REPO_ROOT=$(cd "$SCRIPT_DIR/.." && pwd)
SRC_DIR=$REPO_ROOT/src
APP_PROJ=$SRC_DIR/BoltMate.App/BoltMate.App.csproj
TFM=net10.0-macos

case "$(uname -m)" in
    arm64)  RID=osx-arm64 ;;
    x86_64) RID=osx-x64   ;;
    *)      echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

NO_CLEAN=0
NO_INSTALL=0
NO_LAUNCH=0
for arg in "$@"; do
    case "$arg" in
        --no-clean)   NO_CLEAN=1 ;;
        --no-install) NO_INSTALL=1 ;;
        --no-launch)  NO_LAUNCH=1 ;;
    esac
done

if [[ "$NO_CLEAN" = 0 ]]; then
    echo "==> Running uninstall-mac.sh"
    bash "$SCRIPT_DIR/uninstall-mac.sh" || true
fi

echo "==> Wiping bin/obj"
# Stale build outputs are responsible for the recurring "Permission
# denied" copy of libhidapi.dylib during the Microsoft.macOS SDK's
# install-name-tool step. Clean wipe sidesteps it.
find "$SRC_DIR" -type d \( -name bin -o -name obj \) -exec rm -rf {} + 2>/dev/null || true

echo "==> dotnet publish ($CONFIG, $RID, $TFM)"
dotnet publish "$APP_PROJ" \
    -c "$CONFIG" \
    -r "$RID" \
    -f "$TFM" \
    --self-contained \
    /p:UseAppHost=true

APP_BUNDLE=$SRC_DIR/BoltMate.App/bin/$CONFIG/$TFM/$RID/BoltMate.app
if [[ ! -d "$APP_BUNDLE/Contents/MacOS" ]]; then
    echo "ERR: expected $APP_BUNDLE not produced (Contents/MacOS missing)" >&2
    exit 1
fi
echo "    built: $APP_BUNDLE"

echo "==> Patching Info.plist (LSUIElement, icon, copyright, display name)"
# The Microsoft.macOS SDK generates a minimal Info.plist — we patch in
# the bits BoltMate needs:
#   • LSUIElement=true (menubar-only, no Dock icon)
#   • CFBundleIconFile=app-icon (Finder + Dock icon)
#   • CFBundleDisplayName=BoltMate (system-facing label)
#   • NSHighResolutionCapable=true (Retina rendering)
PLIST=$APP_BUNDLE/Contents/Info.plist
plutil -replace LSUIElement -bool true "$PLIST"
plutil -replace NSHighResolutionCapable -bool true "$PLIST" 2>/dev/null || true
plutil -replace CFBundleIconFile -string app-icon.icns "$PLIST"
plutil -replace CFBundleDisplayName -string BoltMate "$PLIST"
plutil -replace NSHumanReadableCopyright -string "Copyright (c) Jared Ballen" "$PLIST" 2>/dev/null || true

# Normalize Info.plist to XML so byte-level content is deterministic across
# rebuilds. The Microsoft.macOS SDK generates a BINARY plist whose key-order
# is hash-randomized per run — same logical content, different bytes — which
# bumps the bundle's CodeResources seal + outer cdhash on every build. That
# in turn forces TCC (especially Input Monitoring, which keys on cdhash) to
# re-prompt every install. XML serialization sorts dicts predictably so
# identical content → identical bytes.
plutil -convert xml1 "$PLIST"

echo "==> Moving icon to Contents/Resources/app-icon.icns"
# The SDK stages content under Resources/Assets/ via Content items in the
# csproj, but CFBundleIconFile=app-icon resolves against Resources/
# directly. Copy the .icns up one level so Finder finds it.
ICNS_SRC=$APP_BUNDLE/Contents/Resources/Assets/app-icon.icns
ICNS_DEST=$APP_BUNDLE/Contents/Resources/app-icon.icns
if [[ -f "$ICNS_SRC" && ! -f "$ICNS_DEST" ]]; then
    cp "$ICNS_SRC" "$ICNS_DEST"
fi

# Dev-cert signing for local installs. Override via SIGN_IDENTITY env
# var when shipping (Developer ID Application: ...). Empty string falls
# back to ad-hoc (-) which preserves the legacy path for CI.
SIGN_IDENTITY=${SIGN_IDENTITY:-"Mac Developer: Jared Allen (44A626R4VU)"}
ENTITLEMENTS=$REPO_ROOT/src/BoltMate.App/BoltMate.App.entitlements

if [[ "$SIGN_IDENTITY" = "-" ]]; then
    echo "==> Re-signing bundle (ad-hoc) — Info.plist edit invalidates SDK signature"
    rm -rf "$APP_BUNDLE/Contents/_CodeSignature"
    codesign --force --deep --sign - --identifier com.jaredballen.BoltMate "$APP_BUNDLE"
else
    echo "==> Re-signing bundle with hardened runtime + entitlements"
    echo "    Identity: $SIGN_IDENTITY"
    # Strip the SDK's _CodeSignature so codesign re-signs cleanly.
    rm -rf "$APP_BUNDLE/Contents/_CodeSignature"
    # Sign nested dylibs first (no entitlements on them), then the bundle
    # WITH entitlements. --deep on the outer call would re-sign the
    # dylibs and strip the entitlements, so we do it in two passes.
    find "$APP_BUNDLE/Contents" -type f \( -name "*.dylib" -o -name "*.so" \) -print0 |
        xargs -0 -I{} codesign --force --options runtime --sign "$SIGN_IDENTITY" "{}"
    codesign --force --options runtime \
        --entitlements "$ENTITLEMENTS" \
        --sign "$SIGN_IDENTITY" \
        --identifier com.jaredballen.BoltMate \
        "$APP_BUNDLE"
fi

if [[ "$NO_INSTALL" = 0 ]]; then
    echo "==> Installing to /Applications/BoltMate.app"
    rm -rf /Applications/BoltMate.app
    cp -a "$APP_BUNDLE" /Applications/
fi

if [[ "$NO_INSTALL" = 0 ]]; then
    echo "==> Re-registering bundle with Launch Services (flushes cached app metadata)"
    # If a prior `dotnet run` launched the raw binary, Launch Services
    # cached its NSApp identity — Avalonia.Native sets a default
    # "Avalonia Application" name when no bundle Info.plist is in
    # scope. macOS firewall + other UI surfaces read from that cache
    # in preference to our new bundle's Info.plist. -f forces a
    # re-read of the freshly-installed plist.
    LSREG=/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister
    "$LSREG" -f /Applications/BoltMate.app 2>/dev/null || true

    echo "==> Dropping any stale firewall entry"
    # Previous builds may have added a firewall rule under the old
    # name; dropping forces a fresh prompt on next bind so the new
    # bundle's DisplayName surfaces in the dialog.
    /usr/libexec/ApplicationFirewall/socketfilterfw \
        --remove /Applications/BoltMate.app/Contents/MacOS/BoltMate 2>/dev/null || true

    echo "==> Nudging icon services + Dock/Finder so the new bundle's icon repaints"
    # macOS caches Finder/Dock icons aggressively by bundle path inode +
    # mod time. A fresh `cp -a` already updates mod time, but the cached
    # "no icon" / stale icon for this bundle path tends to stick until
    # we kick Dock + Finder. No sudo needed — these run per-user.
    touch /Applications/BoltMate.app
    killall Dock 2>/dev/null || true
    killall Finder 2>/dev/null || true
fi

if [[ "$NO_LAUNCH" = 0 && "$NO_INSTALL" = 0 ]]; then
    echo "==> Launching BoltMate"
    open /Applications/BoltMate.app
fi

echo ""
echo "==> Done."
echo "    Bundle:    $APP_BUNDLE"
if [[ "$NO_INSTALL" = 0 ]]; then
    echo "    Installed: /Applications/BoltMate.app"
fi
