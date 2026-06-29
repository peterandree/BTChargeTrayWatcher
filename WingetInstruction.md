# WingetInstruction

This guide explains how to create a new BTChargeTrayWatcher release and submit the WinGet manifest using the scripts in tools/.

## Scripts in tools/

- `tools/build-installer-local.ps1`
- `tools/build-installer.ps1`
- `tools/release-all.ps1`

## What each script does

- `build-installer-local.ps1`
  - Publishes the app and builds an installer for local testing.
  - Does not change version metadata files.
  - Safe for dry-runs.

- `build-installer.ps1`
  - Publishes and builds installer.
  - Patches `installer/BTChargeTrayWatcher.iss` to the provided version (or csproj version).
  - Prints SHA256 and expected InstallerUrl/InstallerSha256 values.

- `release-all.ps1`
  - Bumps version (minor by default, major with `-Major`, or explicit with `-Version`).
  - Builds installer.
  - Updates all `winget/*.yaml` manifests.
  - Validates manifests using `winget validate`.
  - Commits + tags + pushes main repo.
  - Creates GitHub release and uploads installer asset.
  - Copies manifests into your `winget-pkgs` fork and opens PR (unless `-NoPR`).

## Prerequisites

- Windows + PowerShell
- .NET 10 SDK
- Inno Setup 6 (`winget install JRSoftware.InnoSetup`)
- Git + GitHub CLI (`gh`) authenticated for `peterandree/BTChargeTrayWatcher`
- WinGet CLI (`winget`)
- Your `winget-pkgs` fork cloned at:
  - `%USERPROFILE%\src\winget-pkgs`

## Recommended release flow

1. Run tests before release:

```powershell
dotnet test
```

2. Build a local installer smoke-check first:

```powershell
.\tools\build-installer-local.ps1
```

3. Run full release automation:

```powershell
.\tools\release-all.ps1
```

Default behavior:
- version bump: `x.y.z -> x.(y+1).0`
- commits, tags, pushes
- creates GitHub release
- opens winget-pkgs PR

## Common release options

- Explicit version:

```powershell
.\tools\release-all.ps1 -Version 3.2.0
```

- Major bump:

```powershell
.\tools\release-all.ps1 -Major
```

- Create draft GitHub release:

```powershell
.\tools\release-all.ps1 -Draft
```

- Skip WinGet PR creation:

```powershell
.\tools\release-all.ps1 -NoPR
```

- Force overwrite existing tag/release:

```powershell
.\tools\release-all.ps1 -Version 3.2.0 -Force
```

## Manual winget-only path (if needed)

If you only want installer + manifest values first:

```powershell
.\tools\build-installer.ps1 -Version 3.2.0
```

Then:

1. Update the three files under `winget/`.
2. Validate:

```powershell
winget validate .\winget
```

3. Copy manifests to:

`%USERPROFILE%\src\winget-pkgs\manifests\p\Peterandree\BTChargeTrayWatcher\<version>\`

4. Commit/push in winget-pkgs and open PR to `microsoft/winget-pkgs`.

## Script audit summary (correctness/completeness)

Checked scripts:
- `tools/build-installer.ps1`
- `tools/build-installer-local.ps1`
- `tools/release-all.ps1`

Verified:
- All three scripts parse correctly in PowerShell.
- Full release script now includes important guards and completeness fixes:
  - Tool preflight checks (`git`, `gh`, `dotnet`, `winget`)
  - Clean working-tree guard (unless `-Force`)
  - `-Force` now removes existing tag/release before recreating
  - Includes `installer/BTChargeTrayWatcher.iss` in release commit
  - More robust `winget-pkgs` branch handling and no-op PR skip

## Troubleshooting

- `winget validate` fails:
  - Ensure installer URL points to the exact uploaded GitHub release asset.
  - Recompute SHA256 from the produced installer.

- `gh` release/PR fails:
  - Run `gh auth status` and re-authenticate if needed.

- Inno Setup not found:
  - Install with `winget install JRSoftware.InnoSetup`.

- `winget-pkgs` PR step skipped:
  - Confirm your fork exists at `%USERPROFILE%\src\winget-pkgs`.
