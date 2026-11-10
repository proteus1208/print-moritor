#Requires -RunAsAdministrator
# Called by the one-click Setup.exe after files are copied to Program Files.
param(
    [string]$InstallDir = $PSScriptRoot,
    [switch]$SkipPasswordPrompt
)

$ErrorActionPreference = "Stop"
$Root = "C:\ProgramData\PrintArchive"

function Write-Step($msg) { Write-Host "-> $msg" -ForegroundColor Cyan }

Write-Step "Creating archive folders"
New-Item -ItemType Directory -Force -Path "$Root\Archive\Jobs", "$Root\Temp", "$Root\Logs", "$Root\Pending" | Out-Null

$serviceExe = Join-Path $InstallDir "PrintArchive.Service.exe"
$agentExe   = Join-Path $InstallDir "PrintArchive.Agent.exe"
$uiExe      = Join-Path $InstallDir "PrintArchive.UI.exe"

foreach ($p in @($serviceExe, $agentExe, $uiExe)) {
    if (-not (Test-Path $p)) { throw "Missing $p" }
}

Write-Step "Installing Windows service"
sc.exe stop PrintArchive 2>$null | Out-Null
Start-Sleep -Seconds 1
sc.exe delete PrintArchive 2>$null | Out-Null
sc.exe create PrintArchive binPath= "`"$serviceExe`"" start= auto DisplayName= "PrintArchive" | Out-Null
sc.exe description PrintArchive "Monitors completed print jobs and archives encrypted metadata/documents" | Out-Null
sc.exe failure PrintArchive reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe start PrintArchive | Out-Null

Write-Step "Registering Agent at logon"
Get-Process -Name "PrintArchive.Agent" -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
$action = New-ScheduledTaskAction -Execute $agentExe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName "PrintArchive Agent" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
Start-Process -FilePath $agentExe

Write-Step "Enabling PrintService Operational log"
try { wevtutil.exe sl "Microsoft-Windows-PrintService/Operational" /e:true | Out-Null } catch { }

if (-not $SkipPasswordPrompt) {
    Write-Step "Opening password / unlock UI"
    Start-Process -FilePath $uiExe
}

Write-Host ""
Write-Host "PrintArchive installed." -ForegroundColor Green
Write-Host "  Hotkey : Ctrl+Alt+F12"
Write-Host "  Data   : $Root"
Write-Host "  App    : $InstallDir"
