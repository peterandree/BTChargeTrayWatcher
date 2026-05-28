<#
.SYNOPSIS
    Full release automation for BTChargeTrayWatcher: version bump, build, manifest update, tag, push, PR.

.DESCRIPTION
    - Auto-increments minor version (or major with -Major)
    - Updates csproj and WinGet manifest
    - Builds installer
    - Computes SHA256
    - Copies manifest to winget-pkgs fork
    - Commits, tags, pushes
    - Opens PR via GitHub CLI
    - Cleans up build artifacts (non-fatal)

.PARAMETER Major
    If set, bump major version instead of minor.

.PARAMETER NoPR
    If set, do not open a PR automatically.
#>
[CmdletBinding()]
param(
    [switch]$Major,
    [switch]$NoPR
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Paths
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$Csproj   = Join-Path $RepoRoot "BTChargeTrayWatcher.csproj"
$InstallerScript = Join-Path $RepoRoot "tools\build-installer.ps1"
$ManifestYaml   = Join-Path $RepoRoot "winget\peterandree.BTChargeTrayWatcher.installer.yaml"
$InstallerOut   = Join-Path $RepoRoot "publish\installer"
$WingetPkgs     = "$env:USERPROFILE\src\winget-pkgs"

# 1. Parse and bump version
Write-Host "==> Reading current version"
[xml]$csprojXml = Get-Content $Csproj -Raw
$verNode = $csprojXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
$parts = ($verNode -split '\.')
if ($Major) {
    $newVer = "{0}.0.0" -f ([int]$parts[0] + 1)
} else {
    $newVer = "{0}.{1}.0" -f $parts[0], ([int]$parts[1] + 1)
}
Write-Host "  Old: $verNode  New: $newVer" -ForegroundColor Green

# 2. Update csproj
Write-Host "==> Updating csproj version"
$csprojText = Get-Content $Csproj -Raw
$csprojText = $csprojText -creplace '<Version>[^<]+</Version>', "<Version>$newVer</Version>"
[System.IO.File]::WriteAllText($Csproj, $csprojText, [System.Text.UTF8Encoding]::new($false))

# 3. Build installer
Write-Host "==> Building installer"
& $InstallerScript

# 4. Find installer and hash
$installer = Get-Item "$InstallerOut\BTChargeTrayWatcher*Setup.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$hash = (Get-FileHash -Algorithm SHA256 $installer.FullName).Hash
Write-Host "  SHA256: $hash"

# 5. Update WinGet manifest
Write-Host "==> Updating WinGet manifest"
$yaml = Get-Content $ManifestYaml -Raw
$yaml = $yaml -creplace 'PackageVersion: [^\r\n]+', "PackageVersion: $newVer"
$yaml = $yaml -creplace 'InstallerUrl: .+', "InstallerUrl: https://github.com/peterandree/BTChargeTrayWatcher/releases/download/v$newVer/$(Split-Path $installer.Name -Leaf)"
$yaml = $yaml -creplace 'InstallerSha256: .+', "InstallerSha256: $hash"
[System.IO.File]::WriteAllText($ManifestYaml, $yaml, [System.Text.UTF8Encoding]::new($false))

# 6. Validate manifest
Write-Host "==> Validating manifest"
winget validate $ManifestYaml

# 7. Copy manifest to winget-pkgs fork
Write-Host "==> Copying manifest to winget-pkgs fork"
$destDir = Join-Path $WingetPkgs "manifests\p\Peterandree\BTChargeTrayWatcher\$newVer"
if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
Copy-Item $ManifestYaml (Join-Path $destDir "peterandree.BTChargeTrayWatcher.installer.yaml") -Force

# 8. Commit, tag, push (main repo)
Write-Host "==> Committing and tagging main repo"
git -C $RepoRoot add $Csproj $ManifestYaml
$commitMsg = "Release v$newVer: build, manifest, installer"
git -C $RepoRoot commit -m $commitMsg
$tag = "v$newVer"
git -C $RepoRoot tag $tag
Write-Host "  Tag: $tag"
git -C $RepoRoot push
Write-Host "  Pushed main repo"
git -C $RepoRoot push origin $tag
Write-Host "  Pushed tag"

# 9. Commit, push, PR (winget-pkgs fork)
Write-Host "==> Committing and pushing to winget-pkgs fork"
git -C $WingetPkgs add $destDir\peterandree.BTChargeTrayWatcher.installer.yaml
$wingetMsg = "BTChargeTrayWatcher $newVer manifest update"
git -C $WingetPkgs commit -m $wingetMsg
$branch = "btchargetraywatcher-$newVer"
git -C $WingetPkgs checkout -b $branch
Write-Host "  Branch: $branch"
git -C $WingetPkgs push -u origin $branch
if (-not $NoPR) {
    Write-Host "==> Opening PR via GitHub CLI"
    gh pr create --repo microsoft/winget-pkgs --base master --head $env:USERNAME:$branch --title "$wingetMsg" --body "Automated manifest update for $newVer."
}

# 10. Cleanup (non-fatal)
Write-Host "==> Cleaning up build artifacts (non-fatal)"
$clean = @(
  "$RepoRoot\bin",
  "$RepoRoot\obj",
  "$RepoRoot\.vs",
  "$RepoRoot\TestResults"
)
foreach ($c in $clean) { try { Remove-Item -Recurse -Force $c -ErrorAction Stop } catch {} }
Write-Host "Release script complete."
