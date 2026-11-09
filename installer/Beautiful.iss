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

; ---- round7 r3：编译期形态门禁 ----
; SourceDir 必须是 publish.cmd / install-local.cmd / build-installer.cmd 产出的自包含目录
;（发布自检通过后写入 SC-STAMP.txt）。任何旁路盖拷进发布目录的混合产物（"FDD runtimeconfig +
; app-local host + partial runtime 件"，app-local hostfxr 锁死框架查找根，装完双击必死——
; round7 三轮真机缺陷同一根因）没有 stamp，ISCC 直接拒编。运行期 ssPostInstall 摘 host
; （见 [Code]）是最后保险，编译期拦下才是正道。
#if !FileExists(AddBackslash(SourceDir) + "SC-STAMP.txt")
  #error 发布目录缺 SC-STAMP.txt：SourceDir 不是脚本产出的自包含形态（round7 r3，混合产物装完双击必死）。请用 scripts\build-installer.cmd 打包；不要把 bin\Release 构建输出或 lite 产物拷进发布目录。
#endif

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
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "logs\*,config\*,cache\*,SC-STAMP.txt"; \
  Flags: ignoreversion recursesubdirs

[InstallDelete]
; round5 真机缺陷 1（安装目录启动链）的根因修口：覆盖安装时，旧布局（self-contained）残留在
; {app} 的 .NET 宿主/运行时文件必须先删。apphost 按"先 app-local 后系统"的顺序解析 fxr，
; 残留的 hostfxr.dll 抢先于系统 fxr 被加载（COREHOST_TRACE 实拍 'Resolved fxr [app]\hostfxr.dll'），
; 与新 runtimeconfig/deps.json 不配套 → 弹 "You must install or update .NET to run this
; application." 且进程挂死、--exit 等一切参数无效。真机取证：移走该文件后应用立即正常启动。
; [InstallDelete] 先于 [Files] 执行：自包含新产物随后会把同名文件原样拷回（hostfxr/hostpolicy/
; coreclr 本就是 self-contained 产物的组成部分），framework-dependent 新产物则从此走系统 fxr——
; 两种布局都安全。logs/config/cache 是用户数据，归 [UninstallDelete]，此处绝不触碰。
;
; round7 真机缺陷 1 补全：round5 名单只删 9 个宿主件，真机现场（8.0 自包含残留 + net10 应用
; 文件混装，取证 docs/evidence/round7/00-boot-error-screen.png）里 System.Private.CoreLib.dll
; 等旧 runtime 程序集仍成堆躺在 {app}。卡启动的虽只有宿主三件套（hostfxr 锁定框架查找根），
; 但残留会在历次升级中滚雪球。这里扩成"宿主 9 件 + BCL/WPF/VC runtime 通配与散件"：
; 任意旧 self-contained 布局一次清干净。通配只匹配 {app} 根层、不递归子目录，logs/config/cache
; 依旧安全；新产物自带的同名件随后由 [Files] 原样拷回，删了不亏、留着必脏。
Type: files; Name: "{app}\hostfxr.dll"
Type: files; Name: "{app}\hostpolicy.dll"
Type: files; Name: "{app}\nethost.dll"
Type: files; Name: "{app}\coreclr.dll"
Type: files; Name: "{app}\clrjit.dll"
Type: files; Name: "{app}\clrcompression.dll"
Type: files; Name: "{app}\mscordaccore.dll"
Type: files; Name: "{app}\mscordbi.dll"
Type: files; Name: "{app}\mscorrc.dll"
Type: files; Name: "{app}\System.Private.CoreLib.dll"
Type: files; Name: "{app}\System.*.dll"
Type: files; Name: "{app}\Microsoft.*.dll"
Type: files; Name: "{app}\Presentation*.dll"
Type: files; Name: "{app}\UIAutomation*.dll"
Type: files; Name: "{app}\WindowsBase.dll"
Type: files; Name: "{app}\netstandard.dll"
Type: files; Name: "{app}\ReachFramework.dll"
Type: files; Name: "{app}\DirectWriteForwarder.dll"
Type: files; Name: "{app}\D3DCompiler_47_cor3.dll"
Type: files; Name: "{app}\PenImc_cor3.dll"
Type: files; Name: "{app}\vcruntime140.dll"
Type: files; Name: "{app}\vcruntime140_1.dll"
Type: files; Name: "{app}\msvcp140.dll"
Type: files; Name: "{app}\concrt140.dll"

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

// round7 r2 缺陷（混合形态产物）的运行期兜底：[Files] 拷贝完成后检查 {app}\Beautiful.runtimeconfig.json
// 的形态——没有 includedFrameworks 段（framework-dependent，或"FDD runtimeconfig + app-local host
// + partial runtime 件"的混合形态）时，app-local hostfxr/hostpolicy 会把框架查找根锁死在应用
// 目录，应用目录没有全套 runtime 就双击必死（Required: Microsoft.NETCore.App 10.0.0 (x64)，
// DOTNET_ROOT 也不生效）。此时摘掉 app-local host 三件（hostfxr/hostpolicy/nethost），应用改走
// 系统 fxr——装有 .NET Desktop Runtime 10 的机器即可正常启动；混合产物里残留的 coreclr 等运行
// 时件没有 hostfxr 加载链入口，不会被加载，留着无害。自包含产物（includedFrameworks 在位）的
// host 文件是启动链必需件，原样保留。打包入口的编译期门禁见 scripts\build-installer.cmd——
// 混合产物在那里就该被拦下，这里是最后一道保险。
procedure RemoveAppLocalHostForFdd;
var
  cfgFile: String;
  lines: TArrayOfString;
  i: Integer;
  selfContained: Boolean;
  hostFile: String;
begin
  cfgFile := ExpandConstant('{app}\Beautiful.runtimeconfig.json');
  if not FileExists(cfgFile) then Exit;
  selfContained := False;
  if LoadStringsFromFile(cfgFile, lines) then
    for i := 0 to GetArrayLength(lines) - 1 do
      if Pos('includedFrameworks', lines[i]) > 0 then
      begin
        selfContained := True;
        Break;
      end;
  if selfContained then Exit;
  for i := 0 to 2 do
  begin
    case i of
      0: hostFile := ExpandConstant('{app}\hostfxr.dll');
      1: hostFile := ExpandConstant('{app}\hostpolicy.dll');
      2: hostFile := ExpandConstant('{app}\nethost.dll');
    end;
    if FileExists(hostFile) then
    begin
      DeleteFile(hostFile);
      Log(Format('FDD runtimeconfig: removed app-local %s', [hostFile]));
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 覆盖安装：旧实例还开着就会锁住 exe/dll，先请它退出
  if CurStep = ssInstall then
    AskRunningInstanceToExit(ExpandConstant('{app}\Beautiful.exe'));
  if CurStep = ssPostInstall then
    RemoveAppLocalHostForFdd;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    AskRunningInstanceToExit(ExpandConstant('{app}\Beautiful.exe'));
end;
