; Inno Setup Script for JL Engine NOW
; Compile with Inno Setup 7

[Setup]
AppName=JL Engine NOW
AppVersion=1.0.0
DefaultDirName={commonpf64}\JL Engine NOW
DefaultGroupName=JL Engine NOW
OutputBaseFilename=JL-Engine-NOW-Setup
Compression=lzma
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
AppendDefaultDirName=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\publish\JLEngine.Host\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\JL Engine NOW"; Filename: "{app}\JLEngine.Host.exe"
Name: "{group}\JL Engine NOW (Console)"; Filename: "{app}\JLEngine.Host.exe"; Parameters: "--urls http://localhost:8081"

[Run]
Filename: "{app}\JLEngine.Host.exe"; Description: "Launch JL Engine NOW"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
