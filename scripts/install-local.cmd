@echo off
setlocal EnableExtensions
chcp 65001 >nul

rem scripts\install-local.cmd -- publish Beautiful into a fixed runnable folder:
rem   <repo>\dist\Beautiful\Beautiful.exe   (self-contained win-x64)
rem Double-click to run; re-running wipes dist\Beautiful first (idempotent).
rem Publish arguments are copied verbatim from .github\workflows\release.yml
rem ("发布自包含产物"), only -o differs, so dist\Beautiful and the Release zip
rem are the same artifact.

cd /d "%~dp0.."
set "REPO_ROOT=%CD%"
set "OUT=%REPO_ROOT%\dist\Beautiful"

rem Repo-local SDK + NuGet cache stay on this drive (never C:)
if exist "%REPO_ROOT%\.dotnet\dotnet.exe" set "DOTNET_ROOT=%REPO_ROOT%\.dotnet"
if defined DOTNET_ROOT set "PATH=%DOTNET_ROOT%;%PATH%"
set "NUGET_PACKAGES=%REPO_ROOT%\.nuget"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"

where dotnet >nul 2>nul
if errorlevel 1 goto nodotnet

echo [1/4] 清理旧目录：%OUT%
if exist "%OUT%" rmdir /s /q "%OUT%"
if not exist "%REPO_ROOT%\dist" mkdir "%REPO_ROOT%\dist"

echo [2/4] 编译 native（VC BuildTools v143；缺失则继续纯 C# 形态）...
call "%REPO_ROOT%\scripts\build-native.cmd" Release
if errorlevel 1 echo [warn] native 编译失败：继续发布纯 C# 产物（BEAUTIFUL_NONATIVE 形态）

echo [3/4] 正在发布（自包含 win-x64，约 1-3 分钟）...
dotnet publish "src\Beautiful.App" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseAppHost=true -o "%OUT%"
if errorlevel 1 goto fail

if not exist "%OUT%\Beautiful.exe" goto missing
for %%F in ("%OUT%\Beautiful.exe") do set "EXE_SIZE=%%~zF"
echo [4/4] 发布完成。
echo.
echo 真正可运行的软件目录：%OUT%
echo 入口程序：           %OUT%\Beautiful.exe
echo Beautiful.exe 大小： %EXE_SIZE% 字节
echo 自包含产物，不需要再安装 .NET 运行时，双击即可运行。
echo.
pause
exit /b 0

:nodotnet
echo [错误] 找不到 dotnet：请安装 .NET 8 SDK，或把仓库内置 SDK 放到 %REPO_ROOT%\.dotnet\
pause
exit /b 1

:fail
echo [错误] dotnet publish 失败，请查看上面的 dotnet 输出。
pause
exit /b 1

:missing
echo [错误] 发布流程结束但没有找到 %OUT%\Beautiful.exe
pause
exit /b 1
