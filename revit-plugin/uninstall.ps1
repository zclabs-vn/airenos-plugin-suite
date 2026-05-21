# Dev uninstall of AirenoOS.Revit.Plugin.
# Removes the .addin manifest and AirenoOS\ subfolder from each
#   %AppData%\Autodesk\Revit\Addins\<version>\
#
# Usage:
#   .\uninstall.ps1                # removes every installed version
#   .\uninstall.ps1 -Versions 2026 # only one version

[CmdletBinding()]
param(
    [string[]] $Versions
)

$ErrorActionPreference = 'Stop'

# Discover installed versions if not specified.
if (-not $Versions) {
    $addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
    if (-not (Test-Path $addinsRoot)) {
        Write-Host "No Revit addins folder. Nothing to do."
        return
    }
    $Versions = Get-ChildItem $addinsRoot -Directory `
        | Where-Object { $_.Name -match '^\d{4}$' } `
        | ForEach-Object { $_.Name }
}

$removed = @()
foreach ($v in $Versions) {
    $userAddins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v"
    if (-not (Test-Path $userAddins)) { continue }

    # Flat layout: addin + plugin DLL + bundled deps. The old (subfolder)
    # layout left an AirenoOS\ directory — clean that too for backward compat.
    $patterns = @(
        'AirenoOS.Revit.Plugin.addin',
        'AirenoOS.Revit.Plugin.dll',
        'AirenoOS.Revit.Plugin.pdb',
        'AirenoOS.Revit.Plugin.deps.json',
        'System.Text.Json.dll',
        'System.Text.Encodings.Web.dll',
        'System.Buffers.dll',
        'System.Memory.dll',
        'System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll',
        'System.Threading.Tasks.Extensions.dll',
        'System.ValueTuple.dll',
        'Microsoft.Bcl.AsyncInterfaces.dll'
    )

    $touched = $false
    foreach ($p in $patterns) {
        $f = Join-Path $userAddins $p
        if (Test-Path $f) {
            Remove-Item $f -Force
            $touched = $true
        }
    }
    $legacyDir = Join-Path $userAddins 'AirenoOS'
    if (Test-Path $legacyDir) {
        Remove-Item $legacyDir -Recurse -Force
        $touched = $true
    }
    if ($touched) {
        $removed += "Revit $v"
    }
}

if ($removed.Count -eq 0) {
    Write-Host "Nothing was installed."
} else {
    Write-Host "Removed:" -ForegroundColor Green
    $removed | ForEach-Object { Write-Host "  $_" }
}
