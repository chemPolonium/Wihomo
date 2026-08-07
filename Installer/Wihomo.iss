#ifndef MyAppVersion
  #define MyAppVersion "1.1.1"
#endif

#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{B25875E1-3AFC-4E13-A4AE-B2F778DFA37D}
AppName=Wihomo
AppVersion={#MyAppVersion}
AppPublisher=chemPolonium
DefaultDirName={autopf}\Wihomo
DefaultGroupName=Wihomo
UninstallDisplayIcon={app}\Wihomo.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=Wihomo-Setup-{#MyAppVersion}
SetupIconFile=..\Wihomo.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "cs\*,de\*,es\*,fr\*,it\*,ja\*,ko\*,pl\*,pt-BR\*,ru\*,tr\*,zh-Hans\*,zh-Hant\*"

[Icons]
Name: "{group}\Wihomo"; Filename: "{app}\Wihomo.exe"
Name: "{autodesktop}\Wihomo"; Filename: "{app}\Wihomo.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Wihomo.exe"; Description: "启动 Wihomo"; Flags: nowait postinstall skipifsilent
