@echo off
setlocal
rem scripts\build-installer.cmd -- 自包含 publish + 产物形态自检 + Inno Setup 打安装包（一键入口）。
rem 仓库根 = 本脚本上一级：脚本随仓库走，不写死盘符。
rem 依据（round7 r2 真机缺陷）：dist\Beautiful 曾被非 publish 流程盖成"FDD runtimeconfig +
rem app-local host + partial runtime 件"混合形态，而 Beautiful.iss 的默认 SourceDir 就是
rem dist\Beautiful——手工 ISCC 把混合产物打进安装包，装完双击即死（Required:
rem Microsoft.NETCore.App 10.0.0，app-local hostfxr 把框架查找根锁死在应用目录）。
rem 本脚本把 publish、形态自检、ISCC 串成一条带门禁的链：混合产物在打包前就被拦下，
rem 保证打出来的安装包要么完整自包含（host 是启动链必需件），要么纯 FDD（不带 host，
rem 由安装器的 ssPostInstall 兜底摘除——见 Beautiful.iss [Code]）。
rem 本文件以 GBK 存储（与 publish.cmd 同纪律：UTF-8 中文注释在 cmd 批处理里两头不稳）。
set "REPO_ROOT=%~dp0.."
set "NUGET_PACKAGES=%REPO_ROOT%\.nuget"
rem 全局 PATH 有 10.x SDK 就走全局（global.json 锁 10.0.100；内置 .dotnet 若还是 8.0.x 满足不了）
set "GLOBAL_NET10="
where dotnet >nul 2>nul && (dotnet --list-sdks 2>nul | findstr /B /C:"10." >nul) && set "GLOBAL_NET10=1"
if not defined GLOBAL_NET10 if exist "%REPO_ROOT%\.dotnet\dotnet.exe" set "DOTNET_ROOT=%REPO_ROOT%\.dotnet"
if defined DOTNET_ROOT set "PATH=%DOTNET_ROOT%;%PATH%"
set "APP_VERSION=%~1"
if "%APP_VERSION%"=="" set "APP_VERSION=5.1.0"
set "OUT=%REPO_ROOT%\dist\Beautiful"
set "ISCC=%REPO_ROOT%\.tools\innosetup\ISCC.exe"
if not exist "%ISCC%" set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" goto noiscc

echo [1/3] 清理并发布自包含产物：%OUT%
if exist "%OUT%" rmdir /s /q "%OUT%"
if exist "%OUT%" goto locked
dotnet publish "src\Beautiful.App" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseAppHost=true -o "%OUT%"
if errorlevel 1 goto publishfail

echo [2/3] 产物形态自检（自包含三件套 + 全框架件）
findstr /C:"includedFrameworks" "%OUT%\Beautiful.runtimeconfig.json" >nul 2>nul
if errorlevel 1 goto badartifact
findstr /C:"runtimepack" "%OUT%\Beautiful.deps.json" >nul 2>nul
if errorlevel 1 goto badartifact
for %%F in (hostfxr.dll coreclr.dll System.Private.CoreLib.dll) do if not exist "%OUT%\%%F" goto badartifact
rem round7 r3: write SC-STAMP.txt (self-contained shape marker) for the iss compile-time gate.
> "%OUT%\SC-STAMP.txt" echo self-contained %DATE% %TIME%

echo [3/3] 打安装程序（Inno Setup）
"%ISCC%" /DAppVersion=%APP_VERSION% /DSourceDir="%OUT%" /DOutputDir="%REPO_ROOT%\installer\out" "%REPO_ROOT%\installer\Beautiful.iss"
if errorlevel 1 goto isccfail

echo 完成：安装包在 %REPO_ROOT%\installer\out\Beautiful-Setup-v%APP_VERSION%.exe
echo dist\Beautiful 是同一件自包含产物，可直接运行或压缩为 zip。
exit /b 0

:locked
echo [错误] %OUT% 清理失败：目录被占用（是否有 Beautiful.exe 正在运行？）。
exit /b 1

:publishfail
echo [错误] dotnet publish 失败，请查看上面的 dotnet 输出。
exit /b 1

:badartifact
echo [错误] 发布产物形态自检失败：runtimeconfig 不是自包含（缺 includedFrameworks）、
echo        deps.json 缺 runtimepack，或缺 hostfxr/coreclr/System.Private.CoreLib——
echo        这是"FDD runtimeconfig + app-local host"混合形态（round7 r2 真机缺陷），
echo        装完双击必死。排查：本脚本必须自己 publish（--self-contained true -r win-x64），
echo        绝不要把 bin\Release 构建输出或 lite 产物拷进 %OUT%。
exit /b 1

:isccfail
echo [错误] ISCC 失败（退出码 %errorlevel%）。
exit /b 1

:noiscc
echo [错误] 找不到 ISCC.exe：仓库本地 %REPO_ROOT%\.tools\innosetup\ 或 C:\Program Files (x86)\Inno Setup 6\ 均不存在。
exit /b 1
