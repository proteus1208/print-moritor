; Inno Setup script -> produces dist\PrintArchive-Setup.exe (one-click installer)
; Build with: BuildInstaller.ps1

#define MyAppName "PrintArchive"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "PrintArchive"
#define MyAppExeName "PrintArchive.UI.exe"

[Setup]
AppId={{A8F3C2E1-9B74-4D2A-8F01-7C2E91B4D5A6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\PrintArchive
DefaultGroupName=PrintArchive
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=PrintArchive-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=
CloseApplications=force
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; Published self-contained binaries
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Helper scripts used at install/uninstall time
Source: "PostInstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "Uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\PrintArchive"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall PrintArchive"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PrintArchive"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Run]
; Configure service + agent + open UI for password
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\PostInstall.ps1"" -InstallDir ""{app}"""; \
  WorkingDir: "{app}"; \
  Flags: runhidden waituntilterminated; \
  StatusMsg: "Configuring PrintArchive service and agent..."
Filename: "{app}\{#MyAppExeName}"; Description: "Open PrintArchive now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Uninstall.ps1"" -InstallDir ""{app}"""; \
  RunOnceId: "PrintArchiveUninstallHooks"; \
  Flags: runhidden waituntilterminated
