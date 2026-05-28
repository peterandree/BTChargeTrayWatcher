# Optional: One-Command Automated Release

You can automate the entire release process (version bump, build, manifest update, tag, push, PR) with:

```powershell
cd BTChargeTrayWatcher
.\tools\release-all.ps1
```

**Features:**
- Auto-increments the minor version (e.g., 3.0.0 → 3.1.0)
- Updates csproj and WinGet manifest
- Builds the installer
- Computes SHA256 and updates manifest
- Copies manifest to your winget-pkgs fork (see script for path)
- Commits, tags, pushes
- Opens a PR to microsoft/winget-pkgs (via GitHub CLI)
- Cleans up build artifacts (non-fatal)

**Options:**
- `-Major` — bump the major version instead of minor
- `-NoPR` — skip opening a PR automatically

**Requirements:**
- [GitHub CLI](https://cli.github.com/) (`gh`)
- [WinGet CLI](https://learn.microsoft.com/en-us/windows/package-manager/winget/)
- Your winget-pkgs fork cloned to `%USERPROFILE%\src\winget-pkgs`

---
# Release Process for BTChargeTrayWatcher

This guide describes how to build, package, and publish a new release for BTChargeTrayWatcher, including WinGet integration.

---

## 1. Prerequisites
- Windows 10/11 with .NET 10 SDK
- PowerShell
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (install via `winget install JRSoftware.InnoSetup`)
- GitHub account with push access
- (Optional) WinGet CLI for manifest validation

---

## 2. Build and Package the App

1. **Update the version:**
   - Edit `<Version>` in `BTChargeTrayWatcher.csproj` (e.g., `3.0.0`).

2. **Build the installer:**
   ```powershell
   .\tools\build-installer.ps1
   ```
   - Output: `publish\installer\BTChargeTrayWatcher_<version>_Setup.exe`

---

## 3. Create a GitHub Release

1. Go to the [GitHub Releases page](https://github.com/peterandree/BTChargeTrayWatcher/releases).
2. Click "Draft a new release".
3. Tag: `v<version>` (e.g., `v3.0.0`)
4. Title: `BTChargeTrayWatcher v<version>`
5. Description: List highlights, fixes, and SHA256 for the installer (see build output).
6. Upload the EXE installer from `publish/installer/`.
7. Publish the release.

---

## 4. Update the WinGet Manifest

1. Edit `winget/peterandree.BTChargeTrayWatcher.installer.yaml`:
   - `InstallerType: exe`
   - `InstallerUrl:` (link to the uploaded EXE in your GitHub release)
   - `InstallerSha256:` (SHA256 from build output)
   - `InstallerSwitches:`
     - `Silent: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`
     - `SilentWithProgress: /SILENT /SUPPRESSMSGBOXES /NORESTART`

2. Validate the manifest:
   ```powershell
   winget validate .\winget\peterandree.BTChargeTrayWatcher.installer.yaml
   ```

3. Copy the updated YAML to your `winget-pkgs` fork at:
   `manifests/p/Peterandree/BTChargeTrayWatcher/<version>/`

4. Commit and push. Open or update your PR to `microsoft/winget-pkgs`.

---

## 5. Final Checklist
- [ ] Installer tested on a clean Windows VM
- [ ] Start Menu shortcut created
- [ ] Uninstaller entry present
- [ ] WinGet manifest validated
- [ ] PR merged in `winget-pkgs`

---

## 6. Troubleshooting
- If WinGet validation fails due to hash mismatch, re-download the EXE from the GitHub release and recompute the SHA256.
- For Start Menu integration, always use the EXE installer (not portable or MSIX unless you have a trusted cert).
- For further help, see [TESTING.md](TESTING.md) or open an issue on GitHub.
