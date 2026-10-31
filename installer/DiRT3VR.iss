#ifndef Stage
  #error Stage must point to the staged DiRT3VR package folder
#endif
#ifndef PackageVersion
  #error PackageVersion must be supplied by tools/package.ps1
#endif
[Setup]
AppId={{F4DF86CA-B327-4582-9E6A-FF3043337C0E}
AppName=DiRT3VR
AppVersion={#PackageVersion}
AppPublisher=Bohloney
DefaultDirName={code:DefaultGameDir}
AppendDefaultDirName=no
; Keep the target choice available when upgrading an existing install.
DisableDirPage=no
DisableProgramGroupPage=yes
DirExistsWarning=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallFilesDir={app}\DiRT3VR\uninstall
UninstallDisplayIcon={app}\DiRT3VR\DiRT3VR.exe
OutputDir={#Stage}\..
OutputBaseFilename=DiRT3VR-{#PackageVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
AppMutex=Global\DiRT3VR.Session
SetupLogging=yes

[Messages]
WizardSelectDir=Browse DiRT 3 Installation
SelectDirDesc=Keep the detected folder or choose a different DiRT 3 Complete Edition installation.
SelectDirBrowseLabel=Click Next to use this folder, or Browse to choose another installation.
BrowseDialogTitle=Browse DiRT 3 Installation
BrowseDialogLabel=Select the DiRT 3 Complete Edition folder containing dirt3_game.exe.

[Files]
; The launcher and its payload stay together in a DiRT3VR subfolder. Game files
; are changed only by the launcher during a session and restored afterwards.
Source: "{#Stage}\DiRT3VR.exe"; DestDir: "{app}\DiRT3VR"; Flags: ignoreversion
Source: "{#Stage}\*"; DestDir: "{app}\DiRT3VR"; Excludes: "\DiRT3VR.exe"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{autoprograms}\DiRT3VR"; Filename: "{app}\DiRT3VR\DiRT3VR.exe"; WorkingDir: "{app}\DiRT3VR"
Name: "{autodesktop}\DiRT3VR"; Filename: "{app}\DiRT3VR\DiRT3VR.exe"; WorkingDir: "{app}\DiRT3VR"; Tasks: desktopicon

[Run]
Filename: "{app}\DiRT3VR\DiRT3VR.exe"; Description: "Open DiRT3VR"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
var
  SuggestedGame: String;

function DefaultGameDir(Param: String): String;
begin
  if SuggestedGame <> '' then Result := SuggestedGame
  else Result := ExpandConstant('{commonpf32}\Steam\steamapps\common\DiRT 3 Complete Edition');
end;

procedure InitializeWizard();
var
  ResultCode, I: Integer;
  ExplicitDirectory: Boolean;
  Discovered: AnsiString;
begin
  ExplicitDirectory := False;
  for I := 1 to ParamCount do
    if CompareText(Copy(ParamStr(I), 1, 5), '/DIR=') = 0 then ExplicitDirectory := True;
  WizardForm.SelectDirLabel.Caption := 'Select the existing DiRT 3 Complete Edition folder containing dirt3_game.exe. No game files or saves are included.';
  ExtractTemporaryFile('DiRT3VR.exe');
  if Exec(ExpandConstant('{tmp}\DiRT3VR.exe'), '--discover "' + ExpandConstant('{tmp}\game-path.txt') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
    if LoadStringFromFile(ExpandConstant('{tmp}\game-path.txt'), Discovered) then
    begin
      SuggestedGame := UTF8Decode(Discovered);
      if not ExplicitDirectory and (SuggestedGame <> '') and not FileExists(WizardDirValue() + '\dirt3_game.exe') then
        WizardForm.DirEdit.Text := SuggestedGame;
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ErrorText: AnsiString;
begin
  Result := '';
  if CheckForMutexes('Global\DiRT3VR.Session') then
  begin
    Result := 'Close DiRT3VR before installing.';
    exit;
  end;
  // Validates the executable, refuses while DiRT 3 runs or file recovery is pending.
  if not Exec(ExpandConstant('{tmp}\DiRT3VR.exe'), '--check-install --game "' + WizardDirValue() + '" --error-log "' + ExpandConstant('{tmp}\install-error.txt') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
  begin
    Result := 'Game-folder validation failed. No package files were installed.';
    if LoadStringFromFile(ExpandConstant('{tmp}\install-error.txt'), ErrorText) then Result := Result + #13#10 + UTF8Decode(ErrorText);
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  Launcher: String;
  ErrorText: AnsiString;
begin
  Result := False;
  if CheckForMutexes('Global\DiRT3VR.Session') then
  begin
    MsgBox('Close DiRT 3 and DiRT3VR, letting DiRT3VR restore its files, before uninstalling.', mbError, MB_OK);
    exit;
  end;
  Launcher := ExpandConstant('{app}\DiRT3VR\DiRT3VR.exe');
  if FileExists(Launcher) and (not Exec(Launcher, '--check-uninstall --game "' + ExpandConstant('{app}') + '" --error-log "' + ExpandConstant('{tmp}\uninstall-error.txt') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0)) then
  begin
    if LoadStringFromFile(ExpandConstant('{tmp}\uninstall-error.txt'), ErrorText) then
      MsgBox(UTF8Decode(ErrorText) + #13#10 + 'DiRT3VR was not uninstalled. Recovery data was preserved.', mbError, MB_OK)
    else
      MsgBox('DiRT3VR could not confirm that its files were restored. It was not uninstalled; recovery data was preserved.', mbError, MB_OK);
    exit;
  end;
  Result := True;
end;
