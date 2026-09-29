; Jane's installer, compiled by build/make-setup.ps1 with Inno Setup 6.
;
; What this installs is the PUBLIC build: unsigned, and published WITHOUT the uiAccess manifest.
; Windows refuses to start a uiAccess binary that is not signed by a certificate the machine
; trusts, so an unsigned installer carrying one would install an application that never opens.
; The price is that Jane installed this way cannot type into windows running as administrator.
;
; Because there is no uiAccess there is no need for Program Files, and so no need for a UAC
; prompt: the default is a per-user install. Pass /ALLUSERS to install machine-wide.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish-setup"
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts\setup"
#endif

#define AppName "Jane"
#define AppExe "Jane.exe"
#define AppUrl "https://github.com/Divici/Jane1.0"

; SmokeTest builds install under a different identity and leave a running Jane alone, so the
; installer can be exercised on a developer's machine without touching the Jane they are using.
#ifdef SmokeTest
  #define AppId "{{5B0E7A0E-6C1D-4E0B-9C57-2F3A1D6B8E11}"
#else
  #define AppId "{{E227F60C-1789-4E3D-A75B-466B737AA9C4}"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=David Aihe
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} setup

DefaultDirName={autopf}\{#AppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=no

PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

; Not Jane's own single-instance name: see InstallerTests.
SetupMutex=jane-dictation-setup

; Jane is closed by PrepareToInstall below. The Restart Manager would ask the user to do it.
CloseApplications=no
RestartApplications=no

OutputDir={#OutputDir}
OutputBaseFilename=JaneSetup
SetupIconFile=..\..\src\Jane.App\jane.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start Jane when I sign in to Windows"; GroupDescription: "Startup:"; Check: not IsAdminInstallMode
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; The same key, name and quoting the tray's "Start with Windows" toggle uses, so that toggle
; and this task are one setting rather than two.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Jane"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start Jane now"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
procedure CloseJane;
var
  ResultCode: Integer;
begin
#ifndef SmokeTest
  { Jane keeps nothing in memory that is not already on disk: settings and history are written
    as they happen. Ending the process loses no work, and it is the only way to replace an
    executable that starts with Windows and is therefore always open. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
#endif
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseJane;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  CloseJane;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  DataDir := ExpandConstant('{localappdata}\Jane');
  if not DirExists(DataDir) then
    Exit;

  { Asked, and the default is to keep it. A silent uninstall gets the default. }
  if SuppressibleMsgBox(
       'Jane has been removed.' + #13#10 + #13#10 +
       'Do you also want to delete your settings, dictionary, transcript history and the ' +
       'downloaded speech and language models?' + #13#10 + #13#10 +
       DataDir,
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
  begin
    DelTree(DataDir, True, True, True);
  end;
end;
