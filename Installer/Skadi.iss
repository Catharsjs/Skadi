#define AppName      "Skadi"
#define AppVersion   "2.0.2"
#define AppPublisher "Catharsjs"
#define AppExe       "Skadi.exe"
#ifndef SourceDir
#define SourceDir "..\EventCapture.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\publish"
#endif

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Catharsjs/Skadi
AppSupportURL=https://github.com/Catharsjs/Skadi/issues
AppUpdatesURL=https://github.com/Catharsjs/Skadi/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=SkadiSetup
#ifndef InstallerOutputDir
#define InstallerOutputDir "Output"
#endif

OutputDir={#InstallerOutputDir}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile={#SourceDir}\EventCapture.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Skadi"

[Code]
const
  DotNetDesktopRuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';

function IsDotNetDesktopRuntimeInstalled: Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;

  if not RegGetSubkeyNames(
    HKLM64,
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App',
    Versions) then
  begin
    exit;
  end;

  for Index := 0 to GetArrayLength(Versions) - 1 do
  begin
    if Pos('10.', Versions[Index]) = 1 then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function OnRuntimeDownloadProgress(
  const Url: String;
  const FileName: String;
  const Progress: Int64;
  const ProgressMax: Int64): Boolean;
begin
  if ProgressMax > 0 then
  begin
    WizardForm.ProgressGauge.Position :=
      Progress * WizardForm.ProgressGauge.Max div ProgressMax;
  end;

  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  RuntimeInstallerPath: String;
  ResultCode: Integer;
begin
  Result := '';

  if IsDotNetDesktopRuntimeInstalled() then
  begin
    exit;
  end;

  WizardForm.StatusLabel.Caption :=
    'Downloading .NET Desktop Runtime...';

  try
    DownloadTemporaryFile(
      DotNetDesktopRuntimeUrl,
      'windowsdesktop-runtime-win-x64.exe',
      '',
      @OnRuntimeDownloadProgress);
  except
    Result :=
      'Could not download .NET Desktop Runtime. Please check your internet connection and run setup again.';
    exit;
  end;

  RuntimeInstallerPath :=
    ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');

  WizardForm.StatusLabel.Caption :=
    'Installing .NET Desktop Runtime...';

  if not Exec(
    RuntimeInstallerPath,
    '/install /quiet /norestart',
    '',
    SW_SHOW,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result :=
      'Could not start .NET Desktop Runtime installer.';
    exit;
  end;

  if ResultCode = 3010 then
  begin
    NeedsRestart := True;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Result :=
      '.NET Desktop Runtime installation failed. Exit code: ' +
      IntToStr(ResultCode);
  end;
end;

function IsSkadiRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := False;

  if Exec(
    ExpandConstant('{sys}\cmd.exe'),
    '/C tasklist /FI "IMAGENAME eq {#AppExe}" | find /I "{#AppExe}" >nul',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Result := ResultCode = 0;
  end;
end;

function StopSkadiProcess: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if not IsSkadiRunning() then
  begin
    exit;
  end;

  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/IM "{#AppExe}" /T /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);

  Sleep(1000);
  Result := not IsSkadiRunning();
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;

  if not IsSkadiRunning() then
  begin
    exit;
  end;

  if MsgBox(
    'Skadi is currently running.' + #13#10#13#10 +
    'The application must be closed before it can be uninstalled.' + #13#10 +
    'Do you want Setup to close Skadi now?',
    mbConfirmation,
    MB_YESNO) <> IDYES then
  begin
    Result := False;
    exit;
  end;

  if not StopSkadiProcess() then
  begin
    MsgBox(
      'Setup could not close Skadi.' + #13#10#13#10 +
      'Please close Skadi from the system tray and run uninstall again.',
      mbError,
      MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox(
      'Skadi has been installed successfully!' + #13#10#13#10 +
      'The application runs in the background.' + #13#10 +
      'Default hotkeys:' + #13#10 +
      'Alt+F1 - Save Screenshot' + #13#10 +
      'Alt+F2 - Start/Stop Recording' + #13#10 +
      'Alt+F3 - Save Replay' + #13#10 +
      'Alt+Z - Show/Hide UI' + #13#10#13#10 +
      'You can also open Skadi from the system tray.',
      mbInformation, MB_OK);
end;
