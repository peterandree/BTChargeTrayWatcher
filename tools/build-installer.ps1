<#
.SYNOPSIS
    Builds a Windows EXE installer for BTChargeTrayWatcher using Inno Setup.

.DESCRIPTION
    Automated pipeline:
      1. Resolves the app version from BTChargeTrayWatcher.csproj.
      2. Publishes the app (self-contained, compressed, single-file).
      3. Patches AppVersion in the .iss script.
      4. Compiles the installer with ISCC.exe.
      5. Prints the output path and SHA256 for the WinGet manifest.

.PARAMETER SkipPublish
    Skip the dotnet publish step (use the existing publish output).

.PARAMETER Version
    Override the version string (e.g. "3.0.1"). Defaults to the csproj version.

.EXAMPLE
    .\tools\build-installer.ps1

.EXAMPLE
    .\tools\build-installer.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$Version      = "",
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$RepoRoot   = Resolve-Path (Join-Path $PSScriptRoot "..")
$CsprojPath = Join-Path $RepoRoot "BTChargeTrayWatcher.csproj"
$IssPath    = Join-Path $RepoRoot "installer\BTChargeTrayWatcher.iss"
$PublishDir = Join-Path $RepoRoot "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

# Locate ISCC.exe
$IsccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    throw "Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup"
}

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
# Step 1: Resolve version
# ---------------------------------------------------------------------------
if (-not $Version) {
    $csprojXml   = [xml](Get-Content $CsprojPath -Raw)
    $versionNode = $csprojXml.Project.PropertyGroup |
                   ForEach-Object { $_.Version } |
                   Where-Object   { $_ } |
                   Select-Object  -First 1
    $Version = if ($versionNode) { $versionNode } else { "3.0.0" }
}
Write-Host "Version: $Version" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Step 2: Publish
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Step "Publishing app (self-contained, compressed, single-file)"
    & dotnet publish $CsprojPath -c Release -r win-x64 /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true /p:SelfContained=true /p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}

$ExePath = Join-Path $PublishDir "BTChargeTrayWatcher.exe"
if (-not (Test-Path $ExePath)) {
    throw "Published EXE not found at: $ExePath"
}
Write-Host "  EXE: $([math]::Round((Get-Item $ExePath).Length/1MB,2)) MB"

# ---------------------------------------------------------------------------
# Step 3: Patch version in .iss
# ---------------------------------------------------------------------------
Write-Step "Patching .iss version to $Version"
$iss = Get-Content $IssPath -Raw
$iss = $iss -creplace '#define AppVersion\s+"[^"]*"', "#define AppVersion   ""$Version"""
[System.IO.File]::WriteAllText($IssPath, $iss, [System.Text.UTF8Encoding]::new($false))
Write-Host "  Updated AppVersion in $(Split-Path $IssPath -Leaf)"

# ---------------------------------------------------------------------------
# Step 4: Compile installer
# ---------------------------------------------------------------------------
Write-Step "Compiling installer with Inno Setup"
& $Iscc $IssPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

# ---------------------------------------------------------------------------
# Step 5: Output
# ---------------------------------------------------------------------------
Write-Step "Output"
$InstallerPattern = Join-Path $RepoRoot "publish\installer\BTChargeTrayWatcher_*_Setup.exe"
$InstallerPath    = Get-Item $InstallerPattern | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
$Hash             = (Get-FileHash -Algorithm SHA256 $InstallerPath).Hash

Write-Host ""
Write-Host "  Installer: $InstallerPath"
Write-Host "  Size:      $([math]::Round((Get-Item $InstallerPath).Length/1MB,2)) MB"
Write-Host "  SHA256:    $Hash"
Write-Host ""
Write-Host "  InstallerType:   exe"
Write-Host "  InstallerUrl:    https://github.com/peterandree/BTChargeTrayWatcher/releases/download/v$Version/BTChargeTrayWatcher_$($Version)_Setup.exe"
Write-Host "  InstallerSha256: $Hash"
Write-Host "  InstallerSwitches:"
Write-Host "    Silent: /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
