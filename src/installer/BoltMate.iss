; BoltMate.iss
; Inno Setup 6.x script — produces BoltMate-Setup.exe.
;
; Invoked from MSBuild via the BuildInstaller target in
; Directory.Build.targets when `dotnet publish -p:BuildInstaller=true`
; is run with RID win-x64. Inputs are passed via /D defines:
;
;   /DMyAppVersion="1.2.3"   — bundle version (from Nerdbank.GitVersioning)
;   /DSourceDir="C:\..."     — publish output dir (rsync'd to the Win VM)
;   /DOutputDir="C:\..."     — where the installer .exe should land
;
; AppId is a fixed GUID — required so subsequent upgrades replace the
; previous install instead of stacking side-by-side entries in
; Add/Remove Programs.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "."
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define MyAppName       "BoltMate"
#define MyAppPublisher  "Jared Ballen"
#define MyAppURL        "https://boltmate.app"
#define MyAppExeName    "BoltMate.exe"
#define MyAumid         "BoltMate"

[Setup]
AppId={{9C2C6E0F-8D45-4F4D-9A75-2C9E5A1E3B7A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=BoltMate-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
; SetupIconFile is optional — we ship .ico in the published dir already,
; but the Setup wizard icon stays generic for now. Hook a real one in
; alongside the EV/OV signing cert work.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Pull the whole published tree. Recursesubdirs picks up any runtimes/
; subfolders the self-contained publish stages (libhidapi, etc).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu shortcut tagged with AppUserModelID — THE reason this
; installer exists. Without it, BoltMate doesn't appear in Settings →
; System → Notifications → Apps because the Settings UI iterates
; registered AUMIDs (via shell shortcuts), not raw HKCU registry
; entries. The Settings panel reads + writes the per-app Enabled
; DWORD that WinNotifications.cs already owns, so the in-app toggle
; and the OS toggle stay in sync once this entry exists.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAumid}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAumid}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Clean the user-data dirs the app creates so re-install starts fresh.
; LOCALAPPDATA holds logs; APPDATA holds settings + the lock file. We
; leave these for the per-user uninstall path so an unattended uninstall
; for a different account doesn't try to scrub their data.
Type: filesandordirs; Name: "{userappdata}\BoltMate"
Type: filesandordirs; Name: "{userappdata}\..\Local\BoltMate"

[UninstallRun]
; Wipe the per-AUMID notification settings entry so a clean reinstall
; presents the welcome primer again. Inno Setup runs this BEFORE
; deleting the install dir, so reg.exe is still on PATH.
Filename: "reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{#MyAumid}"" /f"; Flags: runhidden; RunOnceId: "DelAumidSettings"
