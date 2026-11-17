$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:NUGET_PACKAGES = Join-Path $root '.nuget'
    dotnet build 'Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $packageDirectory = Join-Path $root 'artifacts/SpaceSaver_0.1.0.0'
    New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
    Copy-Item -LiteralPath 'Jellyfin.Plugin.SpaceSaver/bin/Release/net9.0/Jellyfin.Plugin.SpaceSaver.dll' -Destination $packageDirectory
    Copy-Item -LiteralPath 'meta.json' -Destination $packageDirectory
    Compress-Archive -LiteralPath (Join-Path $packageDirectory 'Jellyfin.Plugin.SpaceSaver.dll'), (Join-Path $packageDirectory 'meta.json') -DestinationPath 'artifacts/SpaceSaver_0.1.0.0.zip' -Force
    Get-FileHash -Algorithm SHA256 -LiteralPath 'artifacts/SpaceSaver_0.1.0.0.zip'
} finally { Pop-Location }
