<#
.SYNOPSIS
    Builds a signed MSIX package for BTChargeTrayWatcher.

.DESCRIPTION
    Fully automated pipeline:
      1. Publishes the app (self-contained, compressed, single-file).
      2. Generates PNG icon assets from app.ico.
      3. Stages the MSIX layout (EXE + AppxManifest.xml + Assets).
      4. Creates (or reuses) a self-signed dev certificate if no thumbprint supplied.
      5. Patches AppxManifest.xml Publisher and Version.
      6. Packs with makeappx.exe.
      7. Signs with signtool.exe.

.PARAMETER Version
    Four-part MSIX version, e.g. "3.0.0.0". Defaults to project version.

.PARAMETER Publisher
    Certificate Subject DN. Must match the signing certificate exactly.
    Defaults to "CN=Peterandree" (self-signed dev cert).

.PARAMETER CertThumbprint
    Thumbprint of an existing certificate in the current user's cert store.
    If omitted, a self-signed dev cert is created automatically.

.PARAMETER OutDir
    Output directory for the .msix file. Defaults to .\publish\msix.

.PARAMETER SkipPublish
    If set, skip the dotnet publish step (use the existing publish output).

.EXAMPLE
    # Dev build (self-signed)
    .\tools\build-msix.ps1

.EXAMPLE
    # Release build with a real EV/trusted cert already in your cert store
    .\tools\build-msix.ps1 -Publisher "CN=Your Name, O=Your Org, C=US" -CertThumbprint "ABCDEF1234..."

.NOTES
    Requires: Windows SDK 10.0.22621.0 or later (makeappx.exe + signtool.exe).
    IMPORTANT: For distribution via WinGet, sign with a trusted certificate.
               Self-signed packages can only be installed on devices that
               explicitly trust the developer certificate.
#>
[CmdletBinding()]
param(
    [string]$Version        = "",
    [string]$Publisher      = "CN=Peterandree",
    [string]$CertThumbprint = "",
    [string]$OutDir         = "",
    [switch]$SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$RepoRoot    = Resolve-Path (Join-Path $PSScriptRoot "..")
$CsprojPath  = Join-Path $RepoRoot "BTChargeTrayWatcher.csproj"
$IcoPath     = Join-Path $RepoRoot "app.ico"
$ManifestSrc = Join-Path $RepoRoot "msix\AppxManifest.xml"
$PublishDir  = Join-Path $RepoRoot "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"
$StageDir    = Join-Path $RepoRoot "bin\Release\msix-stage"
$OutDir      = if ($OutDir) { $OutDir } else { Join-Path $RepoRoot "publish\msix" }

# Windows SDK — prefer highest available version
$SdkBinRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
$SdkVersion = Get-ChildItem $SdkBinRoot -Directory |
              Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
              Sort-Object { [Version]$_.Name } -Descending |
              Select-Object -First 1 -ExpandProperty Name
$SdkDir     = Join-Path $SdkBinRoot "$SdkVersion\x64"
$MakeAppx   = Join-Path $SdkDir "makeappx.exe"
$SignTool    = Join-Path $SdkDir "signtool.exe"

foreach ($tool in $MakeAppx, $SignTool) {
    if (-not (Test-Path $tool)) {
        throw "Required tool not found: $tool. Install the Windows 10 SDK."
    }
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

function Export-IcoToPng([string]$src, [string]$dest, [int]$size) {
    Add-Type -AssemblyName System.Drawing
    $icon   = New-Object System.Drawing.Icon($src, $size, $size)
    $bmp    = $icon.ToBitmap()
    $canvas = New-Object System.Drawing.Bitmap($size, $size)
    $g      = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($bmp, 0, 0, $size, $size)
    $g.Dispose()
    $null = New-Item -ItemType Directory -Path (Split-Path $dest) -Force
    $canvas.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $canvas.Dispose(); $icon.Dispose()
    Write-Host "  Asset: $(Split-Path $dest -Leaf) ($size x $size)"
}

function Export-WideIcoToPng([string]$src, [string]$dest, [int]$w, [int]$h) {
    Add-Type -AssemblyName System.Drawing
    $icon   = New-Object System.Drawing.Icon($src, $h, $h)
    $bmp    = $icon.ToBitmap()
    $canvas = New-Object System.Drawing.Bitmap($w, $h)
    $g      = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    # Centre the icon in the wide canvas
    $x = [int](($w - $h) / 2)
    $g.DrawImage($bmp, $x, 0, $h, $h)
    $g.Dispose()
    $null = New-Item -ItemType Directory -Path (Split-Path $dest) -Force
    $canvas.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $canvas.Dispose(); $icon.Dispose()
    Write-Host "  Asset: $(Split-Path $dest -Leaf) ($w x $h)"
}

# ---------------------------------------------------------------------------
# Step 1: Resolve version from csproj if not provided
# ---------------------------------------------------------------------------
if (-not $Version) {
    $csprojXml    = [xml](Get-Content $CsprojPath -Raw)
    $versionNode  = $csprojXml.Project.PropertyGroup |
                    ForEach-Object { $_.Version } |
                    Where-Object   { $_ } |
                    Select-Object  -First 1
    $Version = if ($versionNode) { "$versionNode.0" } else { "3.0.0.0" }
}
# Ensure 4-part version
if (($Version -split '\.').Count -eq 3) { $Version = "$Version.0" }
Write-Host "Version: $Version" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Step 2: Publish
# ---------------------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Step "Publishing app (self-contained, compressed, single-file)"
    & dotnet publish $CsprojPath -c Release -r win-x64
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}

$ExePath = Join-Path $PublishDir "BTChargeTrayWatcher.exe"
if (-not (Test-Path $ExePath)) {
    throw "Published EXE not found at: $ExePath"
}
Write-Host "  EXE: $([math]::Round((Get-Item $ExePath).Length/1MB,2)) MB"

# ---------------------------------------------------------------------------
# Step 3: Generate PNG assets from app.ico
# ---------------------------------------------------------------------------
Write-Step "Generating MSIX PNG assets"
$AssetsDir = Join-Path $StageDir "Assets"
Export-IcoToPng    $IcoPath (Join-Path $AssetsDir "Square44x44Logo.png")    44
Export-IcoToPng    $IcoPath (Join-Path $AssetsDir "Square150x150Logo.png") 150
Export-WideIcoToPng $IcoPath (Join-Path $AssetsDir "Wide310x150Logo.png")  310 150
Export-IcoToPng    $IcoPath (Join-Path $AssetsDir "StoreLogo.png")          50

# ---------------------------------------------------------------------------
# Step 4: Stage the MSIX layout
# ---------------------------------------------------------------------------
Write-Step "Staging MSIX layout"
if (Test-Path $StageDir) { Remove-Item $StageDir -Recurse -Force }
$null = New-Item -ItemType Directory -Path $StageDir -Force
$null = New-Item -ItemType Directory -Path $AssetsDir -Force

# Copy EXE
Copy-Item $ExePath $StageDir

# Patch and copy AppxManifest.xml
$manifestXml = Get-Content $ManifestSrc -Raw
# Use -creplace (case-sensitive) to avoid matching version="1.0" in the XML declaration
$manifestXml = $manifestXml -creplace 'Publisher="[^"]*"',  "Publisher=""$Publisher"""
$manifestXml = $manifestXml -creplace 'Version="[\d\.]+"',  "Version=""$Version"""
# Write UTF-8 without BOM — makeappx rejects files with a BOM
[System.IO.File]::WriteAllText(
    (Join-Path $StageDir "AppxManifest.xml"),
    $manifestXml,
    [System.Text.UTF8Encoding]::new($false)
)

# Re-generate assets now that StageDir exists (Export-* already wrote them above)

# ---------------------------------------------------------------------------
# Step 5: Get or create signing certificate
# ---------------------------------------------------------------------------
Write-Step "Resolving signing certificate"
if ($CertThumbprint) {
    $cert = Get-Item "Cert:\CurrentUser\My\$CertThumbprint" -ErrorAction Stop
    Write-Host "  Using existing cert: $($cert.Subject) [$CertThumbprint]"
} else {
    $certName = "BTChargeTrayWatcher Dev"
    $existing = Get-ChildItem "Cert:\CurrentUser\My" |
                Where-Object { $_.Subject -eq $Publisher -and $_.FriendlyName -eq $certName } |
                Sort-Object NotAfter -Descending |
                Select-Object -First 1

    if ($existing -and $existing.NotAfter -gt (Get-Date)) {
        $cert             = $existing
        $CertThumbprint   = $cert.Thumbprint
        Write-Host "  Reusing dev cert: $($cert.Subject) [expires $($cert.NotAfter.ToString('yyyy-MM-dd'))]"
    } else {
        Write-Host "  Creating new self-signed dev cert..."
        $cert = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $Publisher `
            -KeyUsage DigitalSignature `
            -FriendlyName $certName `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -NotAfter (Get-Date).AddYears(2) `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        $CertThumbprint = $cert.Thumbprint
        Write-Host "  Created: $($cert.Subject) [$CertThumbprint]"
        Write-Host "  NOTE: This self-signed cert is for LOCAL TESTING ONLY." -ForegroundColor Yellow
        Write-Host "        For WinGet distribution, replace with a trusted EV cert." -ForegroundColor Yellow
    }
}

# ---------------------------------------------------------------------------
# Step 6: Pack with makeappx
# ---------------------------------------------------------------------------
Write-Step "Packing MSIX"
$null = New-Item -ItemType Directory -Path $OutDir -Force
$MsixName = "BTChargeTrayWatcher_$($Version)_x64.msix"
$MsixPath = Join-Path $OutDir $MsixName

if (Test-Path $MsixPath) { Remove-Item $MsixPath -Force }

& $MakeAppx pack /d $StageDir /p $MsixPath /nv /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed." }
Write-Host "  Packed: $MsixPath ($([math]::Round((Get-Item $MsixPath).Length/1MB,2)) MB)"

# ---------------------------------------------------------------------------
# Step 7: Sign with signtool
# ---------------------------------------------------------------------------
Write-Step "Signing MSIX"
& $SignTool sign /fd SHA256 /sha1 $CertThumbprint $MsixPath
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed." }
Write-Host "  Signed: $MsixPath"

# ---------------------------------------------------------------------------
# Step 8: Print SHA256 for WinGet manifest
# ---------------------------------------------------------------------------
Write-Step "Output"
$hash = (Get-FileHash -Algorithm SHA256 $MsixPath).Hash
Write-Host ""
Write-Host "  MSIX:   $MsixPath"
Write-Host "  Size:   $([math]::Round((Get-Item $MsixPath).Length/1MB,2)) MB"
Write-Host "  SHA256: $hash"
Write-Host ""
Write-Host "  InstallerUrl:   https://github.com/peterandree/BTChargeTrayWatcher/releases/download/v$($Version.TrimEnd('.0'))/BTChargeTrayWatcher_$($Version)_x64.msix"
Write-Host "  InstallerSha256: $hash"
Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
