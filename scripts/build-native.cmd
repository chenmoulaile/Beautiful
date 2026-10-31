@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0.."
rem ── VS 探测（2026-10-31 修）：旧版只认 %ProgramFiles(x86)%\2022\BuildTools 一个硬编码路径，
rem ── GitHub windows-latest 装的是 2022\Enterprise、本地可能是 BuildTools/Community/Professional。
rem ── 顺序：环境变量覆盖 → vswhere（带 VC 工具链的最新安装）→ 旧硬编码路径兜底。
set "VS="
if defined BEAUTIFUL_VS_PATH if exist "%BEAUTIFUL_VS_PATH%\VC\Auxiliary\Build\vcvars64.bat" set "VS=%BEAUTIFUL_VS_PATH%"
if not defined VS (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if exist "%VSWHERE%" (
        for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2^>nul`) do set "VS=%%i"
    )
)
if not defined VS set "VS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools"
if not exist "%VS%\VC\Auxiliary\Build\vcvars64.bat" goto novs
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
set "MSBUILD=%VS%\MSBuild\Current\Bin\MSBuild.exe"
set "CONFIG=%1"
if "%CONFIG%"=="" set "CONFIG=Release"

echo [native] Visual Studio: %VS%
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
echo [native] MSVC v143 not found: BEAUTIFUL_VS_PATH 未设、vswhere 未找到带 VC 工具链的 VS、且旧默认路径不存在。
exit /b 1
