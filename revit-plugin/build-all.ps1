# Build AirenoOS.Revit.Plugin for every supported Revit version.
# Output lands in revit-plugin/build/Revit{2024,2025,2026}/Release/.
#
# Usage:
#   .\build-all.ps1                # builds 2024 + 2025 + 2026 in Release
#   .\build-all.ps1 -Config Debug  # Debug build
#   .\build-all.ps1 -Versions 2026 # only one version

[CmdletBinding()]
param(
    [string]   $Config   = 'Release',
    [string[]] $Versions = @('2024', '2025', '2026')
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'AirenoOS.Revit.Plugin\AirenoOS.Revit.Plugin.csproj'

foreach ($v in $Versions) {
    Write-Host "==> Building Revit $v ($Config)" -ForegroundColor Cyan
    & dotnet build $proj -p:RevitVersion=$v -c $Config -nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Revit $v build failed (exit $LASTEXITCODE)"
    }
}

Write-Host "All builds succeeded." -ForegroundColor Green
