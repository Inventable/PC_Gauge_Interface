#define MyAppName "Northstar Gauge Interface"
#define MyAppPublisher "Northstar Downhole Specialists"
#define MyAppExeName "Gauge.Interface.App.exe"
#define MyPublishDir "..\dist\publish\win-x64"
#ifndef MyAppVersion
#ifndef MyAppVersion
  #define MyAppVersion GetFileVersion(MyPublishDir + "\" + MyAppExeName)
#endif
#endif

[Setup]
AppId={{4CE71FE9-D9E1-4AC2-8D69-7C027DA0E480}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\Northstar Gauge Interface
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Northstar-Gauge-Interface-Setup-{#MyAppVersion}
SetupIconFile=..\src\Gauge.Interface.App\Assets\northstar-app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
