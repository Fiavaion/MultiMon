#!/usr/bin/env bash
#
# publish-release-mac.sh — macOS counterpart of scripts/publish-release.ps1.
#
# Builds a self-contained osx-arm64 Release of MultiMon.Control.Mac and MultiMon.Stress.Mac,
# stages them with the root docs, and produces dist/MultiMon-<version>-macos-arm64.dmg.
# Version is read from Directory.Build.props (single source of truth, same as the Windows script).
#
# Usage:   bash scripts/publish-release-mac.sh          # unsigned DMG
#          MAC_SIGN_IDENTITY="Developer ID Application: Fiavaion (TEAMID)" \
#            bash scripts/publish-release-mac.sh        # signed DMG
#
# Environment (all optional; each stage is skipped when its variables are absent):
#   MAC_SIGN_IDENTITY  codesign identity. Unset -> unsigned bundles and DMG.
#   APPLE_ID           Apple ID for notarytool.        ) all three required
#   APPLE_TEAM_ID      Developer Team ID.              ) to notarize
#   APPLE_APP_PASSWORD app-specific password.          ) and staple
#
# License: EUPL-1.2
# NOTE: FFmpeg is NOT bundled. It is an external prerequisite required only for the
#       "Convert to HAP" feature. End users must install it separately (brew install ffmpeg).
#
# How the .app is produced — MultiMon.Control.Mac targets net9.0-macos (the Microsoft.macOS
# workload), so the SDK itself emits a finished application bundle: `dotnet publish -c Release
# -r osx-arm64` writes MultiMon.Control.Mac.app with Info.plist and the MultiMon.icns
# BundleResource (CFBundleIconFile) already inside. This script therefore does NOT hand-assemble
# Contents/MacOS + Contents/Resources; it only renames the bundle to MultiMon.app for the DMG.
# No -p:PublishSingleFile: the macOS app-bundle layout is what ships, not a single file.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

# --- Version from Directory.Build.props (no xmllint dependency) ---
BUILD_PROPS="$REPO_ROOT/Directory.Build.props"
VERSION="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$BUILD_PROPS" | head -n 1)"
if [ -z "$VERSION" ]; then
    echo "ERROR: could not parse <Version> from $BUILD_PROPS" >&2
    exit 1
fi
echo "Version: $VERSION"

# --- Paths ---
DIST_ROOT="$REPO_ROOT/dist"
KIT_NAME="MultiMon-$VERSION-macos-arm64"
STAGE_DIR="$DIST_ROOT/$KIT_NAME"          # DMG staging root
DMG_PATH="$DIST_ROOT/$KIT_NAME.dmg"
ENTITLEMENTS="$REPO_ROOT/MultiMon.Control.Mac/Entitlements.plist"

CONTROL_PROJ="$REPO_ROOT/MultiMon.Control.Mac/MultiMon.Control.Mac.csproj"
STRESS_PROJ="$REPO_ROOT/MultiMon.Stress.Mac/MultiMon.Stress.Mac.csproj"
CONTROL_PUBLISH_DIR="$REPO_ROOT/MultiMon.Control.Mac/bin/Release/net9.0-macos/osx-arm64/publish"
STRESS_PUBLISH_DIR="$REPO_ROOT/MultiMon.Stress.Mac/bin/Release/net9.0-macos/osx-arm64/publish"

# Root docs to copy into the kit (same set as the Windows script)
DOC_FILES=(
    "README.md"
    "LICENSE"
    "CHANGELOG.md"
    "REQUIREMENTS.md"
    "THIRD-PARTY-NOTICES.md"
)

# --- Idempotent: clean the target staging dir and DMG ---
echo
echo "Cleaning previous dist target..."
rm -rf "$STAGE_DIR" "$DMG_PATH"
mkdir -p "$STAGE_DIR/Stress"

# --- Publish MultiMon.Control.Mac ---
# No -o: the macOS SDK writes the .app into the project's own publish directory; redirecting the
# output directory rearranges the bundle layout.
echo
echo "Publishing MultiMon.Control.Mac (osx-arm64, self-contained)..."
rm -rf "$CONTROL_PUBLISH_DIR"
dotnet publish "$CONTROL_PROJ" -c Release -r osx-arm64 --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false

CONTROL_APP="$(find "$CONTROL_PUBLISH_DIR" -maxdepth 1 -type d -name '*.app' | head -n 1)"
if [ -z "$CONTROL_APP" ]; then
    echo "ERROR: no .app bundle found under $CONTROL_PUBLISH_DIR" >&2
    exit 1
fi
cp -R "$CONTROL_APP" "$STAGE_DIR/MultiMon.app"
echo "  Staged $(basename "$CONTROL_APP") as MultiMon.app"

# --- Publish MultiMon.Stress.Mac (headless harness, shipped alongside) ---
echo
echo "Publishing MultiMon.Stress.Mac (osx-arm64, self-contained)..."
rm -rf "$STRESS_PUBLISH_DIR"
dotnet publish "$STRESS_PROJ" -c Release -r osx-arm64 --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false

STRESS_APP="$(find "$STRESS_PUBLISH_DIR" -maxdepth 1 -type d -name '*.app' | head -n 1)"
if [ -z "$STRESS_APP" ]; then
    echo "ERROR: no .app bundle found under $STRESS_PUBLISH_DIR" >&2
    exit 1
fi
cp -R "$STRESS_APP" "$STAGE_DIR/Stress/"
echo "  Staged $(basename "$STRESS_APP")"

# --- Copy root docs ---
echo
echo "Copying root docs..."
for DOC in "${DOC_FILES[@]}"; do
    if [ -f "$REPO_ROOT/$DOC" ]; then
        cp "$REPO_ROOT/$DOC" "$STAGE_DIR/"
        echo "  Copied $DOC"
    else
        echo "  WARNING: not found (skipped): $DOC" >&2
    fi
done

# --- Drag-to-install target ---
ln -s /Applications "$STAGE_DIR/Applications"

# --- Optional code signing ---
if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
    echo
    echo "Signing bundles with: $MAC_SIGN_IDENTITY"
    # --timestamp is required for the signature to be notarizable.
    for BUNDLE in "$STAGE_DIR/MultiMon.app" "$STAGE_DIR/Stress/"*.app; do
        codesign --force --deep --options runtime --timestamp \
            --entitlements "$ENTITLEMENTS" \
            --sign "$MAC_SIGN_IDENTITY" "$BUNDLE"
        codesign --verify --strict --verbose=2 "$BUNDLE"
        echo "  Signed $(basename "$BUNDLE")"
    done
else
    echo
    echo "MAC_SIGN_IDENTITY not set — producing UNSIGNED bundles (Gatekeeper will warn on first launch)."
fi

# --- Build the DMG ---
echo
echo "Building $DMG_PATH ..."
hdiutil create -volname "MultiMon $VERSION" -srcfolder "$STAGE_DIR" \
    -fs HFS+ -format UDZO -ov "$DMG_PATH"

if [ -n "${MAC_SIGN_IDENTITY:-}" ]; then
    codesign --force --timestamp --sign "$MAC_SIGN_IDENTITY" "$DMG_PATH"
    echo "  Signed the DMG"
fi

# --- Optional notarization + staple ---
if [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
    echo
    echo "Submitting to Apple notary service (this waits for the verdict)..."
    xcrun notarytool submit "$DMG_PATH" \
        --apple-id "$APPLE_ID" \
        --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_PASSWORD" \
        --wait
    xcrun stapler staple "$DMG_PATH"
    xcrun stapler validate "$DMG_PATH"
    echo "  Notarized and stapled"
else
    echo
    echo "APPLE_ID / APPLE_TEAM_ID / APPLE_APP_PASSWORD not all set — skipping notarization."
fi

# --- Report ---
DMG_SIZE_MB="$(du -m "$DMG_PATH" | cut -f1)"
echo
echo "====================================================="
echo "  Staging folder : $STAGE_DIR"
echo "  DMG            : $DMG_PATH"
echo "  DMG size       : ${DMG_SIZE_MB} MB"
echo "====================================================="
