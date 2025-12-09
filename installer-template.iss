#define MyAppName "MicaPDF"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "MicaPDF"
#define MyAppURL "https://github.com/TCDev69/MicaPDF"
#define MyAppExeName "MicaPDF.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=Release
OutputBaseFilename=MicaPDF-Setup
ArchitecturesAllowed=
ArchitecturesInstallIn64BitMode=
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "downloadnet8"; Description: "Download and install .NET 8 Desktop Runtime (required if not already installed)"; GroupDescription: "Additional options:"; Flags: checkablealone
Name: "downloadwinappruntime"; Description: "Download and install Windows App Runtime (required if not already installed)"; GroupDescription: "Additional options:"; Flags: checkablealone

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]
var
  DownloadPage: TDownloadWizardPage;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then
    Log(Format('Successfully downloaded %s to %s', [Url, FileName]));
  Result := True;
end;

function IsDotNet8Installed: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('dotnet', '--list-runtimes', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function IsWindowsAppRuntimeInstalled: Boolean;
var
  ResultCode: Integer;
begin
  // Check if Windows App Runtime is installed by looking for the WindowsAppRuntime DLL
  Result := FileExists(ExpandConstant('{commonpf}\WindowsApps\Microsoft.WindowsAppRuntime.1.5_5001.178.1908.0_x64__8wekyb3d8bbwe\Microsoft.WindowsAppRuntime.dll')) or
            FileExists(ExpandConstant('{commonpf}\WindowsApps\Microsoft.WindowsAppRuntime.1.5_5001.178.1908.0_x86__8wekyb3d8bbwe\Microsoft.WindowsAppRuntime.dll'));
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  DotNetInstaller: String;
  WinAppRuntimeInstaller: String;
  ResultCode: Integer;
  DotNetInstallerURL: String;
  WinAppRuntimeInstallerURL: String;
begin
  Result := True;
  
  if CurPageID = wpReady then begin
    // Download and install Windows App Runtime first if needed
    if WizardIsTaskSelected('downloadwinappruntime') and not IsWindowsAppRuntimeInstalled then begin
      DownloadPage.Clear;
      WinAppRuntimeInstaller := ExpandConstant('{tmp}\windowsappruntime.exe');
      
      #ifdef Win64
        WinAppRuntimeInstallerURL := 'https://aka.ms/windowsappsdk/1.5/latest/windowsappruntimeinstall-x64.exe';
      #else
        WinAppRuntimeInstallerURL := 'https://aka.ms/windowsappsdk/1.5/latest/windowsappruntimeinstall-x86.exe';
      #endif
      
      DownloadPage.Add(WinAppRuntimeInstallerURL, 'windowsappruntime.exe', '');
      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
          Result := True;
          
          if FileExists(WinAppRuntimeInstaller) then begin
            if MsgBox('Do you want to install Windows App Runtime now?' + #13#10 + 'This is required to run MicaPDF.', mbConfirmation, MB_YESNO) = IDYES then begin
              Exec(WinAppRuntimeInstaller, '--quiet', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
              if ResultCode = 0 then
                MsgBox('Windows App Runtime installed successfully!', mbInformation, MB_OK)
              else
                MsgBox('Failed to install Windows App Runtime. You may need to install it manually.', mbError, MB_OK);
            end;
          end;
        except
          if DownloadPage.AbortedByUser then begin
            Result := False;
          end else begin
            MsgBox('Failed to download Windows App Runtime installer. You can download it manually from microsoft.com', mbError, MB_OK);
          end;
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
    
    // Then download and install .NET 8 if needed
    if WizardIsTaskSelected('downloadnet8') and not IsDotNet8Installed then begin
      DownloadPage.Clear;
      DotNetInstaller := ExpandConstant('{tmp}\dotnet8-desktop-runtime.exe');
      
      #ifdef Win64
        DotNetInstallerURL := 'https://download.visualstudio.microsoft.com/download/pr/4d6cbbf4-002a-4575-a2e5-e7920c0cd6eb/1e12ca0e0c74e5e9fb15c7d09784d1b5/windowsdesktop-runtime-8.0.11-win-x64.exe';
      #else
        DotNetInstallerURL := 'https://download.visualstudio.microsoft.com/download/pr/b280d97f-25a9-4ab7-a7d8-2ff1c3c26a0c/1f7cf9397246708199f5a3e730323683/windowsdesktop-runtime-8.0.11-win-x86.exe';
      #endif
      
      DownloadPage.Add(DotNetInstallerURL, 'dotnet8-desktop-runtime.exe', '');
      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
          Result := True;
          
          if FileExists(DotNetInstaller) then begin
            if MsgBox('Do you want to install .NET 8 Desktop Runtime now?' + #13#10 + 'This is required to run MicaPDF.', mbConfirmation, MB_YESNO) = IDYES then begin
              Exec(DotNetInstaller, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
              if ResultCode = 0 then
                MsgBox('.NET 8 Desktop Runtime installed successfully!', mbInformation, MB_OK)
              else
                MsgBox('Failed to install .NET 8. You may need to install it manually.', mbError, MB_OK);
            end;
          end;
        except
          if DownloadPage.AbortedByUser then begin
            Result := False;
          end else begin
            MsgBox('Failed to download .NET 8 installer. You can download it manually from microsoft.com', mbError, MB_OK);
          end;
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "MicaPDF.Document"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\MicaPDF.Document"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\MicaPDF.Document\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKCU; Subkey: "Software\Classes\MicaPDF.Document\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
