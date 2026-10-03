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
AppPublisher=Wrench
AppPublisherURL=https://github.com/EverythingSuckz/Dashio
AppSupportURL=https://github.com/EverythingSuckz/Dashio/issues
AppUpdatesURL=https://github.com/EverythingSuckz/Dashio/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Wrench
VersionInfoProductName=Dashio
VersionInfoDescription=Dashio Setup
VersionInfoCopyright=MIT License, Wrench and Dashio contributors
DefaultDirName={autopf}\Dashio
DefaultGroupName=Dashio
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
DisableWelcomePage=no
SetupIconFile={#RepoRoot}\src\Dashio.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\Dashio.exe
UninstallDisplayName=Dashio
; Follows the light or dark setting of Windows.
WizardStyle=modern dynamic
; Drawn by tools\make-icon.ps1, one file for each display scale.
WizardImageFile={#RepoRoot}\installer\art\side-light-*.png
WizardSmallImageFile={#RepoRoot}\installer\art\small-*.png
WizardImageFileDynamicDark={#RepoRoot}\installer\art\side-dark-*.png
WizardSmallImageFileDynamicDark={#RepoRoot}\installer\art\small-*.png
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
; The uninstaller offers to remove the data of the person who runs it, which is in their profile.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Off unless asked for: Dashio is the tool that shows what starts by itself.
Name: "startup"; Description: "Start Dashio when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The licence is short and asks for no agreement, so it is installed beside the app instead of shown as a page.
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Dashio"; Filename: "{app}\Dashio.exe"
Name: "{autodesktop}\Dashio"; Filename: "{app}\Dashio.exe"; Tasks: desktopicon

[Registry]
; The same entry the switch in Dashio's Settings adds and removes. It also shows in Dashio's Startup page and in Task Manager.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Dashio"; ValueData: """{app}\Dashio.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; runasoriginaluser: the window must never run elevated, even when started by the installer.
Filename: "{app}\Dashio.exe"; Description: "{cm:LaunchProgram,Dashio}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// Dashio's own data (the change log, settings, kept drive scans) is not part of the install.
// It stays unless the person uninstalling says otherwise.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataFolder: String;
begin
  if CurUninstallStep <> usPostUninstall then
    exit;
  DataFolder := ExpandConstant('{localappdata}\Dashio');
  if DirExists(DataFolder) and not UninstallSilent then
    if MsgBox('Also remove Dashio''s own data: the history of changes, your settings and the kept drive scans?'
      + #13#10#13#10 + DataFolder, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(DataFolder, True, True, True);
end;
