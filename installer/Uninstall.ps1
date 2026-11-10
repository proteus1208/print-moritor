#Requires -RunAsAdministrator
param([string]$InstallDir = $PSScriptRoot)

$ErrorActionPreference = "SilentlyContinue"

sc.exe stop PrintArchive | Out-Null
Start-Sleep -Seconds 1
sc.exe delete PrintArchive | Out-Null

Unregister-ScheduledTask -TaskName "PrintArchive Agent" -Confirm:$false

Get-Process -Name "PrintArchive.Agent","PrintArchive.UI","PrintArchive.Service" -ErrorAction SilentlyContinue |
    ForEach-Object { try { $_.Kill() } catch { } }

Write-Host "Service and Agent removed. Program files are removed by the uninstaller."
Write-Host "Archive data kept at C:\ProgramData\PrintArchive (delete manually if desired)."
