# Dev install of AirenoOS.Revit.Plugin.
#
# Copies the plugin DLL + .addin manifest + any deps from
#   revit-plugin\build\Revit<version>\Release\
# into
#   %AppData%\Autodesk\Revit\Addins\<version>\AirenoOS\
#
# Per-user install (no admin needed). The WiX installer targets %ProgramData%
# for all-users; this script is the developer's fast iteration loop.
#
# Usage:
#   .\install.ps1                            # installs all versions present in build/
#   .\install.ps1 -Versions 2026             # only one version
#   .\install.ps1 -Build                     # rebuild first
#
# Idempotent — running twice replaces files in place.

[CmdletBinding()]
param(
    [string[]] $Versions,
    [switch]   $Build,
    [string]   $Config = 'Release'
)

$ErrorActionPreference = 'Stop'

if ($Build) {
    $buildArgs = @{ Config = $Config }
    if ($Versions) { $buildArgs.Versions = $Versions }
    & (Join-Path $PSScriptRoot 'build-all.ps1') @buildArgs
}

$buildRoot = Join-Path $PSScriptRoot 'build'

# Discover which versions have a built output if user didn't specify.
if (-not $Versions) {
    if (-not (Test-Path $buildRoot)) {
        throw "No build output found. Run with -Build or call build-all.ps1 first."
    }
    $found = @()
    foreach ($d in Get-ChildItem $buildRoot -Directory) {
        if ($d.Name -match '^Revit(\d{4})$') {
            $found += $matches[1]
        }
    }
    $Versions = $found | Sort-Object
    if (-not $Versions) {
        throw "No Revit<version> folders under $buildRoot."
    }
}

$installed = @()
foreach ($v in $Versions) {
    $src = Join-Path $buildRoot "Revit$v\$Config"
    if (-not (Test-Path $src)) {
        Write-Warning "Skipping Revit $v - no build at $src"
        continue
    }

    # Flat layout - matches the WiX installer. The .addin's relative
    # <Assembly>AirenoOS.Revit.Plugin.dll</Assembly> resolves alongside.
    $userAddins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    if (-not (Test-Path $userAddins)) {
        New-Item -ItemType Directory -Path $userAddins -Force | Out-Null
    }

    foreach ($f in Get-ChildItem $src -File) {
        Copy-Item $f.FullName -Destination $userAddins -Force
    }

    $installed += "Revit $v -> $userAddins"
}

if ($installed.Count -eq 0) {
    Write-Warning "Nothing installed."
} else {
    Write-Host "Installed:" -ForegroundColor Green
    $installed | ForEach-Object { Write-Host "  $_" }
    Write-Host "Restart Revit. Look for the AirenoOS ribbon tab." -ForegroundColor Yellow
}
