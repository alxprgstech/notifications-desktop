#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PublishDir
  #error PublishDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif

#define AppName "ALXPRGS Notify Desktop"
#define AppExe "ALXPRGS.Notify.Desktop.exe"
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"

[Setup]
AppId={{02AA4972-2C68-4135-879D-4F6257803F9D}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoCompany=ALXPRGS
AppPublisher=ALXPRGS
DefaultDirName={localappdata}\Programs\ALXPRGS Notify Desktop
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
SetupIconFile=..\Assets\Notify.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=ALXPRGS-Notify-Desktop-Setup-{#AppVersion}-win-x64

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function StopDesktop: Boolean;
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  Result := not FileExists(Exe);
  if not Result then
    Result := Exec(Exe, '--shutdown', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not StopDesktop then
    Result := 'Close ALXPRGS Notify Desktop from its notification area menu, then retry.';
end;

function InitializeUninstall: Boolean;
begin
  Result := StopDesktop;
  if not Result then
    SuppressibleMsgBox('Close ALXPRGS Notify Desktop from its notification area menu, then retry.', mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, '{#RunKey}', '{#AppName}', Command) then
      if CompareText(Command, '"' + ExpandConstant('{app}\{#AppExe}') + '" --background') = 0 then
        RegDeleteValue(HKCU, '{#RunKey}', '{#AppName}');
end;
