<#
  Generate the test fixture drawing `tests/airenos-fixtures.dwg`.

  Drives AutoCAD via COM Automation. Requires AutoCAD 2024/2025/2026 already running
  (we attach to a live instance rather than launching a new one to avoid licence prompts).

  Usage:
    PS> .\create-fixture.ps1                    # uses currently running AutoCAD
    PS> .\create-fixture.ps1 -Version 25        # forces ProgId AutoCAD.Application.25 (AutoCAD 2025)

  Produces:
    tests/airenos-fixtures.dwg          - main test drawing
    tests/xref-sample.dwg                - auxiliary file referenced as XREF

  Manual finishing steps (documented at the end of the script output):
    1. Open airenos-fixtures.dwg in AutoCAD, run BEDIT, set up a Visibility parameter on
       block DOOR (cannot be done via COM).
    2. Run XATTACH on xref-sample.dwg if you want the xref entity present.
#>

[CmdletBinding()]
param(
  [string]$Version = ''   # leave blank for any running AutoCAD; otherwise '24', '25', '26'
)

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = (Get-Item $PSScriptRoot).Parent.FullName }
$testsDir  = Join-Path $root 'tests'
$fixture   = Join-Path $testsDir 'airenos-fixtures.dwg'
$xrefFile  = Join-Path $testsDir 'xref-sample.dwg'

if (-not (Test-Path $testsDir)) { New-Item -ItemType Directory -Path $testsDir | Out-Null }

# ── Attach to live AutoCAD ────────────────────────────────────────────────────────
function Get-AutoCAD([string]$v) {
  $progIds = @(
    @{Id="AutoCAD.Application.$v"; When=$v -ne ''},
    @{Id='AutoCAD.Application.25'; When=$true},
    @{Id='AutoCAD.Application.24'; When=$true},
    @{Id='AutoCAD.Application.26'; When=$true},
    @{Id='AutoCAD.Application';    When=$true}
  ) | Where-Object When
  foreach ($p in $progIds) {
    try {
      $a = [Runtime.InteropServices.Marshal]::GetActiveObject($p.Id)
      Write-Host "Attached: $($p.Id) - $($a.Version)" -ForegroundColor Cyan
      return $a
    } catch { }
  }
  throw "No running AutoCAD found. Open AutoCAD 2024/2025/2026 first."
}

$acad = Get-AutoCAD $Version

# ── Helper: build a Variant double[] for COM point/array args ─────────────────────
function Pt([double]$x,[double]$y,[double]$z=0) {
  $a = New-Object double[] 3; $a[0]=$x; $a[1]=$y; $a[2]=$z; ,$a
}
function Arr([double[]]$values) { ,$values }

# ── Helper: SendCommand with COM-busy retry ───────────────────────────────────────
function Send($doc,$cmd) {
  for ($i=0; $i -lt 40; $i++) {
    try { $doc.SendCommand($cmd); return $true }
    catch [System.Runtime.InteropServices.COMException] { Start-Sleep -Milliseconds 250 }
  }
  $false
}

# ─────────────────────────────────────────────────────────────────────────────────
#  Step 1 - create xref-sample.dwg
# ─────────────────────────────────────────────────────────────────────────────────
Write-Host "`n[1/2] Building $xrefFile" -ForegroundColor Yellow

# COM tip: after Documents.Add(), AutoCAD may need a beat before ModelSpace is wired.
# We also use ActiveDocument (which the new doc becomes after Add) rather than the return value,
# because in some PowerShell COM contexts the return value's automation interface arrives null.
$null = $acad.Documents.Add()
Start-Sleep -Milliseconds 500
$xrefDoc = $acad.ActiveDocument
if (-not $xrefDoc) { throw "ActiveDocument is null after Documents.Add()" }
$ms = $xrefDoc.ModelSpace

# Simple content: 1 circle + 1 text
$null = $ms.AddCircle((Pt 0 0), 20.0)
$null = $ms.AddText('XREF SOURCE', (Pt -15 25), 5.0)

# SaveAs to xref path. AutoCAD may prompt for format if the file is new - we use the COM SaveAs which
# bypasses the dialog when given an explicit name + format.
# 60 = AcDwgVersion.ac2018 (DWG 2018, supported by AutoCAD 2024/25/26)
try { $xrefDoc.SaveAs($xrefFile, 60) } catch { $xrefDoc.SaveAs($xrefFile) }
Write-Host "  saved: $xrefFile"
$xrefDoc.Close($false)

# ─────────────────────────────────────────────────────────────────────────────────
#  Step 2 - create airenos-fixtures.dwg with all required entities
# ─────────────────────────────────────────────────────────────────────────────────
Write-Host "`n[2/2] Building $fixture" -ForegroundColor Yellow

$null = $acad.Documents.Add()
Start-Sleep -Milliseconds 500
$doc = $acad.ActiveDocument
if (-not $doc) { throw "ActiveDocument is null after Documents.Add()" }
$ms = $doc.ModelSpace
$blocks = $doc.Blocks

# --- 2a. Define block "TEST" with an attribute definition ROOM_TAG -------------
$testBlk = $blocks.Add((Pt 0 0), 'TEST')
# Block geometry: a square outline 10x10
$sqPts = New-Object double[] 10
$sqPts[0]=-5;  $sqPts[1]=-5
$sqPts[2]= 5;  $sqPts[3]=-5
$sqPts[4]= 5;  $sqPts[5]= 5
$sqPts[6]=-5;  $sqPts[7]= 5
$sqPts[8]=-5;  $sqPts[9]=-5
$pl = $testBlk.AddLightWeightPolyline($sqPts)
$pl.Closed = $true
# Attribute definition inside block - picked up at insert time
# AddAttribute(height, mode, prompt, insertionPoint, tag, value)
$null = $testBlk.AddAttribute(2.5, 0, 'Room tag', (Pt -4 -2), 'ROOM_TAG', 'A01')
Write-Host "  + block 'TEST' defined with attribute ROOM_TAG"

# --- 2b. Insert TEST block reference at (50, 50) and set attribute value -------
$insPt = Pt 50 50
$blkRef = $ms.InsertBlock($insPt, 'TEST', 1.0, 1.0, 1.0, 0.0)
# Set attribute value on the reference
foreach ($att in $blkRef.GetAttributes()) {
  if ($att.TagString -eq 'ROOM_TAG') { $att.TextString = 'A01' }
}
Write-Host "  + inserted TEST block at (50,50) with ROOM_TAG=A01"

# --- 2c. Closed LWPolyline (room boundary) -------------------------------------
$roomPts = New-Object double[] 10
$roomPts[0]=0;   $roomPts[1]=0
$roomPts[2]=120; $roomPts[3]=0
$roomPts[4]=120; $roomPts[5]=80
$roomPts[6]=0;   $roomPts[7]=80
$roomPts[8]=0;   $roomPts[9]=0
$room = $ms.AddLightWeightPolyline($roomPts)
$room.Closed = $true
Write-Host "  + closed LWPolyline 120x80 (room boundary)"

# --- 2d. MText "ROOM A" near TEST block ---------------------------------------
$mt = $ms.AddMText((Pt 60 55), 100, 'ROOM A')
$mt.Height = 4.0
Write-Host "  + MText 'ROOM A' at (60,55)"

# --- 2e. Aligned dimension along bottom of room -------------------------------
try {
  $null = $ms.AddDimAligned((Pt 0 0), (Pt 120 0), (Pt 60 -15))
  Write-Host "  + Aligned dimension"
} catch {
  Write-Host "  ! Dimension failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# --- 2f. Hatch on the room polyline -------------------------------------------
# COM marshalling for AppendOuterLoop is finicky; use explicit System.Object[] array
try {
  $hatch = $ms.AddHatch(0, 'ANSI31', $true)
  $boundary = New-Object 'System.Object[]' 1
  $boundary[0] = $room
  $hatch.AppendOuterLoop($boundary)
  $hatch.Evaluate()
  $hatch.PatternScale = 5.0
  Write-Host "  + Hatch (ANSI31, scale 5) on room polyline"
} catch {
  Write-Host "  ! Hatch failed (add manually via -HATCH command): $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# --- 2g. Dynamic block 'DOOR' - define static block; visibility parameter requires BEDIT --
try {
  $doorBlk = $blocks.Add((Pt 0 0), 'DOOR')
  $null = $doorBlk.AddArc((Pt 0 0), 6.0, 0.0, [Math]::PI/2)
  $null = $doorBlk.AddLine((Pt 0 0), (Pt 6 0))
  $null = $ms.InsertBlock((Pt 200 50), 'DOOR', 1.0, 1.0, 1.0, 0.0)
  Write-Host "  + DOOR block inserted (visibility param to be added manually via BEDIT)"
} catch {
  Write-Host "  ! DOOR block failed: $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# --- 2h. Attach XREF -----------------------------------------------------------
try {
  $null = $doc.ModelSpace.AttachExternalReference($xrefFile, 'xref-sample', (Pt -50 0), 1.0, 1.0, 1.0, 0.0, $false, '')
  Write-Host "  + XREF attached: $xrefFile"
} catch {
  Write-Host "  ! XREF attach failed (manual: XATTACH this path): $($_.Exception.Message)" -ForegroundColor DarkYellow
}

# --- 2i. Save fixture (retry around RPC_E_CALL_REJECTED - XREF attach may
#         leave COM momentarily busy on background tasks) -----------------------
$saved = $false
for ($i=0; $i -lt 40; $i++) {
  try { $doc.SaveAs($fixture, 60); $saved = $true; break }
  catch [System.Runtime.InteropServices.COMException] { Start-Sleep -Milliseconds 500 }
  catch {
    # Maybe the format-version flag is wrong; retry without explicit version
    try { $doc.SaveAs($fixture); $saved = $true; break }
    catch { Start-Sleep -Milliseconds 500 }
  }
}
if ($saved) {
  Write-Host "`n  saved: $fixture" -ForegroundColor Green
} else {
  Write-Host "`n  ! SaveAs failed - file not written. Try running script again." -ForegroundColor Red
}

# ── Summary ──────────────────────────────────────────────────────────────────────
Write-Host "`n=== Fixture build complete ===" -ForegroundColor Green
Write-Host "Files:"
Write-Host "  - $xrefFile"
Write-Host "  - $fixture"
Write-Host ""
Write-Host "Manual finishing steps (cannot be automated via COM):"
Write-Host "  1. Open airenos-fixtures.dwg, run BEDIT on block 'DOOR':"
Write-Host "       - Add a Visibility parameter"
Write-Host "       - Create two visibility states: 'Open' and 'Closed'"
Write-Host "       - Save block"
Write-Host "  2. Verify XREF attached correctly (XREF palette should list 'xref-sample')"
