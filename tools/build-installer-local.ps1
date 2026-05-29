<#
.SYNOPSIS
    Builds a local Windows EXE installer for BTChargeTrayWatcher without
    changing version metadata or any other repository files.

.DESCRIPTION
    Local test pipeline:
      1. Optionally publishes the app (self-contained, compressed, single-file).
      2. Compiles the existing Inno Setup script as-is.
      3. Prints the installer output path and SHA256.

.PARAMETER SkipPublish
    Skip the dotnet publish step and reuse the current publish output.

.EXAMPLE
    .\tools\build-installer-local.ps1

.EXAMPLE
    .\tools\build-installer-local.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
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
# Step 1: Publish
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
# Step 2: Compile installer (no repo file modifications)
# ---------------------------------------------------------------------------
Write-Step "Compiling installer with Inno Setup"
& $Iscc $IssPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }

# ---------------------------------------------------------------------------
# Step 3: Output
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
Write-Host "Build complete." -ForegroundColor Green