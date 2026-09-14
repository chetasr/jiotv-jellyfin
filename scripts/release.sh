#!/bin/bash
#
# Full release flow: build artifacts, publish a GitHub Release with the plugin
# zip, and update the root manifest.json (Jellyfin plugin-catalog format) that
# Jellyfin's "Repository URL" install fetches.
#
# Usage: ./scripts/release.sh vX.Y.Z [owner/repo]
#
set -euo pipefail

VERSION=${1:?usage: ./scripts/release.sh vX.Y.Z [owner/repo]}
REPO=${2:-chetasr/jiotv-jellyfin}

ROOT_DIR=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT_DIR"

echo "Building $VERSION"
./build.sh "${VERSION#v}"

ZIP_FILE=$(ls "$ROOT_DIR"/artifacts/*.zip | head -1)
ZIP_BASE=$(basename "$ZIP_FILE")
CHECKSUM=$(sha256sum "$ZIP_FILE" | awk '{print $1}')
ZIP_URL="https://github.com/${REPO}/releases/download/${VERSION}/${ZIP_BASE}"

echo "Updating manifest.json with $ZIP_URL (sha256 $CHECKSUM)"
python3 scripts/update-manifest.py "$VERSION" "$ZIP_URL" "$CHECKSUM" \
  "https://github.com/${REPO}"

echo "Publishing $ZIP_BASE to GitHub Release ${REPO}@${VERSION}"
notes=$(cat "$ROOT_DIR"/docs/spec.md 2>/dev/null | head -20 || echo "JioTV live tuner plugin.")
gh release create "$VERSION" "$ZIP_FILE" \
  --repo "$REPO" \
  --title "JioTV Plugin $VERSION" \
  --notes "$notes"

echo ""
echo "Manifest now lists $VERSION -> $ZIP_URL"
echo "Repository URL to add in Jellyfin:"
echo "  https://raw.githubusercontent.com/${REPO}/main/manifest.json"
