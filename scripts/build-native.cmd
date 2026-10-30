@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0.."
set "VS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools"
if not exist "%VS%\VC\Auxiliary\Build\vcvars64.bat" goto novs
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
set "MSBUILD=%VS%\MSBuild\Current\Bin\MSBuild.exe"
set "CONFIG=%1"
if "%CONFIG%"=="" set "CONFIG=Release"

echo [native] Beautiful.Native %CONFIG% x64
"%MSBUILD%" "src\Beautiful.Native\Beautiful.Native.vcxproj" /p:Configuration=%CONFIG% /p:Platform=x64 /m /v:minimal
if errorlevel 1 exit /b 1

echo [native] Beautiful.TrayBridge %CONFIG% x64
"%MSBUILD%" "src\Beautiful.TrayBridge\Beautiful.TrayBridge.vcxproj" /p:Configuration=%CONFIG% /p:Platform=x64 /m /v:minimal
if errorlevel 1 exit /b 1

echo [native] NativeSmoke %CONFIG% x64
"%MSBUILD%" "tools\NativeSmoke\NativeSmoke.vcxproj" /p:Configuration=%CONFIG% /p:Platform=x64 /m /v:minimal
if errorlevel 1 exit /b 1

echo [native] done.
exit /b 0

:novs
echo [native] Visual Studio BuildTools v143 not found.
exit /b 1
