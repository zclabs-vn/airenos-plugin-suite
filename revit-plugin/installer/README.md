# AirenoOS Revit Plugin — Installer

WiX 5 MSI installer for the AirenoOS Revit plugin. Installs per-machine for all
Windows users; supports Revit 2024 / 2025 / 2026 as opt-in features.

## Build

```powershell
# from this folder
.\build-installer.ps1
```

This runs `..\build-all.ps1` first (which builds the plugin for all three Revit
versions into `..\build\Revit{2024,2025,2026}\Release\`), then compiles the WiX
project. Output: `bin\Release\AirenoOS.Revit.Installer.msi`.

## Install

Double-click the MSI **or**:

```powershell
# Silent install (no UI, requires admin)
msiexec /i AirenoOS.Revit.Installer.msi /qn

# Install with the WiX FeatureTree UI so the end user can pick versions
msiexec /i AirenoOS.Revit.Installer.msi

# Uninstall
msiexec /x AirenoOS.Revit.Installer.msi /qn
```

Installed layout:

```
C:\ProgramData\Autodesk\Revit\Addins\
    2024\
        AirenoOS.Revit.Plugin.addin
        AirenoOS.Revit.Plugin.dll
        System.Text.Json.dll + transitive deps   (net48 bundles them)
    2025\
        AirenoOS.Revit.Plugin.addin
        AirenoOS.Revit.Plugin.dll
        AirenoOS.Revit.Plugin.deps.json          (net8)
    2026\
        AirenoOS.Revit.Plugin.addin
        AirenoOS.Revit.Plugin.dll
        AirenoOS.Revit.Plugin.deps.json          (net8)
```

After install: restart Revit. An **AirenoOS** ribbon tab appears with three
buttons: *Connect*, *Extract Now*, *Apply Writeback*.

## Dev iteration

For fast iteration during plugin development, skip the MSI and use the
per-user dev scripts in the parent folder:

```powershell
..\install.ps1 -Build      # rebuild + deploy to %AppData%
..\uninstall.ps1           # remove
```

The dev scripts target `%AppData%\Autodesk\Revit\Addins\<version>\` (per-user,
no admin needed); the MSI targets `%ProgramData%\Autodesk\Revit\Addins\<version>\`
(all-users, admin required).

## How upgrades work

`UpgradeCode` in `Package.wxs` is fixed (`3f2b4a85-9c61-4a8d-92b1-7d8e6f4c1a23`).
`ProductVersion` (currently `1.0.0.0`) is the comparison key. Bumping
`ProductVersion` and rebuilding produces an MSI that, on install, removes the
previous version first via the `<MajorUpgrade>` directive. End-users get a
clean replacement, not a side-by-side install.

`ProductCode` is auto-generated each build by WiX — that's by design, and the
reason `msiexec /x` against a freshly-rebuilt MSI returns 1605 ("not installed")
if the previously-installed MSI was from an earlier build. Use Programs and
Features (or `wmic product where "name='AirenoOS Revit Plugin'" call uninstall`)
to remove the prior version's ProductCode.
