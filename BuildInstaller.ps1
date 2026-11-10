# Builds self-contained binaries, then packs a one-click Setup.exe with Inno Setup.
$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Publish = Join-Path $Root "publish"
$Dist = Join-Path $Root "dist"
$Iss = Join-Path $Root "installer\PrintArchive.iss"

Write-Host "=== 1/2  Publish apps ===" -ForegroundColor Cyan
& (Join-Path $Root "Build.ps1")
if ($LASTEXITCODE -ne 0 -and -not $?) { throw "Build.ps1 failed." }

$needed = @(
    "PrintArchive.Service.exe",
    "PrintArchive.Agent.exe",
    "PrintArchive.UI.exe"
)
foreach ($n in $needed) {
    if (-not (Test-Path (Join-Path $Publish $n))) {
        throw "Missing publish\$n - build did not produce required executables."
    }
}

Write-Host "=== 2/2  Compile one-click installer ===" -ForegroundColor Cyan

function Find-ISCC {
    $candidates = @(
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }

    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

$iscc = Find-ISCC
if (-not $iscc) {
    Write-Host ""
    Write-Host "Inno Setup 6 is required to build PrintArchive-Setup.exe" -ForegroundColor Yellow
    Write-Host "Download/install: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "After installing Inno Setup, run this script again:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\BuildInstaller.ps1"
    Write-Host ""
    Write-Host "Until then you can still install with:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\installer\Setup.ps1"
    exit 1
}

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
& $iscc $Iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setup = Join-Path $Dist "PrintArchive-Setup.exe"
if (-not (Test-Path $setup)) { throw "Setup exe was not created." }

Write-Host ""
Write-Host "One-click installer ready:" -ForegroundColor Green
Write-Host "  $setup"
Write-Host ""
Write-Host "Give users that single EXE. They double-click it (UAC yes) to install."
Get-Item $setup | Select-Object FullName, Length, LastWriteTime
