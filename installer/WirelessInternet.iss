; Standard per-user installer. Compile with the signed Inno Setup 6.7.3 toolchain.
; Optional ISCC overrides: /DStageDir=... /DOutputDir=... /DChineseMessagesFile=...
; Stage must be an explicit, sanitized payload; never point it at a live installation.
; No ADB, network command, runtime download, service or scheduled task runs in Setup.

#define AppVersion "1.5.0"
#define AppDisplayName "无线网络共享"
#ifndef StageDir
  #define StageDir AddBackslash(SourcePath) + "..\stage"
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\..\..\outputs"
#endif
#ifndef ChineseMessagesFile
  #define ChineseMessagesFile AddBackslash(SourcePath) + "..\toolchain\inno\Languages\ChineseSimplified.isl"
#endif

[Setup]
AppId=Local.WirelessInternet.Desktop
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppVerName={#AppDisplayName} {#AppVersion}
VersionInfoVersion={#AppVersion}.0
VersionInfoDescription={#AppDisplayName} 安装程序
DefaultDirName={localappdata}\Programs\WirelessInternet
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName={#AppDisplayName}
DisableProgramGroupPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex=Local\WirelessInternetDesktopRelease
SetupMutex=Local\WirelessInternetSetupRelease
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayIcon={app}\WirelessInternet.exe
OutputDir={#OutputDir}
OutputBaseFilename=WirelessInternet-{#AppVersion}-Setup-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
SetupLogging=no
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(ChineseMessagesFile)
Name: "chinesesimp"; MessagesFile: "{#ChineseMessagesFile}"
#endif

[CustomMessages]
english.DesktopIcon=Create a desktop shortcut
english.StartApplication=Launch Wireless Internet Sharing
english.UninstallShortcut=Uninstall Wireless Internet Sharing
english.ActiveSession=An active or unfinished sharing session is recorded in this installation.%n%nUse the application's Disconnect action, then choose Exit from its tray menu. Retry only after sharing has stopped. Setup will not stop ADB or modify the phone automatically.
english.RunningApplication=Wireless Internet Sharing is still running. Choose Exit from its tray menu and retry. Setup will not terminate the application or the shared ADB server.
english.InvalidDirectory=Choose a writable per-user application directory. Drive roots, network paths, Windows and Program Files directories, and the existing D:\Tools\wireless-internet portable installation are not allowed.%n%nThe normal destination is your LocalAppData\Programs\WirelessInternet directory.
english.NeedDotNet=Microsoft .NET Framework 4.8 or newer is required (Release value 528040 or greater). Install or enable it through Windows, then run this installer again. This installer does not download or install runtimes.
#if FileExists(ChineseMessagesFile)
chinesesimp.DesktopIcon=创建桌面快捷方式
chinesesimp.StartApplication=启动无线网络共享
chinesesimp.UninstallShortcut=卸载无线网络共享
chinesesimp.ActiveSession=此安装目录仍记录着活动或尚未结束的共享会话。%n%n请先在应用中点击“断开连接”，再从托盘菜单选择“退出”，确认共享结束后重试。安装程序不会自动停止 ADB 或修改手机。
chinesesimp.RunningApplication=无线网络共享仍在运行。请从托盘菜单选择“退出”后重试。安装程序不会强制结束应用或共享的 ADB 服务。
chinesesimp.InvalidDirectory=请使用当前用户可写的独立应用目录。不允许安装到盘符根目录、网络路径、Windows、Program Files 或现有 D:\Tools\wireless-internet 便携工具目录。%n%n默认目录为当前用户的 LocalAppData\Programs\WirelessInternet。
chinesesimp.NeedDotNet=需要 Microsoft .NET Framework 4.8 或更高版本（Release 值不低于 528040）。请通过 Windows 安装或启用后重试；本安装程序不会下载或安装运行时。
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Enumerate root files: state, logs, credentials and arbitrary staged root files are not included.
Source: "{#StageDir}\WirelessInternet.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\WirelessInternet.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; User bindings survive upgrades and uninstall. The staged file must contain no phone identity.
Source: "{#StageDir}\wireless-config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist uninsneveruninstall
Source: "{#StageDir}\support\*"; DestDir: "{app}\support"; Excludes: "*.log,*.tmp,adbkey,adbkey.pub,wireless-session.json,relay-ready-*"; Flags: ignoreversion recursesubdirs
Source: "{#StageDir}\platform-tools\adb.exe"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#StageDir}\platform-tools\AdbWinApi.dll"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#StageDir}\platform-tools\AdbWinUsbApi.dll"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#StageDir}\platform-tools\libwinpthread-1.dll"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#StageDir}\platform-tools\NOTICE.txt"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#StageDir}\platform-tools\source.properties"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#StageDir}\gnirehtet-rust-win64\*"; DestDir: "{app}\gnirehtet-rust-win64"; Excludes: "*.log,*.tmp,adbkey,adbkey.pub"; Flags: ignoreversion recursesubdirs
Source: "{#StageDir}\source\*"; DestDir: "{app}\source"; Excludes: "*.log,*.tmp,adbkey,adbkey.pub"; Flags: ignoreversion recursesubdirs
Source: "{#StageDir}\notices\*"; DestDir: "{app}\notices"; Flags: ignoreversion recursesubdirs

; Remove only exact obsolete launchers and retired script components on upgrade.
[InstallDelete]
Type: files; Name: "{app}\start-wireless-internet.cmd"
Type: files; Name: "{app}\stop-wireless-internet.cmd"
Type: files; Name: "{app}\wireless-internet.ps1"
Type: files; Name: "{app}\wireless-watchdog.ps1"
Type: files; Name: "{app}\support\gui-action.ps1"
Type: files; Name: "{app}\support\phone-watchdog.sh"
Type: files; Name: "{app}\source\build.ps1"
Type: files; Name: "{app}\使用说明.txt"
Type: files; Name: "{app}\桌面版使用说明.txt"
Type: files; Name: "{app}\source\BUILD.txt"
Type: files; Name: "{app}\notices\第三方组件说明.txt"

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\WirelessInternet.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppDisplayName}"; Filename: "{app}\WirelessInternet.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\WirelessInternet.exe"; Description: "{cm:StartApplication}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent unchecked

; No recursive deletion, [UninstallDelete], [Registry], services, or autorun entries.
; Inno removes only recorded installed files/shortcuts. User config, runtime state,
; unknown files, global .android data, phone authorizations and APKs are left alone.

[Code]
const
  ReleaseMutexName = 'Local\WirelessInternetDesktopRelease';
  PortableRoot = 'D:\Tools\wireless-internet';

function NormalPath(const Value: String): String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Trim(Value)));
end;

function SameOrChildPath(const Candidate, Parent: String): Boolean;
var
  ChildPath, ParentPath: String;
begin
  ChildPath := NormalPath(Candidate);
  ParentPath := NormalPath(Parent);
  Result := (CompareText(ChildPath, ParentPath) = 0) or
    (CompareText(Copy(AddBackslash(ChildPath), 1, Length(AddBackslash(ParentPath))),
      AddBackslash(ParentPath)) = 0);
end;

function AllowedTargetDirectory(const Target: String): Boolean;
var
  Path, Drive: String;
begin
  Path := NormalPath(Target);
  Drive := ExtractFileDrive(Path);
  Result := (Path <> '') and (Drive <> '') and
    (Copy(Path, 1, 2) <> '\\') and
    (CompareText(Path, AddBackslash(Drive)) <> 0) and
    (CompareText(Path, Drive) <> 0);
  if not Result then Exit;
  Result := not SameOrChildPath(Path, ExpandConstant('{win}')) and
    not SameOrChildPath(Path, ExpandConstant('{commonpf32}')) and
    not SameOrChildPath(Path, ExpandConstant('{commonpf64}')) and
    not SameOrChildPath(Path, PortableRoot) and
    not SameOrChildPath(PortableRoot, Path);
end;

function HasNetFramework48: Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and
    (Release >= 528040);
  if not Result then
    Result := RegQueryDWordValue(HKLM32,
      'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and
      (Release >= 528040);
end;

function SessionCheckMessage(const Target: String): String;
begin
  Result := '';
  if CheckForMutexes(ReleaseMutexName) then
    Result := CustomMessage('RunningApplication')
  else if FileExists(AddBackslash(Target) + 'state\wireless-session.json') then
    Result := CustomMessage('ActiveSession');
end;

function InitializeSetup: Boolean;
begin
  Result := False;
  if not HasNetFramework48 then
  begin
    SuppressibleMsgBox(CustomMessage('NeedDotNet'), mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not AllowedTargetDirectory(ExpandConstant('{app}')) then
    Result := CustomMessage('InvalidDirectory')
  else Result := SessionCheckMessage(ExpandConstant('{app}'));
end;

function InitializeUninstall: Boolean;
var
  MessageText: String;
begin
  Result := False;
  if not AllowedTargetDirectory(ExpandConstant('{app}')) then
    MessageText := CustomMessage('InvalidDirectory')
  else MessageText := SessionCheckMessage(ExpandConstant('{app}'));
  if MessageText <> '' then
  begin
    SuppressibleMsgBox(MessageText, mbError, MB_OK, IDOK);
    Exit;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  MessageText: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    MessageText := SessionCheckMessage(ExpandConstant('{app}'));
    if MessageText <> '' then RaiseException(MessageText);
  end;
end;
