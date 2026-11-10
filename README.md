# PrintArchive

Windows print-job archive: monitors PrintService Event ID 307, stores structured history in SQLite, encrypts captured originals with AES-256-GCM, and unlocks a History UI with a password-wrapped master key.

## What works

- Windows service listening for completed print jobs (Event ID 307)
- XML event parsing with duplicate protection (`EventRecordId`)
- SQLite job database at `%ProgramData%\PrintArchive\jobs.db`
- Best-effort original-file capture + encrypted document storage
- Password enrollment / unlock / change (wraps the archive master key)
- History UI: search, date filter, printer filter, details, open, export, delete
- Storage quota (default 5 GB) with oldest-job cleanup
- Temp decrypted-file tracking and crash/reboot cleanup
- Self-contained `win-x64` publish (no separate .NET runtime)
- Full installer script: service, agent logon task, folders, ACLs, password prompt

## Limits

- Windows print events do **not** include the original document path. Capture succeeds only when the document title can be resolved to a real file.
- DPAPI LocalMachine lets the service encrypt while the UI is locked; a local administrator can still access the service key.
- Temp-file overwrite-before-delete is best-effort (not guaranteed secure erase on SSDs).
- Screenshots and universal rendered-PDF capture are not enabled.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK (build machine only)
- Administrator rights to install the service

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\Build.ps1
```

Output goes to `.\publish\` (self-contained single-file exes).

## Install

Elevated PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Setup.ps1
```

Uninstall (keeps `%ProgramData%\PrintArchive` data):

```powershell
powershell -ExecutionPolicy Bypass -File .\installer\Setup.ps1 -Uninstall
```

## Daily use

1. Service archives jobs automatically.
2. Press **Ctrl+Alt+F12** (or use the Agent tray icon) to open the UI.
3. Unlock with your password.
4. Double-click a row (or Open) to decrypt to a temp folder and launch the file.

## Archive layout

```
%ProgramData%\PrintArchive\
  jobs.db
  vault.bin
  service.key.dpapi
  settings.json
  Archive\Jobs\<id>\original*.enc
  Temp\
  Logs\service.log
```

## Development run

```powershell
dotnet run --project .\src\PrintArchive.Service
dotnet run --project .\src\PrintArchive.Agent
dotnet run --project .\src\PrintArchive.UI
```
