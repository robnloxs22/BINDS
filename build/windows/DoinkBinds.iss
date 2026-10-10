; Inno Setup script for Doink Binds (https://jrsoftware.org/isinfo.php)
; Build with:  iscc /DRid=win-x64 build\windows\DoinkBinds.iss   (or build\publish-windows.ps1 -Installer)

#ifndef Rid
  #define Rid "win-x64"
#endif
#define AppName "Doink Binds"
#define AppVersion "1.0.0"
#define AppExe "DoinkBinds.exe"
#define SourceDir "..\..\publish\" + Rid

[Setup]
AppId={{7B0E3C55-4D1A-4C8E-9C1E-D01AB1D5D0C1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Doink
DefaultDirName={autopf}\Doink Binds
DefaultGroupName=Doink Binds
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=..\..\publish
OutputBaseFilename=DoinkBinds-Setup-{#Rid}
SetupIconFile=..\..\src\DoinkBinds\Assets\doink.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ChangesAssociations=yes
#if Rid == "win-arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "associate"; Description: "Open .doink files with Doink Binds"; GroupDescription: "File associations:"

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\Samples\*"; DestDir: "{app}\Samples"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.doink"; ValueType: string; ValueName: ""; ValueData: "DoinkBinds.Macro"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\DoinkBinds.Macro"; ValueType: string; ValueName: ""; ValueData: "Doink Binds Macro"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\DoinkBinds.Macro\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\DoinkBinds.Macro\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
