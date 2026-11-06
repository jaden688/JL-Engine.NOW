#define MyAppName "JL Engine"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "JL Engine"
#define MyAppExeName "JLEngine.Host.exe"

[Setup]
AppId={{B8A2E92D-47D5-4D19-8E3D-4CBB5E4A9A17}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\JL Engine
DefaultGroupName=JL Engine
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\..\..\artifacts
OutputBaseFilename=JL-Engine-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Dirs]
Name: "{app}\data"

[Files]
Source: "..\..\..\artifacts\JLEngine-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\data\*"; DestDir: "{app}\data"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\JL Engine"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}\data"
Name: "{autodesktop}\JL Engine"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}\data"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch JL Engine"; WorkingDir: "{app}\data"; Flags: postinstall nowait skipifsilent
