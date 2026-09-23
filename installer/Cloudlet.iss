#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#ifndef SourceDirectory
  #define SourceDirectory "..\release\Cloudlet-" + AppVersion + "-win-x64"
#endif
#ifndef PackageOutput
  #define PackageOutput "..\release"
#endif
#define AppName "Cloudlet"
#define AppExe "Cloudlet.exe"

[Setup]
; Keep the exact historical AppId spelling so existing per-user registration is reused.
AppId={{6ffd6ec1-6466-49e3-b643-276d65864d98}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=FueTsui
AppPublisherURL=https://github.com/FueTsui/RcloneLink
AppSupportURL=https://github.com/FueTsui/RcloneLink
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Cloudlet for Windows 11
DefaultDirName={localappdata}\Programs\Cloudlet
DefaultGroupName={#AppName}
; In non-administrative mode Inno reads previous installation data from HKCU only.
; Reusing the previous directory keeps its uninstall log with the same AppId.
; HKLM installations are not inherited, uninstalled, or modified.
UsePreviousAppDir=yes
UsePreviousPrivileges=no
UsePreviousGroup=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#PackageOutput}
OutputBaseFilename=Cloudlet-{#AppVersion}-Setup-x64
SetupIconFile=..\assets\Cloudlet\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
; Running applications are rejected by PrepareToInstall, never terminated by Setup.
CloseApplications=no
CloseApplicationsFilter=Cloudlet.exe,RcloneLink.exe,RcloneLink.App.exe
RestartApplications=no
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.CreateDesktopShortcut=Create a desktop shortcut
chinesesimplified.CreateDesktopShortcut=创建桌面快捷方式
english.InstallWinFsp=Install WinFsp for drive mounting (optional; administrator permission required)
chinesesimplified.InstallWinFsp=安装 WinFsp 以启用磁盘挂载（可选，需要管理员权限）
english.CloseCloudlet=Cloudlet or RcloneLink is running. Exit it from its tray menu, allow active operations to finish, and retry. Setup will not stop your mounts or transfers automatically.
chinesesimplified.CloseCloudlet=Cloudlet 或 RcloneLink 正在运行。请从托盘菜单退出并等待挂载和传输结束，然后重试。安装程序不会自动停止您的挂载或传输。
english.ProcessCheckFailed=Setup could not check running applications. Please close Cloudlet and RcloneLink, then retry.
chinesesimplified.ProcessCheckFailed=安装程序无法检查运行中的应用。请关闭 Cloudlet 和 RcloneLink 后重试。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; Flags: unchecked

[Files]
Source: "{#SourceDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{sys}\msiexec.exe"; Parameters: "/i ""{app}\winfsp-2.1.25156.msi"""; Verb: "runas"; Description: "{cm:InstallWinFsp}"; Flags: shellexec postinstall unchecked skipifsilent waituntilterminated; Check: WinFspNotInstalled
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

; No startup registration, legacy uninstaller execution, or user-data deletion.
; AppData\RcloneLink and rclone configuration/cache remain application-owned.
[Code]
function WinFspNotInstalled: Boolean;
begin
  Result := not (RegKeyExists(HKLM64, 'SOFTWARE\WinFsp') or
    RegKeyExists(HKLM32, 'SOFTWARE\WinFsp'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand, ExpectedCommand: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  ExpectedCommand := '"' + ExpandConstant('{app}\{#AppExe}') + '" --minimized';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
    'Cloudlet', StartupCommand) and (StartupCommand = ExpectedCommand) then
  begin
    if not RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Cloudlet') then
      Log('Could not remove this installation''s Cloudlet startup value.');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Locator, Service, Processes: Variant;
begin
  Result := '';
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT Name FROM Win32_Process WHERE Name=''Cloudlet.exe'' OR Name=''RcloneLink.exe'' OR Name=''RcloneLink.App.exe''');
    if Processes.Count > 0 then
      Result := CustomMessage('CloseCloudlet');
  except
    Result := CustomMessage('ProcessCheckFailed');
  end;
end;
