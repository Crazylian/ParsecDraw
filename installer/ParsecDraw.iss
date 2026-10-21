; ParsecDraw 安装包脚本（Inno Setup 6）
; 版本由 CI 注入：iscc /DMyAppVersion=x.y.z；本地直接编译则回落 0.0.0
; 载荷来自 ..\publish\setup\（自包含目录发布，目标机无需 .NET 运行时）
#ifndef MyAppVersion
#define MyAppVersion "0.0.0"
#endif

#define MyAppName "ParsecDraw"
#define MyAppExeName "ParsecDraw.exe"
#define MyAppPublisher "Crazylian"
#define MyAppURL "https://github.com/Crazylian/ParsecDraw"

[Setup]
; AppId 一旦发布永不更改（升级识别依据）
AppId={{54729241-be88-43a7-af1c-1b0e31f98d31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
; 按用户安装（设置存 %APPDATA%，本就每用户独立），免 UAC
DefaultDirName={localappdata}\Programs\{#MyAppName}
PrivilegesRequired=lowest
ArchitecturesInstallIn64bitMode=x64compatible
SetupIconFile=..\src\ParsecDraw\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName} {#MyAppVersion}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename=ParsecDraw-Setup-{#MyAppVersion}

[Files]
Source: "..\publish\setup\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式(&D)"; GroupDescription: "附加选项:"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "运行 {#MyAppName}"; Flags: nowait postinstall skipifsilent
