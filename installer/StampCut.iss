; Установщик StampCut (Inno Setup 6). Собирается в GitHub Actions: ISCC /DSourceDir=... /DOutDir=... StampCut.iss
#ifndef SourceDir
  #define SourceDir "..\dist\StampCut"
#endif
#ifndef OutDir
  #define OutDir "..\dist"
#endif
#define AppVersion GetVersionNumbersString(SourceDir + "\StampCut.exe")

[Setup]
AppId={{5A3C1D2E-7B6F-4E1A-9C0D-1F2E3D4C5B6A}
AppName=StampCut
AppVersion={#AppVersion}
AppPublisher=StampCut
DefaultDirName={autopf}\StampCut
DefaultGroupName=StampCut
UninstallDisplayIcon={app}\StampCut.exe
OutputDir={#OutDir}
OutputBaseFilename=StampCut-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern
ChangesAssociations=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Ярлыки:"
Name: "assoc"; Description: "Открывать видео (MP4, MOV, MKV и др.) в StampCut по двойному клику"; GroupDescription: "Файлы:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\StampCut"; Filename: "{app}\StampCut.exe"
Name: "{group}\Удалить StampCut"; Filename: "{uninstallexe}"
Name: "{autodesktop}\StampCut"; Filename: "{app}\StampCut.exe"; Tasks: desktopicon

[Registry]
; Всегда: StampCut в списке «Открыть с помощью» для видео
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "StampCut"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\shell\open\command"; ValueType: string; ValueData: """{app}\StampCut.exe"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".3gp"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\StampCut.exe\SupportedTypes"; ValueType: string; ValueName: ".mts"; ValueData: ""
; По желанию: StampCut как программа по умолчанию
Root: HKA; Subkey: "Software\Classes\StampCut.Video"; ValueType: string; ValueData: "Видео"; Flags: uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\StampCut.Video\DefaultIcon"; ValueType: string; ValueData: "{app}\StampCut.exe,0"; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\StampCut.Video\shell\open\command"; ValueType: string; ValueData: """{app}\StampCut.exe"" ""%1"""; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "StampCut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: string; ValueName: "StampCut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: string; ValueName: "StampCut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: string; ValueName: "StampCut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: string; ValueName: "StampCut.Video"; ValueData: ""; Flags: uninsdeletevalue; Tasks: assoc

[Run]
Filename: "{app}\StampCut.exe"; Description: "Запустить StampCut"; Flags: nowait postinstall skipifsilent
