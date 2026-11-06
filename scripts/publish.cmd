@echo off
setlocal
rem scripts\publish.cmd -- 自包含发布到 <repo>\publish\（CI release.yml 用同一组参数）。
rem 仓库根 = 本脚本上一级：脚本随仓库走，不写死盘符（旧版写死 E:\Beautiful，仓库搬家后即失效）。
set "REPO_ROOT=%~dp0.."
set "NUGET_PACKAGES=%REPO_ROOT%\.nuget"
if exist "%REPO_ROOT%\.dotnet\dotnet.exe" set "DOTNET_ROOT=%REPO_ROOT%\.dotnet"
if defined DOTNET_ROOT set "PATH=%DOTNET_ROOT%;%PATH%"
cd /d "%REPO_ROOT%"

dotnet publish src\Beautiful.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%REPO_ROOT%\publish"
if errorlevel 1 exit /b 1

rem 产物自检（与 install-local.cmd 同一套）：坏产物宁可失败也不要发出去。
rem 事故背景：bin\Release 构建输出被手工盖拷进发布目录，会把 runtimeconfig 退回 framework-dependent
rem 形态（"frameworks"），安装版双击即报 "No frameworks were found"。
findstr /C:"includedFrameworks" "%REPO_ROOT%\publish\Beautiful.runtimeconfig.json" >nul 2>nul
if errorlevel 1 goto badartifact
findstr /C:"runtimepack" "%REPO_ROOT%\publish\Beautiful.deps.json" >nul 2>nul
if errorlevel 1 goto badartifact
if not exist "%REPO_ROOT%\publish\hostfxr.dll" goto badartifact

echo 完成：%REPO_ROOT%\publish\Beautiful.exe
exit /b 0

:badartifact
echo [错误] 发布产物自检失败：runtimeconfig/deps.json 不是自包含形态（缺 includedFrameworks/runtimepack）或缺 hostfxr.dll。
echo        这样的产物双击启动会报 "No frameworks were found"。请确认发布命令带 --self-contained true，
echo        且发布目录干净——不要把 bin\Release 构建输出手工拷进发布目录。
exit /b 1
