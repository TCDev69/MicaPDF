#define MyAppName "MicaPDF"
#define MyAppVersion "1.2.0"
#define MyAppPublisher "TCDev"
#define MyAppExeName "MicaPDF.exe"

[Setup]
; NOTE: The value of AppId uniquely identifies this application. Do not use the same AppId value in installers for other applications.
; (To generate a new GUID, click Tools | Generate GUID inside the IDE.)
AppId={{E6E6A6A6-1234-5678-90AB-CDEF12345678}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DisableProgramGroupPage=yes
; Remove the following line to run in administrative install mode (install for all users.)
PrivilegesRequired=lowest
OutputDir=Installer
OutputBaseFilename=MicaPDF_Setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; IMPORTANT: Update the source path to match your publish output directory
Source: "bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
  NetRuntimeInstalled: Boolean;
  Result1: Boolean;
begin
  // Check for .NET 8 Desktop Runtime (x64)
  // Registry key for .NET 8
  NetRuntimeInstalled := RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedhost\Microsoft.NETCore.App\8.0.0') or 
                         RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedhost\Microsoft.NETCore.App\8.0.0');

  if not NetRuntimeInstalled then
  begin
    if MsgBox('This application requires .NET Desktop Runtime 8.0. Would you like to download it now?', mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-8.0.0-windows-x64-installer', '', '', SW_SHOWNORMAL, mbConfirmation, ErrorCode);
      MsgBox('Please install the .NET Runtime and then run this setup again.', mbInformation, MB_OK);
      Result := False;
    end
    else
    begin
      MsgBox('.NET Runtime 8.0 is required. Setup cannot continue.', mbCriticalError, MB_OK);
      Result := False;
    end;
  end
  else
  begin
    Result := True;
  end;
end;
