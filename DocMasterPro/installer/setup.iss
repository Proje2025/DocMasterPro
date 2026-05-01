[Setup]
AppName=DocMaster Pro
AppVersion=1.0
AppPublisher=DocMasterPro Team
DefaultDirName={autopf}\DocMasterPro
DefaultGroupName=DocMaster Pro
OutputDir=Output
OutputBaseFilename=DocMasterProSetup
SetupIconFile=..\desktop-app\Assets\app.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Files]
Source: "..\desktop-app\bin\Release\net8.0-windows\publish\DocConverter.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\desktop-app\Assets\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DocMaster Pro"; Filename: "{app}\DocConverter.exe"; IconFilename: "{app}\app.ico"
Name: "{group}\Kaldır"; Filename: "{uninstallexe}"
Name: "{commondesktop}\DocMaster Pro"; Filename: "{app}\DocConverter.exe"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Classes\DocMasterPro.PDF"; ValueType: string; ValueName: ""; ValueData: "DocMaster Pro PDF Document"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\DocMasterPro.PDF\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\DocConverter.exe,0"
Root: HKLM; Subkey: "Software\Classes\DocMasterPro.PDF\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\DocConverter.exe"" ""%1"""
Root: HKLM; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: none; ValueName: "DocMasterPro.PDF"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\Applications\DocConverter.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\DocConverter.exe"" ""%1"""; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Applications\DocConverter.exe\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""
Root: HKLM; Subkey: "Software\DocMasterPro\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "DocMaster Pro"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\DocMasterPro\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Open PDF files with DocMaster Pro PDF Studio."
Root: HKLM; Subkey: "Software\DocMasterPro\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\DocConverter.exe,0"
Root: HKLM; Subkey: "Software\DocMasterPro\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "DocMasterPro.PDF"
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\DocConverter.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\DocConverter.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "DocMaster Pro"; ValueData: "Software\DocMasterPro\Capabilities"; Flags: uninsdeletevalue

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne kısayol oluştur"; GroupDescription: "Ek görevler:"

[Run]
Filename: "{tmp}\dotnet-installer.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: ".NET 8 Desktop Runtime yükleniyor..."; Flags: waituntilterminated; Check: not IsDotNet8Installed

[Code]
function IsDotNet8Installed(): Boolean;
var
  Key: String;
begin
  Key := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost';
  Result := RegKeyExists(HKLM, Key);
end;
