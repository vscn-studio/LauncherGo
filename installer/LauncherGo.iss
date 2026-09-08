#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-local"
#endif

#ifndef BuildDir
  #define BuildDir "..\artifacts\publish\win-x64"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{26FFCC71-304F-4FF1-AC1A-3E244C276414}
AppName=LauncherGo
AppVersion={#MyAppVersion}
AppPublisher=Vintage Story CN Studio
DefaultDirName={autopf}\LauncherGo
DefaultGroupName=LauncherGo
OutputDir={#OutputDir}
#ifdef OutputBaseFilename
OutputBaseFilename={#OutputBaseFilename}
#else
OutputBaseFilename=LauncherGo-Setup-{#MyAppVersion}-win-x64
#endif
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\LauncherGo.App.exe
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#ifexist "C:\Program Files (x86)\Inno Setup 6\Languages\ChineseSimplified.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourcePath}Install-DotNetRuntimes.ps1"; Flags: dontcopy
Source: "{#BuildDir}\LauncherGo.ServerHost.runtimeconfig.json"; Flags: dontcopy
Source: "{#BuildDir}\LauncherGo.GatewayHost.runtimeconfig.json"; Flags: dontcopy
Source: "{#BuildDir}\LauncherGo.ServerMapHost.runtimeconfig.json"; Flags: dontcopy
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"
Name: "{autodesktop}\LauncherGo"; Filename: "{app}\LauncherGo.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LauncherGo.App.exe"; Description: "{cm:LaunchProgram,LauncherGo}"; Flags: nowait postinstall skipifsilent

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('Install-DotNetRuntimes.ps1');
  ExtractTemporaryFile('LauncherGo.ServerHost.runtimeconfig.json');
  ExtractTemporaryFile('LauncherGo.GatewayHost.runtimeconfig.json');
  ExtractTemporaryFile('LauncherGo.ServerMapHost.runtimeconfig.json');
  WizardForm.StatusLabel.Caption := 'Checking / installing Microsoft .NET 10 runtimes...';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    ExpandConstant('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{tmp}\Install-DotNetRuntimes.ps1" -PayloadRoot "{tmp}"'),
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Unable to start the .NET runtime installer.';
    Exit;
  end;
  NeedsRestart := (ResultCode = 3010) or (ResultCode = 1641);
  if (ResultCode <> 0) and not NeedsRestart then
    Result := ExpandConstant('Could not install the required x64 .NET 10 runtimes. Check your network and retry. Details: {tmp}\dotnet-install.log');
end;
