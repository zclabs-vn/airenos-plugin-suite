<#
.SYNOPSIS
  Build a release MSI of the AirenoOS BricsCAD plugin.

.DESCRIPTION
  Steps:
    1. dotnet publish the plugin in Release/x64 to a clean staging folder
    2. wix build the installer, embedding the published DLL + PackageContents.xml
    3. Output the MSI to ../dist/

  Requirements:
    - .NET SDK 8.x
    - WiX 5 (`dotnet tool install --global wix`)
    - BricsCAD V26 DLLs reachable at the path encoded in the plugin csproj
      (only needed to satisfy the build references — the MSI never ships them)

.EXAMPLE
  pwsh ./build-installer.ps1
  pwsh ./build-installer.ps1 -Version 1.0.1
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$InstallerDir = $PSScriptRoot
$RepoRoot     = Resolve-Path (Join-Path $InstallerDir "..\..")
$PluginCsproj = Join-Path $RepoRoot "bricscad-plugin\AirenoOS.BricsCAD.Plugin\AirenoOS.BricsCAD.Plugin.csproj"
$DistDir      = Join-Path $RepoRoot "bricscad-plugin\dist"
$StagingDir   = Join-Path $RepoRoot "bricscad-plugin\dist\stage"
$MsiOut       = Join-Path $DistDir "AirenoOS.BricsCAD.Plugin-$Version.msi"

# ── Sanity ────────────────────────────────────────────────────────────────────
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "WiX CLI not found. Install with: dotnet tool install --global wix"
}
if (-not (Test-Path $PluginCsproj)) {
    throw "Plugin csproj not found at $PluginCsproj"
}

Write-Host "[1/3] Publishing plugin ($Configuration / x64)..." -ForegroundColor Cyan
if (Test-Path $StagingDir) { Remove-Item -Recurse -Force $StagingDir }
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

dotnet publish $PluginCsproj `
    -c $Configuration `
    -p:Platform=x64 `
    -o $StagingDir `
    --nologo `
    -v minimal | Out-Host

$PublishedDll = Join-Path $StagingDir "AirenoOS.BricsCAD.Plugin.dll"
if (-not (Test-Path $PublishedDll)) {
    throw "Plugin DLL not produced at $PublishedDll"
}

Write-Host "[2/3] Building MSI..." -ForegroundColor Cyan
Push-Location $InstallerDir
try {
    wix build AirenoOS.BricsCAD.Installer.wxs `
        -arch x64 `
        -d "PluginDll=$PublishedDll" `
        -o $MsiOut
}
finally {
    Pop-Location
}

if (-not (Test-Path $MsiOut)) {
    throw "MSI was not produced at $MsiOut"
}

Write-Host "[3/3] Cleaning staging..." -ForegroundColor Cyan
Remove-Item -Recurse -Force $StagingDir -ErrorAction SilentlyContinue

$size = [math]::Round((Get-Item $MsiOut).Length / 1KB, 1)
Write-Host ""
Write-Host "MSI ready: $MsiOut ($size KB)" -ForegroundColor Green
Write-Host ""
Write-Host "Install on a BricsCAD machine:" -ForegroundColor Gray
Write-Host "  msiexec /i `"$MsiOut`"" -ForegroundColor Gray
Write-Host "Or double-click the .msi (UAC will prompt for admin)." -ForegroundColor Gray
