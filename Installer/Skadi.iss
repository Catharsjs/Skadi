#define AppName      "Skadi"
#define AppVersion   "2.0.0"
#define AppPublisher "Catharsjs"
#define AppExe       "Skadi.exe"
#define SourceDir    "C:\Users\user\source\repos\EventCapture\EventCapture.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Catharsjs/EventCapture
AppSupportURL=https://github.com/Catharsjs/EventCapture/issues
AppUpdatesURL=https://github.com/Catharsjs/EventCapture/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
OutputBaseFilename=Skadi_Setup_v2.0.0
OutputDir=C:\Users\user\source\repos\EventCapture\Installer\Output
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile={#SourceDir}\EventCapture.ico
UninstallDisplayIcon={app}\{#AppExe}

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
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    MsgBox(
      'Skadi has been installed successfully!' + #13#10#13#10 +
      'The application runs in the background.' + #13#10 +
      'Default hotkeys:' + #13#10 +
      'Alt+F1 - Save Screenshot' + #13#10 +
      'Alt+F2 - Save Replay' + #13#10 +
      'Alt+F3 - Start/Stop Recording' + #13#10 +
      'Alt+Z - Show/Hide UI' + #13#10#13#10 +
      'You can also open Skadi from the system tray.',
      mbInformation, MB_OK);
end;
