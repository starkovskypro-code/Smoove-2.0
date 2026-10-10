#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #error SourceDir must point to a self-contained publish directory
#endif
#ifndef InstallerOutput
  #define InstallerOutput "..\out\installer"
#endif

[Setup]
AppId={{D73C1A2C-AB88-4A0F-9D83-88B7B7F62C0E}
AppName=Smoove
AppVersion={#AppVersion}
AppPublisher=Smoove
AppPublisherURL=https://github.com/starkovskypro-code/Smoove-2.0
AppSupportURL=https://github.com/starkovskypro-code/Smoove-2.0/issues
DefaultDirName={localappdata}\Programs\Smoove
DefaultGroupName=Smoove
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#InstallerOutput}
OutputBaseFilename=Smoove-{#AppVersion}-Setup-win-x64
SetupIconFile=..\assets\branding\Smoove.ico
UninstallDisplayIcon={app}\Smoove.Settings.exe
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
AppMutex=Local\Smoove.Probe.Prototype
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,*.log,START-HERE.txt,Smoove.Probe.exe"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Smoove"; Filename: "{app}\Smoove.Settings.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Smoove"; Filename: "{app}\Smoove.Settings.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Smoove.Settings.exe"; Description: "{cm:LaunchProgram,Smoove}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function OwnsStartup(Command: String): Boolean;
begin
  Result := CompareText(Command, '"' + ExpandConstant('{app}\Smoove.Settings.exe') + '" --startup') = 0;
end;

function IsSmooveStartup(Command: String): Boolean;
var Executable, Parameters: String;
    QuotePosition, ExePosition: Integer;
begin
  Command := Trim(Command);
  if (Length(Command) > 0) and (Command[1] = '"') then
  begin
    Delete(Command, 1, 1);
    QuotePosition := Pos('"', Command);
    if QuotePosition = 0 then
    begin Result := False; Exit; end;
    Executable := Copy(Command, 1, QuotePosition - 1);
    Parameters := Trim(Copy(Command, QuotePosition + 1, MaxInt));
  end
  else
  begin
    ExePosition := Pos('.exe', Lowercase(Command));
    if ExePosition = 0 then
    begin Result := False; Exit; end;
    Executable := Copy(Command, 1, ExePosition + 3);
    Parameters := Trim(Copy(Command, ExePosition + 4, MaxInt));
  end;
  Result := (CompareText(ExtractFileName(Executable), 'Smoove.Settings.exe') = 0) and
    (CompareText(Parameters, '--startup') = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Command: String;
begin
  if (CurStep = ssPostInstall) and
     RegQueryStringValue(HKCU, RunKey, 'Smoove', Command) and IsSmooveStartup(Command) then
    RegWriteStringValue(HKCU, RunKey, 'Smoove', '"' + ExpandConstant('{app}\Smoove.Settings.exe') + '" --startup');
end;

function InitializeUninstall(): Boolean;
begin
  Result := not CheckForMutexes('Local\Smoove.Probe.Prototype');
  if not Result then
    MsgBox('Smoove is running. Please choose Exit in its tray menu before uninstalling.', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Command: String;
begin
  if CurUninstallStep = usUninstall then
    if RegQueryStringValue(HKCU, RunKey, 'Smoove', Command) and OwnsStartup(Command) then
      RegDeleteValue(HKCU, RunKey, 'Smoove');
  { User settings in LOCALAPPDATA\Smoove are intentionally preserved. }
end;
