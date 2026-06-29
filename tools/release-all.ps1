<#
.SYNOPSIS
    Full release automation for BTChargeTrayWatcher: version bump, build, manifest update, tag, push, GitHub Release, PR.

.DESCRIPTION
    - Auto-increments minor version (or major with -Major)
    - Updates csproj and all three WinGet manifests
    - Builds installer
    - Computes SHA256
    - Copies manifests to winget-pkgs fork
    - Commits, tags, pushes
    - Creates a GitHub Release with the installer as an asset and auto-generated notes
    - Opens WinGet PR via GitHub CLI
    - Cleans up build artifacts (non-fatal)

.PARAMETER Major
    If set, bump major version instead of minor.

.PARAMETER NoPR
    If set, do not open a PR automatically.

.PARAMETER Notes
    Optional release notes to prepend to the auto-generated GitHub Release notes.
    Use a here-string for multi-line content:
        -Notes @'
        ### Highlights
        - Fixed something
        '@

.PARAMETER Draft
    If set, create the GitHub Release as a draft (allows manual review before publishing).
#>
[CmdletBinding()]
param(
    [switch]$Major,
    [switch]$NoPR,
    [string]$Notes = '',
    [switch]$Draft,
    [string]$Version = '',
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Paths
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$Csproj   = Join-Path $RepoRoot "BTChargeTrayWatcher.csproj"
$IssPath  = Join-Path $RepoRoot "installer\BTChargeTrayWatcher.iss"
$InstallerScript = Join-Path $RepoRoot "tools\build-installer.ps1"
$WingetDir      = Join-Path $RepoRoot "winget"
$ManifestInstaller = Join-Path $WingetDir "peterandree.BTChargeTrayWatcher.installer.yaml"
$ManifestVersion   = Join-Path $WingetDir "peterandree.BTChargeTrayWatcher.yaml"
$ManifestLocale    = Join-Path $WingetDir "peterandree.BTChargeTrayWatcher.locale.en-US.yaml"
$InstallerOut   = Join-Path $RepoRoot "publish\installer"
$WingetPkgs     = "$env:USERPROFILE\src\winget-pkgs"

function Assert-Command([string]$Name)
{
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue))
    {
        throw "Required command '$Name' was not found in PATH."
    }
}

Assert-Command git
Assert-Command gh
Assert-Command dotnet
Assert-Command winget

# Guard: release from a clean repository unless -Force is used.
$status = git -C $RepoRoot status --porcelain
if ($status -and -not $Force)
{
    throw "Working tree is not clean. Commit or stash changes before running release-all.ps1, or rerun with -Force."
}


# 1. Determine target version
Write-Host "==> Reading current version"
[xml]$csprojXml = Get-Content $Csproj -Raw
$verNode = $csprojXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
$parts = ($verNode -split '\.')
if ($Version -ne '') {
    $newVer = $Version
    Write-Host "  Using explicit version: $newVer" -ForegroundColor Yellow
} elseif ($Major) {
    $newVer = "{0}.0.0" -f ([int]$parts[0] + 1)
    Write-Host "  Old: $verNode  New: $newVer (major bump)" -ForegroundColor Green
} else {
    $newVer = "{0}.{1}.0" -f $parts[0], ([int]$parts[1] + 1)
    Write-Host "  Old: $verNode  New: $newVer (minor bump)" -ForegroundColor Green
}

# 1b. Guard: abort if this version was already released
$tag = "v$newVer"
$tagExistsLocal  = [bool](git -C $RepoRoot tag --list $tag 2>$null | Where-Object { $_ -ne '' })
$tagExistsRemote = [bool](git -C $RepoRoot ls-remote --tags origin "refs/tags/$tag" 2>$null | Where-Object { $_ -ne '' })
$releaseLines    = gh release list --repo peterandree/BTChargeTrayWatcher --limit 100 2>$null
$releaseExists   = [bool]($releaseLines | Where-Object { $_ -match "\b$([regex]::Escape($tag))\b" })
if ($tagExistsLocal -or $tagExistsRemote -or $releaseExists) {
    if (-not $Force) {
        Write-Error "Tag or release '$tag' already exists. Delete it first or use -Force to overwrite."
        exit 1
    } else {
        Write-Warning "Overwriting existing tag/release for $tag due to -Force."
        if ($releaseExists) {
            gh release delete $tag --repo peterandree/BTChargeTrayWatcher --yes
            Write-Host "  Deleted existing GitHub release $tag"
        }
        if ($tagExistsLocal) {
            git -C $RepoRoot tag -d $tag
            Write-Host "  Deleted local tag $tag"
        }
        if ($tagExistsRemote) {
            git -C $RepoRoot push origin ":refs/tags/$tag"
            Write-Host "  Deleted remote tag $tag"
        }
    }
}

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

# 5. Update WinGet manifests (all 3 files)
Write-Host "==> Updating WinGet manifests"
$yamlInstaller = Get-Content $ManifestInstaller -Raw
$yamlInstaller = $yamlInstaller -creplace 'PackageVersion: [^\r\n]+', "PackageVersion: $newVer"
$yamlInstaller = $yamlInstaller -creplace '(InstallerUrl:\s*)[^\r\n]+', ('${1}' + "https://github.com/peterandree/BTChargeTrayWatcher/releases/download/v$newVer/$(Split-Path $installer.Name -Leaf)")
$yamlInstaller = $yamlInstaller -creplace '(InstallerSha256:\s*)[^\r\n]+', ('${1}' + $hash)
[System.IO.File]::WriteAllText($ManifestInstaller, $yamlInstaller, [System.Text.UTF8Encoding]::new($false))

$yamlVer = Get-Content $ManifestVersion -Raw
$yamlVer = $yamlVer -creplace 'PackageVersion: [^\r\n]+', "PackageVersion: $newVer"
[System.IO.File]::WriteAllText($ManifestVersion, $yamlVer, [System.Text.UTF8Encoding]::new($false))

$yamlLocale = Get-Content $ManifestLocale -Raw
$yamlLocale = $yamlLocale -creplace 'PackageVersion: [^\r\n]+', "PackageVersion: $newVer"
[System.IO.File]::WriteAllText($ManifestLocale, $yamlLocale, [System.Text.UTF8Encoding]::new($false))

# 5b. Verify manifests contain the expected version before proceeding
$verifyFiles = @($ManifestInstaller, $ManifestVersion, $ManifestLocale)
foreach ($vf in $verifyFiles) {
    $content = Get-Content $vf -Raw
    if ($content -notmatch "PackageVersion:\s*$([regex]::Escape($newVer))") {
        Write-Error "Manifest verification failed: $vf does not contain PackageVersion $newVer. Aborting before any commits."
        exit 1
    }
}
Write-Host "  Manifests verified: PackageVersion $newVer confirmed in all 3 files."

# 6. Validate manifest
Write-Host "==> Validating manifest"
winget validate $WingetDir

# 7. Copy manifests to winget-pkgs fork
Write-Host "==> Copying manifests to winget-pkgs fork"
$destDir = Join-Path $WingetPkgs "manifests\p\Peterandree\BTChargeTrayWatcher\$newVer"
if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
Copy-Item $ManifestInstaller (Join-Path $destDir "peterandree.BTChargeTrayWatcher.installer.yaml") -Force
Copy-Item $ManifestVersion  (Join-Path $destDir "peterandree.BTChargeTrayWatcher.yaml") -Force
Copy-Item $ManifestLocale   (Join-Path $destDir "peterandree.BTChargeTrayWatcher.locale.en-US.yaml") -Force

# 8. Commit, tag, push (main repo)
Write-Host "==> Committing and tagging main repo"
git -C $RepoRoot add $Csproj $ManifestInstaller $ManifestVersion $ManifestLocale
git -C $RepoRoot add $IssPath
$commitMsg = 'Release v' + $newVer + ': build, manifest, installer'
git -C $RepoRoot commit -m $commitMsg
$tag = "v$newVer"
git -C $RepoRoot tag $tag
Write-Host "  Tag: $tag"
git -C $RepoRoot push
Write-Host "  Pushed main repo"
git -C $RepoRoot push origin $tag
Write-Host "  Pushed tag"

# 9. Create GitHub Release
Write-Host "==> Creating GitHub Release"
$releaseArgs = @(
    'release', 'create', $tag,
    $installer.FullName,
    '--repo', 'peterandree/BTChargeTrayWatcher',
    '--title', "BTChargeTrayWatcher $newVer",
    '--generate-notes'
)
if ($Notes -ne '') { $releaseArgs += @('--notes', $Notes) }
if ($Draft)        { $releaseArgs += '--draft' }
gh @releaseArgs
Write-Host "  GitHub Release created: https://github.com/peterandree/BTChargeTrayWatcher/releases/tag/$tag"

# 10. Commit, push, PR (winget-pkgs fork)
if (-not (Test-Path (Join-Path $WingetPkgs ".git"))) {
    Write-Warning "winget-pkgs fork not found at $WingetPkgs — skipping winget-pkgs commit and PR."
    Write-Warning "Clone your fork to $WingetPkgs and re-run, or copy winget\ manifests manually."
} else {
Write-Host "==> Committing and pushing to winget-pkgs fork"
$branch = "btchargetraywatcher-$newVer"
$wingetMsg = "BTChargeTrayWatcher $newVer manifest update"
git -C $WingetPkgs fetch origin
git -C $WingetPkgs checkout master
git -C $WingetPkgs pull --ff-only origin master
git -C $WingetPkgs checkout -B $branch
Write-Host "  Branch: $branch"
git -C $WingetPkgs add $destDir
git -C $WingetPkgs diff --cached --quiet
if ($LASTEXITCODE -eq 0) {
    Write-Warning "No manifest changes detected in winget-pkgs fork; skipping commit/push/PR."
} else {
    git -C $WingetPkgs commit -m $wingetMsg
    git -C $WingetPkgs push -u origin $branch --force-with-lease
    if (-not $NoPR) {
        Write-Host "==> Opening PR via GitHub CLI"
        $ghUser = (gh api user --jq '.login' 2>$null)
        gh pr create --repo microsoft/winget-pkgs --base master --head "${ghUser}:${branch}" --title "$wingetMsg" --body "Automated manifest update for $newVer."
    }
}
} # end winget-pkgs guard

# 11. Cleanup (non-fatal)
Write-Host "==> Cleaning up build artifacts (non-fatal)"
$clean = @(
  "$RepoRoot\bin",
  "$RepoRoot\obj",
  "$RepoRoot\.vs",
  "$RepoRoot\TestResults"
)
foreach ($c in $clean) { try { Remove-Item -Recurse -Force $c -ErrorAction Stop } catch {} }
Write-Host "Release script complete."
