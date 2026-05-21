<#
  Post-build patch for AirenoOS.AutoCAD.Plugin.msi

  WiX 5 dropped syntactic sugar for the standard MSI ControlCondition table — there is
  no <ControlCondition> element under <Control> anymore. We work around this by opening
  the freshly-built MSI as a database (WindowsInstaller COM) and inserting the rows
  directly.

  Effect at install time:
    - VersionSelectDlg checkbox Cb2024 is DISABLED if ACAD2024_FOUND is empty
    - Same for Cb2025 / Cb2026
  So a user without a given AutoCAD version sees the checkbox greyed out and cannot tick it.
#>

param(
  [string]$MsiPath = "$PSScriptRoot\build\AirenoOS.AutoCAD.Plugin.msi"
)

if (-not (Test-Path $MsiPath)) {
  Write-Error "MSI not found: $MsiPath"
  exit 1
}

$installer = New-Object -ComObject WindowsInstaller.Installer
# Open mode 1 = transact (read/write, commit on Commit())
$db = $installer.GetType().InvokeMember(
  'OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 1))

function Invoke-Sql([string]$sql) {
  $view = $db.GetType().InvokeMember('OpenView','InvokeMethod',$null,$db,@($sql))
  $view.GetType().InvokeMember('Execute','InvokeMethod',$null,$view,$null)
  $view.GetType().InvokeMember('Close','InvokeMethod',$null,$view,$null)
  [Runtime.InteropServices.Marshal]::ReleaseComObject($view) | Out-Null
}

# Rows to inject into ControlCondition: (Dialog_, Control_, Action, Condition)
$rows = @(
  @{Ctl='Cb2024'; Act='Disable'; Cond='NOT ACAD2024_FOUND'},
  @{Ctl='Cb2024'; Act='Enable';  Cond='ACAD2024_FOUND'},
  @{Ctl='Cb2025'; Act='Disable'; Cond='NOT ACAD2025_FOUND'},
  @{Ctl='Cb2025'; Act='Enable';  Cond='ACAD2025_FOUND'},
  @{Ctl='Cb2026'; Act='Disable'; Cond='NOT ACAD2026_FOUND'},
  @{Ctl='Cb2026'; Act='Enable';  Cond='ACAD2026_FOUND'}
)

# Best-effort wipe of any prior rows on these controls (idempotent rebuild)
foreach ($r in $rows) {
  $cond = $r.Cond.Replace("'","''")
  $sql = "DELETE FROM ``ControlCondition`` WHERE ``Dialog_``='VersionSelectDlg' AND ``Control_``='$($r.Ctl)' AND ``Action``='$($r.Act)'"
  try { Invoke-Sql $sql } catch { }
}

# Insert the desired rows
foreach ($r in $rows) {
  $cond = $r.Cond.Replace("'","''")
  $sql = "INSERT INTO ``ControlCondition`` (``Dialog_``,``Control_``,``Action``,``Condition``) VALUES ('VersionSelectDlg','$($r.Ctl)','$($r.Act)','$cond')"
  Invoke-Sql $sql
  Write-Host "  + $($r.Ctl) $($r.Act) WHEN ($cond)"
}

$db.GetType().InvokeMember('Commit','InvokeMethod',$null,$db,$null)
[Runtime.InteropServices.Marshal]::ReleaseComObject($db) | Out-Null
[Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null

Write-Host "Patched: $MsiPath"
