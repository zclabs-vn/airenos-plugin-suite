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

Write-Host "[1/3] Building plugin (dual-target net48 + net8.0-windows / $Configuration / x64)..." -ForegroundColor Cyan
if (Test-Path $StagingDir) { Remove-Item -Recurse -Force $StagingDir }
New-Item -ItemType Directory -Force -Path "$StagingDir\V25" | Out-Null
New-Item -ItemType Directory -Force -Path "$StagingDir\V26" | Out-Null

# Build both targets in one pass — produces bin\Release\net48\ + bin\Release\net8.0-windows\.
dotnet build $PluginCsproj `
    -c $Configuration `
    -p:Platform=x64 `
    --nologo `
    -v minimal | Out-Host

$PluginBinDir = Join-Path (Split-Path $PluginCsproj) "bin\x64\Release"
$PluginDirV25 = Join-Path $PluginBinDir "net48"
$PluginDirV26 = Join-Path $PluginBinDir "net8.0-windows"
foreach ($d in @($PluginDirV25, $PluginDirV26)) {
    $dll = Join-Path $d "AirenoOS.BricsCAD.Plugin.dll"
    if (-not (Test-Path $dll)) { throw "Plugin DLL not produced at $dll" }
}

# Ship the ENTIRE net48 output (plugin DLL + every System.* dependency) so V25's
# .NET Framework 4.x CLR can resolve System.Text.Json and friends. Without this,
# HttpSender's static ctor (which constructs JsonSerializerOptions) throws
# TypeInitializationException at first POST attempt and the payload never leaves
# BricsCAD. V26's net8 build is self-contained against the .NET 8 BCL — only the
# plugin DLL needs to ship.
Copy-Item -Path "$PluginDirV25\*.dll" -Destination "$StagingDir\V25\" -Force
Copy-Item -Path "$PluginDirV26\AirenoOS.BricsCAD.Plugin.dll" -Destination "$StagingDir\V26\" -Force
Write-Host "V25 bundle ships: $((Get-ChildItem "$StagingDir\V25\*.dll").Name -join ', ')" -ForegroundColor DarkGray

Write-Host "[2/3] Building MSI..." -ForegroundColor Cyan
Push-Location $InstallerDir
try {
    wix build AirenoOS.BricsCAD.Installer.wxs `
        -arch x64 `
        -ext WixToolset.UI.wixext `
        -d "PluginDirV25=$StagingDir\V25" `
        -d "PluginDllV26=$StagingDir\V26\AirenoOS.BricsCAD.Plugin.dll" `
        -o $MsiOut
}
finally {
    Pop-Location
}

if (-not (Test-Path $MsiOut)) {
    throw "MSI was not produced at $MsiOut"
}

Write-Host "[3/4] Patching ControlCondition rows for VersionSelectDlg..." -ForegroundColor Cyan
& "$InstallerDir\post-build.ps1" -MsiPath $MsiOut

Write-Host "[4/4] Cleaning staging..." -ForegroundColor Cyan
Remove-Item -Recurse -Force $StagingDir -ErrorAction SilentlyContinue

$size = [math]::Round((Get-Item $MsiOut).Length / 1KB, 1)
Write-Host ""
Write-Host "MSI ready: $MsiOut ($size KB)" -ForegroundColor Green
Write-Host ""
Write-Host "Install on a BricsCAD machine:" -ForegroundColor Gray
Write-Host "  msiexec /i `"$MsiOut`"" -ForegroundColor Gray
Write-Host "Or double-click the .msi (UAC will prompt for admin)." -ForegroundColor Gray
