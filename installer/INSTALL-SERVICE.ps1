# Run from an elevated PowerShell after publishing the service.
param(
  [string]$ServiceExe = "$PSScriptRoot\..\publish\PrintArchive.Service.exe"
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $ServiceExe)) { throw "Service executable not found: $ServiceExe" }
sc.exe stop PrintArchive 2>$null | Out-Null
sc.exe delete PrintArchive 2>$null | Out-Null
sc.exe create PrintArchive binPath= "`"$ServiceExe`"" start= auto
sc.exe description PrintArchive "PrintArchive background print event monitor"
sc.exe start PrintArchive
Write-Host "PrintArchive service installed. Enable PrintService/Operational logging in Event Viewer."
