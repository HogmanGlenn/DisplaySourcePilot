#define ProductName "DisplaySourcePilot"
#define ProductExe "DisplaySourcePilot.exe"

#ifndef ProductVersion
#error ProductVersion must be supplied by build/Build-Release.ps1
#endif

[Setup]
AppId={{B11E33BA-DCFC-48C2-B8B7-F76F52771B30}
AppName={#ProductName}
AppVersion={#ProductVersion}
AppVerName={#ProductName} {#ProductVersion}
DefaultDirName={localappdata}\Programs\DisplaySourcePilot
DefaultGroupName=DisplaySourcePilot
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible arm64
OutputDir=..\artifacts\installer
OutputBaseFilename=DisplaySourcePilot-Setup-{#ProductVersion}
SetupIconFile=..\src\DisplaySourcePilot.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#ProductExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763
VersionInfoVersion={#ProductVersion}.0
VersionInfoProductName={#ProductName}
VersionInfoDescription=Install {#ProductName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Add DisplaySourcePilot to the desktop"; GroupDescription: "Shortcuts:"; Flags: checkedonce

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: not IsArm64
Source: "..\artifacts\publish\win-arm64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: IsArm64

[Icons]
Name: "{group}\DisplaySourcePilot"; Filename: "{app}\{#ProductExe}"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{group}\Switch to HDMI"; Filename: "{app}\{#ProductExe}"; Parameters: "--switch hdmi"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{group}\Switch to DisplayPort"; Filename: "{app}\{#ProductExe}"; Parameters: "--switch displayport"; IconFilename: "{app}\Assets\AppIcon.ico"
Name: "{autodesktop}\DisplaySourcePilot"; Filename: "{app}\{#ProductExe}"; IconFilename: "{app}\Assets\AppIcon.ico"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{userstartup}\Samsung Display Input Worker.lnk"
Type: files; Name: "{app}\DisplaySwitch.exe"
Type: files; Name: "{autodesktop}\Display Switch.lnk"
Type: files; Name: "{autodesktop}\HDMI.lnk"
Type: files; Name: "{autodesktop}\DisplayPort.lnk"
Type: files; Name: "{group}\Display Switch.lnk"

[Run]
Filename: "{app}\{#ProductExe}"; Description: "Open DisplaySourcePilot"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#ProductExe}"; Parameters: "--cleanup-shortcuts"; Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "CleanDisplaySourcePilotShortcuts"
Filename: "{app}\{#ProductExe}"; Parameters: "--quit"; Flags: runhidden skipifdoesntexist; RunOnceId: "StopDisplaySourcePilot"

[Code]
function IsArm64: Boolean;
begin
  Result := ProcessorArchitecture = paArm64;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  CurrentExecutable: String;
  LegacyExecutable: String;
begin
  CurrentExecutable := ExpandConstant('{app}\DisplaySourcePilot.exe');
  if FileExists(CurrentExecutable) then
  begin
    Exec(CurrentExecutable, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(750);
  end;
  LegacyExecutable := ExpandConstant('{app}\DisplaySwitch.exe');
  if FileExists(LegacyExecutable) then
  begin
    Exec(LegacyExecutable, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(750);
  end;
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DisplaySourcePilot');
end;
