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
; Идентификатор типа файла: тот же, что прописывает само приложение (FileAssociations.cs).
; Менять нельзя — по нему Windows помнит выбор пользователя в «Открыть с помощью».
#define ProgId  "CVP.Video"

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
; Ставим ассоциации видеофайлов — оболочке нужно сказать об этом по окончании установки.
ChangesAssociations=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
russian.AssocGroup=Типы файлов:
russian.AssocTask=Зарегистрировать CVP для видеофайлов (появится в «Открыть с помощью»)
english.AssocGroup=File types:
english.AssocTask=Register CVP for video files (adds it to "Open with")

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Регистрация не отбирает у пользователя текущий плеер по умолчанию — только добавляет
; CVP в «Открыть с помощью», поэтому включена сразу.
Name: "fileassoc"; Description: "{cm:AssocTask}"; GroupDescription: "{cm:AssocGroup}"

[Files]
; Весь результат publish: exe и подкаталог FFmpeg (без него не откроется ни один файл).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Ассоциации видеофайлов (задача #13). Корень HKA: при установке с правами администратора
; это HKLM (для всех пользователей), при установке в профиль — HKCU. Плеером по умолчанию
; инсталлятор себя не назначает: с Windows 10 такой выбор делается только пользователем в
; системном окне, а запись мимо него система отбрасывает. Здесь — регистрация типа файла и
; список возможностей, по которому CVP виден в «Приложениях по умолчанию».
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueData: "Видео CVP"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "Видео CVP"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueData: "{app}\{#AppExe},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: fileassoc

; «Открыть с помощью» для каждого расширения. Именно OpenWithProgIds, а не значение по
; умолчанию у расширения: так у пользователя не отбирается текущий плеер.
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgIds";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithProgIds";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithProgIds";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.avi\OpenWithProgIds";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ts\OpenWithProgIds";   ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithProgIds";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wmv\OpenWithProgIds";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mpg\OpenWithProgIds";  ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mpeg\OpenWithProgIds"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty; Tasks: fileassoc

; Возможности приложения: без них окно «Приложения по умолчанию» CVP не покажет —
; его список строится по RegisteredApplications.
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Покадровое сравнение двух видео"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#AppExe},0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4";  ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv";  ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov";  ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi";  ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts";   ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v";  ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv";  ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg";  ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "{#ProgId}"; Tasks: fileassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Кэш кадров и настройки лежат в профиле пользователя и удаляются только по его желанию —
; здесь чистим лишь то, что создаём сами рядом с программой.
Type: filesandordirs; Name: "{app}\FFmpeg"
