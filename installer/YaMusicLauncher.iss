#define MyAppName "YaMusic Launcher"
#define MyAppExeName "YaLauncher.exe"
#define MyAppPublisher "m1ndst0rm"

#ifndef AppVersion
  #define AppVersion "1.1.4"
#endif

#ifndef SourceDir
  #define SourceDir "..\\artifacts\\publish\\win-x64"
#endif

[Setup]
AppId={{B44F6E2A-262D-4E6C-AC4A-96AF5B44A5CD}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\YaMusic Launcher
DefaultGroupName=YaMusic Launcher
DisableProgramGroupPage=yes
OutputDir={#SourcePath}\output
OutputBaseFilename=YaMusicLauncher-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительно:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\YaMusic Launcher"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\YaMusic Launcher"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить YaMusic Launcher"; Flags: nowait postinstall skipifsilent
