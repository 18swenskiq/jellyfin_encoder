$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
    $env:NUGET_PACKAGES = Join-Path $root '.nuget'
    dotnet build 'Jellyfin.Plugin.SpaceSaver/Jellyfin.Plugin.SpaceSaver.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    python scripts/release_package.py
    if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    Get-Content -LiteralPath 'artifacts/SHA256SUMS'
} finally { Pop-Location }
