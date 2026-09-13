# publish-release.ps1
# Builds self-contained single-file win-x64 Release packages for MultiMon.Control and
# MultiMon.Stress, assembles a versioned kit folder under dist\, and zips it.
#
# License: EUPL-1.2
# NOTE: FFmpeg is NOT bundled. It is an external prerequisite required only for the
#       "Convert to HAP" feature. End users must install it separately.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish-release.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Resolve repo root (script lives in <root>\scripts\) ---
$RepoRoot = Split-Path -Parent $PSScriptRoot

# --- Read version from Directory.Build.props ---
$BuildPropsPath = Join-Path $RepoRoot "Directory.Build.props"
[xml]$BuildProps = Get-Content $BuildPropsPath
$Version = $BuildProps.Project.PropertyGroup.Version
if (-not $Version) {
    throw "Could not parse <Version> from $BuildPropsPath"
}
Write-Host "Version: $Version"

# --- Paths ---
$DistRoot       = Join-Path $RepoRoot "dist"
$KitName        = "MultiMon-$Version-win-x64"
$KitDir         = Join-Path $DistRoot $KitName
$KitAppDir      = Join-Path $KitDir "App"
$KitStressDir   = Join-Path $KitDir "Stress"
$ZipPath        = Join-Path $DistRoot "$KitName.zip"

$ControlProj    = Join-Path $RepoRoot "MultiMon.Control\MultiMon.Control.csproj"
$StressProj     = Join-Path $RepoRoot "MultiMon.Stress\MultiMon.Stress.csproj"

# Root docs to copy into the kit
$DocFiles = @(
    "README.md",
    "LICENSE",
    "CHANGELOG.md",
    "REQUIREMENTS.md",
    "THIRD-PARTY-NOTICES.md"
)

# --- Idempotent: clean the target kit dir and zip ---
Write-Host "`nCleaning previous dist target..."
if (Test-Path $KitDir)  { Remove-Item $KitDir  -Recurse -Force }
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }

New-Item -ItemType Directory -Path $KitAppDir    -Force | Out-Null
New-Item -ItemType Directory -Path $KitStressDir -Force | Out-Null

# --- Publish MultiMon.Control ---
Write-Host "`nPublishing MultiMon.Control..."
dotnet publish "$ControlProj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "$KitAppDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish MultiMon.Control failed (exit $LASTEXITCODE)" }

# --- Publish MultiMon.Stress ---
Write-Host "`nPublishing MultiMon.Stress..."
dotnet publish "$StressProj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "$KitStressDir"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish MultiMon.Stress failed (exit $LASTEXITCODE)" }

# --- Copy root docs ---
Write-Host "`nCopying root docs..."
foreach ($Doc in $DocFiles) {
    $Src = Join-Path $RepoRoot $Doc
    if (Test-Path $Src) {
        Copy-Item $Src -Destination $KitDir -Force
        Write-Host "  Copied $Doc"
    } else {
        Write-Warning "  Not found (skipped): $Doc"
    }
}

# --- Zip the kit ---
Write-Host "`nZipping kit to $ZipPath ..."
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($KitDir, $ZipPath)
if ($LASTEXITCODE -ne 0) { throw "Zip failed" }

# --- Report ---
$ZipSize = (Get-Item $ZipPath).Length
$ZipSizeMB = [math]::Round($ZipSize / 1MB, 1)

Write-Host ""
Write-Host "====================================================="
Write-Host "  Kit folder : $KitDir"
Write-Host "  Zip        : $ZipPath"
Write-Host "  Zip size   : $ZipSizeMB MB ($ZipSize bytes)"
Write-Host "====================================================="
