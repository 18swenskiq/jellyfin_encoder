#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
dotnet build Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj -c Release
python3 scripts/release_package.py
cat artifacts/SHA256SUMS
