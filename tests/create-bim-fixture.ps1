<#
  Generate tests/bim_test.dwg — a minimal BIM-classified drawing for exercising
  the BimSupport path in CoreExtractor (BIM Room/Space scan, stable_bim_guid
  identity, BIMPropertySet metadata format, IfcClass classification).

  Drives BricsCAD V25 BIM via COM Automation. Requires:
    - BricsCAD V25 with BIM module licensed (RUNASLEVEL = 3 or 5)
    - BricsCAD V25 already running (we attach to a live instance to avoid
      a fresh licence handshake every run)

  Builds via direct COM ModelSpace.AddBox calls (deterministic positioning)
  followed by SendCommand for the BIM-specific classify/space operations.

  Usage:
    PS> .\tests\create-bim-fixture.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Item $PSScriptRoot).Parent.FullName }
$testsDir = Join-Path $root 'tests'
$fixture  = Join-Path $testsDir 'bim_test.dwg'
if (-not (Test-Path $testsDir)) { New-Item -ItemType Directory -Path $testsDir | Out-Null }

function Pt([double]$x,[double]$y,[double]$z=0) {
    $a = New-Object double[] 3
    $a[0]=$x; $a[1]=$y; $a[2]=$z
    ,$a
}

function Get-BricsCAD {
    foreach ($p in @('BricscadApp.AcadApplication.25','BricscadApp.AcadApplication')) {
        try {
            $a = [Runtime.InteropServices.Marshal]::GetActiveObject($p)
            Write-Host "Attached: $p - $($a.Version)" -ForegroundColor Cyan
            return $a
        } catch { }
    }
    throw 'No running BricsCAD found. Open BricsCAD V25 first.'
}

$acad = Get-BricsCAD
$acad.Visible = $true
$doc  = $acad.Documents.Add()
Start-Sleep -Seconds 2
$ms   = $doc.ModelSpace
Write-Host "Active doc: $($doc.Name)" -ForegroundColor DarkGray

# Geometry (mm) — 4m × 3m × 2.5m room with 200mm walls
$L = 4000.0; $B = 3000.0; $H = 2500.0; $W = 200.0
# AddBox(centerPoint, length, width, height) — length=X, width=Y, height=Z
$walls = @(
    # South wall along X (length = L, width = W)
    @{ Center = Pt ($L/2)   ($W/2)   ($H/2);  L = $L;  Wd = $W;  Ht = $H },
    # North wall along X
    @{ Center = Pt ($L/2)   ($B-$W/2) ($H/2); L = $L;  Wd = $W;  Ht = $H },
    # West wall along Y (length = W, width = B)
    @{ Center = Pt ($W/2)   ($B/2)   ($H/2);  L = $W;  Wd = $B;  Ht = $H },
    # East wall along Y
    @{ Center = Pt ($L-$W/2) ($B/2)  ($H/2);  L = $W;  Wd = $B;  Ht = $H }
)

Write-Host 'Adding 4 wall boxes...' -ForegroundColor Cyan
foreach ($w in $walls) {
    $box = $ms.AddBox($w.Center, $w.L, $w.Wd, $w.Ht)
    $box.Layer = '0'
}

Write-Host 'Adding floor slab...' -ForegroundColor Cyan
$null = $ms.AddBox((Pt ($L/2) ($B/2) -75), $L, $B, 150.0)

$totalSolids = $ms.Count
Write-Host "ModelSpace solid count: $totalSolids" -ForegroundColor DarkGray

# Now send BIMCLASSIFY + BIMSPACE via the command line. SendCommand needs a
# trailing newline; we wrap each step and give BricsCAD time to process.
function Cmd([string]$s) {
    $doc.SendCommand("$s`n")
    Start-Sleep -Milliseconds 400
}

Write-Host 'Sending BIM commands...' -ForegroundColor Cyan
# Classify all solids as Wall — selection by PREVIOUS isn't reliable here, so
# use ALL (modelspace is otherwise empty).
Cmd '(setvar "OSMODE" 0)'
Cmd '(setvar "CMDECHO" 1)'
Cmd '_-VIEW _SWISO'
# BIMCLASSIFY accepts a selection then category. Use _SELECT _ALL then run
# BIMCLASSIFY with default options.
Cmd '_SELECT _ALL '
Cmd '_BIMCLASSIFY _PREVIOUS  _WALL'
# Create BIM Space at the room's centroid. Z slightly above slab so the pick
# point is unambiguously inside the enclosed volume.
$cx = $L / 2; $cy = $B / 2
Cmd "_BIMSPACE $cx,$cy,100"
Cmd '(princ "\nBIM fixture: classify+space sequence sent.\n") (princ)'
Start-Sleep -Seconds 4

# ── Inspect ──────────────────────────────────────────────────────────────────
$types = @{}
foreach ($e in $ms) {
    $t = $e.ObjectName
    if ($types.ContainsKey($t)) { $types[$t]++ } else { $types[$t] = 1 }
}
Write-Host 'ModelSpace contents after BIM commands:' -ForegroundColor Cyan
$types.GetEnumerator() | Sort-Object Name | ForEach-Object { Write-Host "  $($_.Name) : $($_.Value)" -ForegroundColor DarkGray }

Write-Host ''
Write-Host "Saving to $fixture" -ForegroundColor Cyan
$doc.SaveAs($fixture)
Start-Sleep -Seconds 2

Write-Host ''
Write-Host "Fixture ready: $fixture" -ForegroundColor Green
Write-Host 'Next: in BricsCAD V25 -> AIRENO_CONNECT -> AIRENO_EXTRACT' -ForegroundColor Gray
