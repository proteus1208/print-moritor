# PrintArchive — Windows C# starter project

This is a Windows-only, self-contained-deployment starter solution for a local print archive.

## What is implemented
- Windows Worker Service project intended to run as an automatic Windows service.
- PrintService Operational event listener for completed print jobs (Event ID 307).
- Parses the Event ID 307 XML fields and writes encrypted print-job metadata to the archive index.
- AES-256-GCM encrypted archive-file helper.
- Password-derived key helper (PBKDF2-SHA256) and Windows DPAPI service-key protection helper.
- Hidden login Agent with Ctrl+Alt+F12 global hotkey.
- WPF unlock/history/settings UI shell.
- Storage quota cleanup model and metadata models.
- Installer packaging notes.

## Important status / limitations
This is a **starter implementation, not a production-ready recorder**. Windows print events do not reliably expose the original source-file path, application executable, complete settings, or printable content. The source resolver is deliberately conservative and records the best available document name; it does not pretend that a spool file is the original DOCX/PSD/PDF.

The project does not yet implement robust per-application document-path correlation, universal spool rendering to PDF, or guaranteed screenshot capture. Those require additional per-application integrations and interactive-session testing. Screenshots are intentionally not silently captured by this starter. Add a clear user-visible capture policy and permissions before enabling them.

The service now writes metadata-only records to an encrypted JSON index at `%ProgramData%\PrintArchive\jobs.index.enc`. The UI currently provides an unlock shell and archive listing foundation; it is not yet connected to that index. The service-key helper uses DPAPI LocalMachine so the service can encrypt while the UI is locked; this protects against casual file inspection but **does not protect the archive from a local administrator or malware running as SYSTEM**. A password-derived wrapping key is provided as a building block, but production key lifecycle and recovery need a security review.

Temporary decrypted-file opening and reliable cleanup-on-close are not enabled in this starter. Do not treat file deletion as guaranteed secure erasure, especially on SSDs.

## Requirements
- Windows 10/11 x64.
- Visual Studio 2026 or .NET 10 SDK.
- Administrator rights to install the Windows service.
- WPF desktop support.

## Build
From this directory on Windows:

```powershell
dotnet restore .\PrintArchive.sln
dotnet build .\PrintArchive.sln -c Release
```

Publish each executable self-contained:

```powershell
dotnet publish .\src\PrintArchive.Service\PrintArchive.Service.csproj -c Release -r win-x64 --self-contained true
dotnet publish .\src\PrintArchive.Agent\PrintArchive.Agent.csproj -c Release -r win-x64 --self-contained true
dotnet publish .\src\PrintArchive.UI\PrintArchive.UI.csproj -c Release -r win-x64 --self-contained true
```

The resulting application bundles the .NET runtime. End users do not need to install .NET separately.

## Run during development
Run the service elevated:

```powershell
dotnet run --project .\src\PrintArchive.Service
```

Run the Agent in the interactive logged-in session:

```powershell
dotnet run --project .\src\PrintArchive.Agent
```

Press Ctrl+Alt+F12 to open the UI shell.

## Install service (after publishing)
Run an elevated PowerShell:

```powershell
sc.exe create PrintArchive binPath= "`"$PWD\publish\PrintArchive.Service.exe`"" start= auto
sc.exe description PrintArchive "Local print archive background service"
sc.exe start PrintArchive
```

Use the supplied installer project/recipe for a real deployment. The Agent should be registered as a per-user logon task, not run in Session 0.

## Archive location
`%ProgramData%\PrintArchive\Archive`

Restrict ACLs to SYSTEM and Administrators. Configure retention and backups carefully.
