; Инсталлятор CVP (Inno Setup 6).
;
; Сам по себе этот скрипт ничего не собирает: он упаковывает готовый результат
; tools/publish.ps1 (single-file exe плюс подкаталог FFmpeg рядом с ним).
; Обычный запуск — через tools/make-installer.ps1, который сначала соберёт билд,
; а потом позовёт ISCC с нужными /D-параметрами.
;
;   ISCC.exe tools\installer.iss /DSourceDir=...\publish\win-x64 /DAppVersion=1.0.0

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\publish\installer"
#endif

#define AppName "CVP"
#define AppExe  "ComparisonPlayer.exe"

[Setup]
; Идентификатор приложения: менять нельзя — по нему обновление находит прошлую установку.
AppId={{7C4E4E4F-2C2E-4C7B-9F2D-1D6B3E9A5C10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
OutputDir={#OutputDir}
OutputBaseFilename=CVP-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Приложение 64-разрядное (D3D11-декод, x64-библиотеки FFmpeg).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Без прав администратора ставится в профиль пользователя — так поставить может любой.
PrivilegesRequiredOverridesAllowed=dialog commandline

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Весь результат publish: exe и подкаталог FFmpeg (без него не откроется ни один файл).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Кэш кадров и настройки лежат в профиле пользователя и удаляются только по его желанию —
; здесь чистим лишь то, что создаём сами рядом с программой.
Type: filesandordirs; Name: "{app}\FFmpeg"
