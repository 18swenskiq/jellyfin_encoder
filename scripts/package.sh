#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
dotnet build Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj -c Release
mkdir -p artifacts/SpaceSaver_0.1.0.0
cp Jellyfin.Plugin.SpaceSaver/bin/Release/net9.0/Jellyfin.Plugin.SpaceSaver.dll meta.json artifacts/SpaceSaver_0.1.0.0/
(cd artifacts/SpaceSaver_0.1.0.0 && zip -j ../SpaceSaver_0.1.0.0.zip Jellyfin.Plugin.SpaceSaver.dll meta.json)
sha256sum artifacts/SpaceSaver_0.1.0.0.zip
