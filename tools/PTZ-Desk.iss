#ifndef ReleaseDir
  #error ReleaseDir is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif
[Setup]
AppId=StokeleyDev.PTZDesk
AppName=PTZ Desk
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\PTZ Desk
DefaultGroupName=PTZ Desk
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\AGL.PTZ.Controller.Instance
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\AGL.PTZ.Controller.exe
SetupIconFile=..\app-icon.ico
OutputDir={#ReleaseDir}
OutputBaseFilename=PTZ-Desk-Setup-x64
Compression=lzma2
SolidCompression=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#ReleaseDir}\Portable\AGL.PTZ.Controller.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#ReleaseDir}\Dependencies\*"; DestDir: "{app}\Dependencies"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\PTZ Desk"; Filename: "{app}\AGL.PTZ.Controller.exe"

[Run]
Filename: "{app}\AGL.PTZ.Controller.exe"; Description: "PTZ Desk"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AGL PTZ Controller', Command) then
    if CompareText(Command, '"' + ExpandConstant('{app}\AGL.PTZ.Controller.exe') + '" --panel') = 0 then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'AGL PTZ Controller');
  if not UninstallSilent and DirExists(ExpandConstant('{localappdata}\AGL PTZ Controller')) then
    if MsgBox('Удалить также данные PTZ Desk для этого пользователя?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\AGL PTZ Controller'), True, True, True);
end;
