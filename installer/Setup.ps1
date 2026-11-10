#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Complete PrintArchive installer (service + agent + folders + optional password).
#>
param(
    [string]$PublishDir = "$PSScriptRoot\..\publish",
    [switch]$SkipPasswordPrompt,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$Root = "C:\ProgramData\PrintArchive"
$InstallDir = Join-Path ${env:ProgramFiles} "PrintArchive"

function Write-Step($msg) { Write-Host "→ $msg" -ForegroundColor Cyan }

if ($Uninstall) {
    Write-Step "Stopping and removing service"
    sc.exe stop PrintArchive 2>$null | Out-Null
    Start-Sleep -Seconds 1
    sc.exe delete PrintArchive 2>$null | Out-Null

    Write-Step "Removing Agent logon task"
    Unregister-ScheduledTask -TaskName "PrintArchive Agent" -Confirm:$false -ErrorAction SilentlyContinue

    Write-Step "Removing install directory"
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

    Write-Host "Uninstall complete. Archive data left at $Root (delete manually if desired)." -ForegroundColor Yellow
    return
}

$required = @(
    "PrintArchive.Service.exe",
    "PrintArchive.Agent.exe",
    "PrintArchive.UI.exe"
)
foreach ($name in $required) {
    $path = Join-Path $PublishDir $name
    if (-not (Test-Path $path)) { throw "Missing $path — run Build.ps1 first." }
}

Write-Step "Creating ProgramData archive directories"
New-Item -ItemType Directory -Force -Path "$Root\Archive\Jobs", "$Root\Temp", "$Root\Logs" | Out-Null

Write-Step "Configuring ACLs (SYSTEM + Administrators)"
$acl = Get-Acl $Root
$acl.SetAccessRuleProtection($true, $false)
$rules = @(
    New-Object System.Security.AccessControl.FileSystemAccessRule("SYSTEM","FullControl","ContainerInherit,ObjectInherit","None","Allow"),
    New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators","FullControl","ContainerInherit,ObjectInherit","None","Allow"),
    New-Object System.Security.AccessControl.FileSystemAccessRule("Users","ReadAndExecute","ContainerInherit,ObjectInherit","None","Allow")
)
foreach ($rule in $rules) { $acl.AddAccessRule($rule) }
Set-Acl -Path $Root -AclObject $acl

Write-Step "Copying binaries to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $InstallDir -Recurse -Force

$serviceExe = Join-Path $InstallDir "PrintArchive.Service.exe"
$agentExe   = Join-Path $InstallDir "PrintArchive.Agent.exe"

Write-Step "Installing Windows service (automatic start)"
sc.exe stop PrintArchive 2>$null | Out-Null
sc.exe delete PrintArchive 2>$null | Out-Null
sc.exe create PrintArchive binPath= "`"$serviceExe`"" start= auto DisplayName= "PrintArchive"
sc.exe description PrintArchive "Monitors completed print jobs and archives encrypted metadata/documents"
sc.exe failure PrintArchive reset= 86400 actions= restart/5000/restart/5000/restart/5000
sc.exe start PrintArchive

Write-Step "Registering Agent at user logon"
$action = New-ScheduledTaskAction -Execute $agentExe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName "PrintArchive Agent" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null

# Start agent now for current session
Start-Process -FilePath $agentExe

Write-Step "Enabling PrintService Operational log (best effort)"
try {
    wevtutil.exe sl "Microsoft-Windows-PrintService/Operational" /e:true | Out-Null
} catch {
    Write-Host "  Could not enable PrintService log automatically — enable it in Event Viewer." -ForegroundColor Yellow
}

if (-not $SkipPasswordPrompt) {
    Write-Step "Password enrollment"
    Write-Host "Launching UI for first-run password creation..."
    Start-Process -FilePath (Join-Path $InstallDir "PrintArchive.UI.exe")
    Write-Host "Create your archive password in the window that opens." -ForegroundColor Green
}

Write-Host ""
Write-Host "PrintArchive installed." -ForegroundColor Green
Write-Host "  Service : PrintArchive (automatic)"
Write-Host "  Agent   : Ctrl+Alt+F12  (also tray icon)"
Write-Host "  Data    : $Root"
Write-Host "  App     : $InstallDir"
Write-Host ""
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File .\installer\Setup.ps1 -Uninstall"
