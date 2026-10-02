; Built by tools\publish.ps1, which passes the version, the architecture and the folder to pack.

#ifndef AppVersion
  #error Run tools\publish.ps1 instead of compiling this file directly.
#endif

[Setup]
; Never change the AppId: Windows uses it to recognise an upgrade of the same app.
AppId={{057940FE-CEF6-471A-89A3-5714BBCFA6A1}
AppName=Dashio
AppVersion={#AppVersion}
AppVerName=Dashio {#AppVersion}
AppPublisher=Dashio contributors
VersionInfoVersion={#AppVersion}.0
DefaultDirName={autopf}\Dashio
DefaultGroupName=Dashio
DisableProgramGroupPage=yes
DisableDirPage=auto
LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\src\Dashio.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Dashio.exe
UninstallDisplayName=Dashio
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=Dashio-Setup-{#AppVersion}-{#Arch}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
MinVersion=10.0.22621
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
; The helper runs with administrator rights, so the files must sit where only an
; administrator can replace them. A per-user install folder would not be safe.
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Dashio"; Filename: "{app}\Dashio.exe"
Name: "{autodesktop}\Dashio"; Filename: "{app}\Dashio.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser: the window must never run elevated, even when started by the installer.
Filename: "{app}\Dashio.exe"; Description: "{cm:LaunchProgram,Dashio}"; Flags: nowait postinstall skipifsilent runasoriginaluser
