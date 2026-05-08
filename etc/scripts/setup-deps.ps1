<#
.SYNOPSIS
  Downloads LLVM + Clang static libraries from vovkos/llvm-package-windows.

.DESCRIPTION
  Called by ClangXref.vcxproj PreBuildEvent. Idempotent - skips if libs exist.
  Downloads Release/amd64/libcmt (static CRT) packages matching our build config.
  Auto-downloads 7zr.exe (portable, ~600KB) if 7z is not on PATH.

.PARAMETER DepsDir
  Target directory for extracted packages. Defaults to ./deps relative to this script.
#>
[CmdletBinding()]
param(
    [string]$DepsDir
)

$ErrorActionPreference = 'Stop'
$Version = '21.1.1'
$BaseUrl = "https://github.com/vovkos/llvm-package-windows/releases/download"

if (-not $DepsDir) {
    $DepsDir = Join-Path $PSScriptRoot 'deps'
}

$LlvmDir = Join-Path $DepsDir "llvm-$Version"
$ClangDir = Join-Path $DepsDir "clang-$Version"
$SentinelLib = Join-Path (Join-Path $LlvmDir 'lib') 'LLVMCore.lib'
$SentinelClang = Join-Path (Join-Path $ClangDir 'lib') 'clangIndex.lib'

# Idempotent: skip if both sentinel files exist
if ((Test-Path $SentinelLib) -and (Test-Path $SentinelClang)) {
    Write-Host "[setup-deps] LLVM/Clang $Version already present - skipping download."
    exit 0
}

Write-Host "[setup-deps] Downloading LLVM/Clang $Version static libs..."
New-Item -ItemType Directory -Force -Path $DepsDir | Out-Null

# --- Ensure we have a 7z extractor ---
function Get-7zPath {
    # Check common locations
    $candidates = @(
        (Get-Command '7z' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        (Get-Command '7z.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "C:\Program Files\7-Zip\7z.exe",
        "C:\Program Files (x86)\7-Zip\7z.exe"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    # Download portable 7zr.exe (handles .7z files, ~600KB, no install)
    $toolsDir = Join-Path $DepsDir 'tools'
    $sevenZr = Join-Path $toolsDir '7zr.exe'
    if (Test-Path $sevenZr) { return $sevenZr }

    Write-Host "[setup-deps] 7z not found on PATH - downloading portable 7zr.exe..."
    New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
    Invoke-WebRequest -Uri 'https://www.7-zip.org/a/7zr.exe' -OutFile $sevenZr -UseBasicParsing
    if (-not (Test-Path $sevenZr)) {
        throw "[setup-deps] Failed to download 7zr.exe"
    }
    return $sevenZr
}

$SevenZ = Get-7zPath
Write-Host "[setup-deps] Using 7z: $SevenZ"

# --- Download and extract packages ---
$Packages = @(
    @{
        Name = "llvm"
        File = "llvm-$Version-windows-amd64-msvc17-libcmt.7z"
        Tag  = "llvm-$Version"
    },
    @{
        Name = "clang"
        File = "clang-$Version-windows-amd64-msvc17-libcmt.7z"
        Tag  = "clang-$Version"
    }
)

foreach ($pkg in $Packages) {
    $url = "$BaseUrl/$($pkg.Tag)/$($pkg.File)"
    $archive = Join-Path $DepsDir $pkg.File
    $targetDir = Join-Path $DepsDir "$($pkg.Name)-$Version"

    if (Test-Path $targetDir) {
        Write-Host "[setup-deps] $($pkg.Name)-$Version already extracted."
        continue
    }

    if (-not (Test-Path $archive)) {
        Write-Host "[setup-deps] Downloading $($pkg.File) (~270 MB)..."
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
    }

    Write-Host "[setup-deps] Extracting $($pkg.File)..."
    $extractDir = Join-Path $DepsDir ($pkg.File -replace '\.7z$', '')
    & $SevenZ x $archive "-o$DepsDir" -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "[setup-deps] 7z extraction failed for $($pkg.File) (exit code $LASTEXITCODE)"
    }
    # Rename extracted dir to clean name if needed
    if ((Test-Path $extractDir) -and ($extractDir -ne $targetDir)) {
        # Retry: 7z or Windows Search may briefly hold directory handles after extraction
        for ($retry = 0; $retry -lt 5; $retry++) {
            try {
                Rename-Item $extractDir $targetDir -ErrorAction Stop
                break
            } catch {
                if ($retry -eq 4) { throw }
                Start-Sleep -Seconds 2
            }
        }
    }

    # Clean up archive
    Remove-Item $archive -Force -ErrorAction SilentlyContinue
}

# --- Verify ---
if (-not (Test-Path $SentinelLib)) {
    throw "[setup-deps] LLVMCore.lib not found at $SentinelLib - extraction may have failed."
}
if (-not (Test-Path $SentinelClang)) {
    throw "[setup-deps] clangIndex.lib not found at $SentinelClang - extraction may have failed."
}

Write-Host "[setup-deps] LLVM/Clang $Version ready at $DepsDir"
