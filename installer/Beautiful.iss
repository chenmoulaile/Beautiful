; Beautiful — Inno Setup 安装脚本
;
; 构建（仓库根目录）：
;   .\dist 先由 dotnet publish 产出，然后
;   ISCC.exe /DAppVersion=3.3.0 /DSourceDir=dist\Beautiful /DOutputDir=installer\out installer\Beautiful.iss
;
; 三个设计决定写在前面，改之前请先读：
;
; 1) **按用户安装（PrivilegesRequired=lowest）**。本应用把 config\dock.json、logs\、cache\ 写在
;    exe 旁边，装进系统 Program Files 后这些写入会被 UAC 虚拟化到 VirtualStore —— 表现为
;    "改了设置没生效 / 日志找不到"。装到用户目录（{autopf} 在非提权下就是
;    %LOCALAPPDATA%\Programs）才能让"写在程序目录旁"这套现有逻辑继续成立。
; 2) **卸载/覆盖安装前先发 `--exit`**，不要 taskkill。本应用会隐藏原生任务栏并接管 Alt+Tab，
;    强杀会把任务栏 autohide 位与键盘钩子留在系统里（README 已知限制第 6 条），用户第一时间就会撞上。
; 3) **打包时排除 logs/config/cache**：那是开发机自己的运行数据，不属于发布物。

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "dist\Beautiful"
#endif
#ifndef OutputDir
  #define OutputDir "installer\out"
#endif

#define AppName "Beautiful"
#define AppExe "Beautiful.exe"

; VersionInfoVersion 只接受纯数字（x.y[.z[.b]]），而本地试构建常带 -test/-rc 后缀。
; 文件属性里放剥掉后缀的数字版本，人看的版本（AppVersion / 文件名）保留完整字符串。
#define DashPos Pos("-", AppVersion)
#if DashPos > 0
  #define VersionNum Copy(AppVersion, 1, DashPos - 1)
#else
  #define VersionNum AppVersion
#endif

[Setup]
; AppId 一旦发布就不能改：它是升级/卸载时识别同一产品的键，换了就会装出两份
AppId={{8F0C4C1E-9E2B-4C7A-9C31-6E5D2A7B8F44}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=chenmoulaile
AppPublisherURL=https://github.com/chenmoulaile/Beautiful
AppSupportURL=https://github.com/chenmoulaile/Beautiful/issues
AppUpdatesURL=https://github.com/chenmoulaile/Beautiful/releases/latest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=Beautiful-Setup-v{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no
WizardStyle=modern
; 自包含产物里没有独立的 .ico（图标在 exe 内），安装程序沿用 exe 图标，不另外造一个来源不明的图标
SetupIconFile=
VersionInfoVersion={#VersionNum}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

[Languages]
; Inno Setup 6 官方只带上面 Languages\ 里的那些，没有简体中文（简体属于第三方翻译包，
; 版权归翻译者，不随手贴进仓库）。向导因此是英文界面，应用本体是中文——
; 这条已写进 README 的已知限制，不做"看起来像中文其实半吊子"的半吊子本地化。
Name: "default"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; 不用 createallsubdirs：那会把源目录里的空子目录（开发机跑出来的 logs/config/cache）
; 也照样在目标机上建出来，即使里面的文件已被 Excludes 排掉。
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "logs\*,config\*,cache\*"; \
  Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{group}\{#AppName} uninstall"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; \
  WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 运行期生成在程序目录旁的数据由卸载器负责清掉（它们不在 [Files] 里，装了也不会被覆盖，
; 但不删就会在卸载后留下一堆孤儿目录）
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\config"
Type: filesandordirs; Name: "{app}\cache"

[Code]
// 让正在跑的实例走它自己的完整退出链路（还原任务栏 / 卸键盘钩子 / ABE_TOP ABM_REMOVE）。
// 返回 True 只表示"信号发出去了"，等 2 秒是给它的清理代码跑完的时间；
// 绝不用 taskkill —— 那正是本文件开头第 2 条要避免的事。
function AskRunningInstanceToExit(Executable: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := False;
  if not FileExists(Executable) then Exit;
  if Exec(Executable, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    // 退出码 0 = 通知到了另一个实例；3 = 没有在跑的实例（正常，说明本来就没装/没开）
    Log(Format('Beautiful --exit returned %d', [ResultCode]));
    Result := True;
  end;
  Sleep(2000);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 覆盖安装：旧实例还开着就会锁住 exe/dll，先请它退出
  if CurStep = ssInstall then
    AskRunningInstanceToExit(ExpandConstant('{app}\Beautiful.exe'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    AskRunningInstanceToExit(ExpandConstant('{app}\Beautiful.exe'));
end;
