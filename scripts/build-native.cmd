@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0.."
rem ── VS 探测（2026-10-31 修，第二版）：GitHub windows-latest 已迁到 windows-2025-vs2026
rem ── 镜像（VS 2026 Enterprise 在 %ProgramFiles%\Microsoft Visual Studio\18\Enterprise，
rem ── 注意是 Program Files 不是 Program Files(x86)），旧脚本只认 BuildTools 硬编码路径必然失败。
rem ── 顺序：BEAUTIFUL_VS_PATH 覆盖 → 逐个候选路径直查 vcvars64.bat → vswhere 兜底。
set "VS="
set "CONFIG=%1"
if "%CONFIG%"=="" set "CONFIG=Release"

if defined BEAUTIFUL_VS_PATH if exist "%BEAUTIFUL_VS_PATH%\VC\Auxiliary\Build\vcvars64.bat" set "VS=%BEAUTIFUL_VS_PATH%"
if defined VS goto found

for %%P in (
    "%ProgramFiles%\Microsoft Visual Studio\18\Enterprise"
    "%ProgramFiles%\Microsoft Visual Studio\18\Community"
    "%ProgramFiles%\Microsoft Visual Studio\18\Professional"
    "%ProgramFiles%\Microsoft Visual Studio\18\BuildTools"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Enterprise"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community"
    "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Professional"
) do (
    if not defined VS if exist "%%~P\VC\Auxiliary\Build\vcvars64.bat" set "VS=%%~P"
)
if defined VS goto found

rem ── 兜底：vswhere（经典安装器路径）。输出原样回显，便于失败时诊断。
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    echo [native] vswhere 兜底探测：
    "%VSWHERE%" -latest -products * -property installationPath
    for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -property installationPath 2^>nul`) do (
        if not defined VS if exist "%%~i\VC\Auxiliary\Build\vcvars64.bat" set "VS=%%~i"
    )
)
if defined VS goto found

echo [native] MSVC not found. Probed:
echo   - BEAUTIFUL_VS_PATH=%BEAUTIFUL_VS_PATH%
echo   - %%ProgramFiles%%\Microsoft Visual Studio\18\{Enterprise,Community,Professional,BuildTools}
echo   - %%ProgramFiles(x86)%%\Microsoft Visual Studio\2022\{BuildTools,Enterprise,Community,Professional}
echo   - vswhere: %VSWHERE%
exit /b 1

:found
echo [native] Visual Studio: %VS%
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
set "MSBUILD=%VS%\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo [native] MSBuild.exe not found at %MSBUILD%
    exit /b 1
)

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
