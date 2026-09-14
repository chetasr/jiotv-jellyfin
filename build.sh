#!/bin/bash
#
# Builds the JioTV plugin and produces an installable zip + Jellyfin catalog meta.json.
#
# Usage: ./build.sh [VERSION]
# Output: artifacts/JioTv.Plugin.{ver}.zip and artifacts/meta.json
#
set -euo pipefail

DOTNET=${DOTNET:-dotnet}
ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
VERSION=${1:-$(date +%Y.%m.%d.1)}

(
  cd "$ROOT_DIR"
  dotnet build JioTv.Plugin/JioTv.Plugin.csproj -c Release
)

echo "Artifacts built at JioTv.Plugin/bin/Release/net9.0/"
mkdir -p "$ROOT_DIR/artifacts"
cp -r "$ROOT_DIR/JioTv.Plugin/bin/Release/net9.0/JioTv.Plugin.dll" "$ROOT_DIR/artifacts/"
cp "$ROOT_DIR/JioTv.Plugin/bin/Release/net9.0/JioTv.Plugin.deps.json" "$ROOT_DIR/artifacts/"
cp "$ROOT_DIR/JioTv.Plugin/bin/Release/net9.0/JioTv.Plugin.xml" "$ROOT_DIR/artifacts/"

echo "Making meta.json for Jellyfin's plugin catalog"
cat > "$ROOT_DIR/artifacts/meta.json" <<EOF
{
  "guid": "7d4abbd2-2e55-4a8c-9e6a-2afc4fc3a3e9",
  "name": "JioTV",
  "version": "$VERSION",
  "targetAbi": "10.9.1.0",
  "timestamp": "$(date -u +%FT%TZ)",
  "description": "JioTV live tuner for Jellyfin: log in with your Jio account, browse 1100+ live channels in Live TV with EPG driven by Jio's own data.",
  "category": "Live TV",
  "owner": "JioTV Jellyfin",
  "changelog": "Auto-refresh tokens, self-healing proxy, AES-secured auth URLs, EPG via Jio.",
  "imageUrl": "https://raw.githubusercontent.com/mitthu786/TS-JioTV/master/app/assets/css/img/logo.png"
}
EOF

echo "Making installable zip"
(
  cd "$ROOT_DIR/artifacts"
  zip -r "JioTv.Plugin.$VERSION.zip" JioTv.Plugin.dll JioTv.Plugin.deps.json meta.json
)

echo ""
echo "✅ Complete: artifacts/JioTv.Plugin.$VERSION.zip"
echo "   Upload manually via Dashboard → Plugins → Repositories → Install (Manual)"
echo "   Or host meta.json + the zip on GitHub Pages for a catalog link."
