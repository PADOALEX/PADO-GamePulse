#ifndef AppVersion
  #error AppVersion must be provided by Build-Installer.ps1
#endif
#ifndef PublishDir
  #error PublishDir must reference the verified self-contained Windows publish folder
#endif
#ifndef InstallerOutput
  #error InstallerOutput must reference a fresh output folder
#endif
#ifndef RetirementInclude
  #error RetirementInclude must be generated from verified release payload histories
#endif

[Setup]
AppId={{7BE24789-A462-4F37-9203-BA468744067E}
AppName=PADO GamePulse
AppVersion={#AppVersion}
AppPublisher=PADO GamePulse contributors
AppComments=Independent Steam Profile Explorer and Gaming Analytics
DefaultDirName={localappdata}\Programs\PADO GamePulse
DefaultGroupName=PADO GamePulse
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputDir={#InstallerOutput}
OutputBaseFilename=PADO.GamePulse-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\GamePulse.Desktop\Assets\GamePulse.ico
UninstallDisplayIcon={app}\GamePulse.Desktop.exe
WizardStyle=modern
DisableProgramGroupPage=yes
Compression=lzma2/fast
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousTasks=yes
SetupMutex=Local\PADO.GamePulse.Setup.7BE24789-A462-4F37-9203-BA468744067E
Uninstallable=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription=PADO GamePulse per-user installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PADO GamePulse"; Filename: "{app}\GamePulse.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\PADO GamePulse"; Filename: "{app}\GamePulse.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\GamePulse.Desktop.exe"; Description: "Launch PADO GamePulse"; Flags: nowait postinstall skipifsilent

[Code]
const
  ProductUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7BE24789-A462-4F37-9203-BA468744067E}_is1';

function CheckInstalledVersion: String;
var
  InstalledVersionText: String;
  InstalledVersion, SetupVersion: Int64;
begin
  Result := '';
  if not RegKeyExists(HKCU, ProductUninstallKey) then
    exit;

  { Refuse an ambiguous registration rather than silently replacing a newer app. }
  if not RegQueryStringValue(HKCU, ProductUninstallKey, 'DisplayVersion', InstalledVersionText) then
  begin
    Result := 'The installed PADO GamePulse version could not be read. Use Windows Installed apps to repair or uninstall that installation before continuing.';
    exit;
  end;
  if not StrToVersion(InstalledVersionText, InstalledVersion) then
  begin
    Result := 'The installed PADO GamePulse version is not recognized. Use Windows Installed apps to repair or uninstall that installation before continuing.';
    exit;
  end;
  if not StrToVersion('{#AppVersion}', SetupVersion) then
  begin
    Result := 'This PADO GamePulse installer has an invalid version. Obtain a verified release.';
    exit;
  end;
  if ComparePackedVersion(InstalledVersion, SetupVersion) > 0 then
    Result := 'A newer PADO GamePulse version (' + InstalledVersionText + ') is already installed. This installer cannot downgrade it. Keep the newer version, or uninstall it explicitly before installing an older release.';
end;

function InitializeSetup: Boolean;
var
  Conflict: String;
begin
  Conflict := CheckInstalledVersion;
  Result := Conflict = '';
  if not Result then
  begin
    Log('PADO GamePulse installation rejected: ' + Conflict);
    SuppressibleMsgBox(Conflict, mbCriticalError, MB_OK, IDOK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Recheck just before writing files, including after an interactive wizard wait. }
  Result := CheckInstalledVersion;
end;

function FileAttributes(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function HasUnsafePath(const FileName: String): Boolean;
var
  Path, Parent: String;
  Attributes: LongWord;
begin
  Result := True;
  Path := FileName;
  while Path <> '' do
  begin
    Attributes := FileAttributes(Path);
    if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then
      exit;
    Parent := ExtractFileDir(Path);
    if CompareText(Parent, Path) = 0 then
      break;
    Path := Parent;
  end;
  Result := False;
end;

procedure RetireManagedFile(const RelativePath, ExpectedHash: String);
var
  FileName, ActualHash: String;
begin
  { Relative names and hashes are validated and compiled into this setup. No wildcard,
    recursive directory deletion or mutable installed manifest is used. }
  FileName := AddBackslash(ExpandConstant('{app}')) + RelativePath;
  if not FileExists(FileName) then
    exit;
  if HasUnsafePath(FileName) then
  begin
    Log('Preserving retired path with inaccessible or reparse components: ' + RelativePath);
    exit;
  end;
  try
    ActualHash := GetSHA256OfFile(FileName);
  except
    Log('Preserving retired path whose ownership hash could not be read: ' + RelativePath);
    exit;
  end;
  if CompareText(ActualHash, ExpectedHash) <> 0 then
  begin
    Log('Preserving a modified retired path: ' + RelativePath);
    exit;
  end;
  if not DeleteFile(FileName) then
    RaiseException('The application was installed, but an obsolete managed file is in use. Close PADO GamePulse and run this installer again before launching it.');
  Log('Removed a verified obsolete managed file: ' + RelativePath);
end;

#include RetirementInclude

procedure RegisterExtraCloseApplicationsResources;
begin
  RegisterRetiredManagedResources;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { Clean only after the new payload was installed. A failed earlier install keeps
    the old payload intact; the standard uninstall log still records managed files. }
  if CurStep = ssPostInstall then
    RemoveRetiredManagedFiles;
end;
