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
