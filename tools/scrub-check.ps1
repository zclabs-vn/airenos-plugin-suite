<#
.SYNOPSIS
  Pre-release scrub check for AirenoOS AutoCAD + BricsCAD source that ships to Brian.

.DESCRIPTION
  Grep-sweeps the shippable subtrees for anything that must not leak to the customer:
    - Vietnamese diacritics (Unicode range U+00C0..U+1EF9)
    - Infrastructure hints (Cloudflare, Docker, nginx, VPS, tunnel keywords)
    - Personal / company identifiers (ZCLabs, mrbo0911, siawaisun, tqcuong, personal emails)
    - Internal / demo-hub URL fragments (mock-mcp, demo-hub)
    - Private-network IPs

  Exit code:
    0 = clean, safe to build the source zip
    1 = at least one hit; do NOT build zip until fixed

  Run this BEFORE building the customer source zip. If it fails, fix the source
  (not the script) and re-run.

.EXAMPLE
  pwsh ./tools/scrub-check.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

$shipRoots = @(
    Join-Path $repoRoot 'autocad-plugin'
    Join-Path $repoRoot 'bricscad-plugin'
)

$excludeDirs = @('bin', 'obj', 'dist', 'build', 'staging', 'lib', '.vs')

$fileGlobs = @('*.cs', '*.wxs', '*.xml', '*.ps1', '*.md', '*.rtf', '*.scr', '*.sln', '*.csproj', '*.txt', '*.json', '*.config')

$rules = @(
    @{ Name = 'Vietnamese diacritics';    Pattern = '[À-ỹ]'; CaseSensitive = $true }
    @{ Name = 'Infrastructure hints';     Pattern = '\b(cloudflare|nginx|docker|kubernetes|k8s|traefik|tunnel|trycloudflare|ngrok|reverse[- ]proxy)\b'; CaseSensitive = $false }
    @{ Name = 'VPS / hosting keywords';   Pattern = '\b(vps|linode|digitalocean|hetzner|contabo|vultr|ovh)\b'; CaseSensitive = $false }
    @{ Name = 'ZCLabs identifiers';       Pattern = 'zc[- ]?labs|\.zclabs\.'; CaseSensitive = $false }
    @{ Name = 'Personal handles';         Pattern = '\b(mrbo0911|siawaisun|tqcuong|cuong.?ta|ta.?quang)\b'; CaseSensitive = $false }
    @{ Name = 'Demo / mock endpoints';    Pattern = '\b(demo-hub|mock-mcp|localhost|127\.0\.0\.1)\b'; CaseSensitive = $false }
    @{ Name = 'Private-network IPs';      Pattern = '\b(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3}|103\.253\.145\.163)\b'; CaseSensitive = $false }
    @{ Name = 'Personal emails';          Pattern = '@(gmail|hotmail|yahoo|outlook|proton|icloud)\.com\b'; CaseSensitive = $false }
    @{ Name = 'Internal .dev URLs';       Pattern = 'https?://[a-z0-9.-]+\.dev\b'; CaseSensitive = $false }
)

# Collect files, excluding build outputs and vendored libs.
$allFiles = foreach ($root in $shipRoots) {
    if (-not (Test-Path $root)) {
        Write-Warning "Ship root missing: $root"
        continue
    }
    Get-ChildItem -Path $root -Recurse -File -Include $fileGlobs |
        Where-Object {
            $relPath = $_.FullName.Substring($root.Length + 1)
            $parts = $relPath -split '[\\/]'
            -not ($parts | Where-Object { $excludeDirs -contains $_ })
        }
}

$fileCount = @($allFiles).Count
Write-Host "[scrub-check] Sweeping $fileCount files under:" -ForegroundColor Cyan
foreach ($r in $shipRoots) { Write-Host "  - $r" -ForegroundColor DarkGray }
Write-Host ""

# Apply each rule.
$findings = New-Object System.Collections.ArrayList

foreach ($rule in $rules) {
    if ($rule.CaseSensitive) {
        $hits = Select-String -Path $allFiles.FullName -Pattern $rule.Pattern -AllMatches -CaseSensitive -ErrorAction SilentlyContinue
    } else {
        $hits = Select-String -Path $allFiles.FullName -Pattern $rule.Pattern -AllMatches -ErrorAction SilentlyContinue
    }
    foreach ($hit in $hits) {
        [void]$findings.Add([pscustomobject]@{
            Rule    = $rule.Name
            File    = $hit.Path.Substring($repoRoot.Path.Length + 1)
            Line    = $hit.LineNumber
            Snippet = $hit.Line.Trim()
        })
    }
}

# Report.
if ($findings.Count -eq 0) {
    Write-Host "[scrub-check] CLEAN. 0 findings across $($rules.Count) rules." -ForegroundColor Green
    exit 0
}

Write-Host "[scrub-check] FOUND $($findings.Count) leak(s):" -ForegroundColor Red
Write-Host ""
$findings | Group-Object Rule | ForEach-Object {
    Write-Host "-- $($_.Name) ($($_.Count)) --" -ForegroundColor Yellow
    foreach ($f in $_.Group) {
        Write-Host "  $($f.File):$($f.Line)" -ForegroundColor White
        Write-Host "    $($f.Snippet)" -ForegroundColor DarkGray
    }
    Write-Host ""
}

Write-Host "Fix the SOURCE, not the script. Re-run scrub-check after each fix." -ForegroundColor Red
exit 1
