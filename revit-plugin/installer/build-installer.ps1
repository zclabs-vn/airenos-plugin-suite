# Build the WiX installer, building the plugin for all 3 Revit versions first.
#
# Usage:
#   .\build-installer.ps1
#   .\build-installer.ps1 -Config Debug
#
# Output:
#   bin\Release\AirenoOS.Revit.Installer.msi

[CmdletBinding()]
param(
    [string] $Config = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host "==> Building plugin (all 3 Revit versions)" -ForegroundColor Cyan
& (Join-Path $root 'build-all.ps1') -Config $Config

Write-Host "==> Building installer" -ForegroundColor Cyan
& dotnet build (Join-Path $PSScriptRoot 'AirenoOS.Revit.Installer.wixproj') -c $Config -nologo
if ($LASTEXITCODE -ne 0) {
    throw "WiX build failed (exit $LASTEXITCODE)"
}

$msi = Join-Path $PSScriptRoot "bin\$Config\AirenoOS.Revit.Installer.msi"
if (Test-Path $msi) {
    $size = [Math]::Round((Get-Item $msi).Length / 1KB, 1)
    Write-Host "MSI built: $msi ($size KB)" -ForegroundColor Green
}
