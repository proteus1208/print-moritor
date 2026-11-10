# Run in the interactive user's session after publishing Agent and UI.
param([string]$AgentExe = "$PSScriptRoot\..\publish\PrintArchive.Agent.exe")
$ErrorActionPreference = "Stop"
if (-not (Test-Path $AgentExe)) { throw "Agent executable not found: $AgentExe" }
$action = New-ScheduledTaskAction -Execute $AgentExe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName "PrintArchive Agent" -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
Write-Host "Agent logon task registered."
