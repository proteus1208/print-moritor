$ErrorActionPreference = "Stop"

$Output = Join-Path $PSScriptRoot "publish"
New-Item -ItemType Directory -Force -Path $Output | Out-Null

function Stop-PrintArchiveLocks {
    Write-Host "Releasing file locks (service / agent / UI)..." -ForegroundColor DarkCyan

    # Running service locks publish\PrintArchive.Service.exe if that path was installed.
    $svc = Get-Service -Name "PrintArchive" -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        try {
            Stop-Service -Name "PrintArchive" -Force -ErrorAction Stop
            Start-Sleep -Seconds 2
        } catch {
            sc.exe stop PrintArchive | Out-Null
            Start-Sleep -Seconds 2
        }
    }

    foreach ($name in @("PrintArchive.Service", "PrintArchive.Agent", "PrintArchive.UI")) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
            try { $_.Kill() } catch { }
        }
    }

    Start-Sleep -Milliseconds 500

    # If an old single-file bundle is still locked, rename it so publish can write a new exe.
    Get-ChildItem -Path $Output -Filter "PrintArchive.*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            [IO.File]::Open($_.FullName, "Open", "ReadWrite", "None").Dispose()
        } catch {
            $bak = "$($_.FullName).old"
            Remove-Item $bak -Force -ErrorAction SilentlyContinue
            Rename-Item $_.FullName $bak -Force
            Write-Host "  Moved locked file aside: $($_.Name) -> $($_.Name).old" -ForegroundColor Yellow
        }
    }
}

Write-Host "Building PrintArchive..." -ForegroundColor Cyan
Stop-PrintArchiveLocks

$Projects = @(
    "PrintArchive.Service",
    "PrintArchive.Agent",
    "PrintArchive.UI"
)

foreach ($Project in $Projects) {
    Write-Host "Publishing $Project..."

    # Publish to a per-project staging folder first, then copy into publish\.
    # Avoids GenerateBundle failing when an installed/running exe still locks publish\.
    $stage = Join-Path $PSScriptRoot "obj\publish-stage\$Project"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    dotnet publish `
        "$PSScriptRoot\src\$Project\$Project.csproj" `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $stage

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish $Project"
    }

    Copy-Item -Path (Join-Path $stage "*") -Destination $Output -Force
}

# Restart service only if it was already installed.
$svc = Get-Service -Name "PrintArchive" -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Restarting PrintArchive service..." -ForegroundColor DarkCyan
    try { Start-Service -Name "PrintArchive" } catch { sc.exe start PrintArchive | Out-Null }
}

Write-Host "Build completed: $Output" -ForegroundColor Green
Get-ChildItem $Output -File | Select-Object Name, Length | Format-Table -AutoSize
