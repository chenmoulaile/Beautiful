@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0.."
set "CONFIG=%1"
if "%CONFIG%"=="" set "CONFIG=Release"

rem ---------------------------------------------------------------------------
rem VS 定位（CI / 本机通用）。
rem 【为什么不能只认 BuildTools 路径】windows-latest 镜像预装的是 VS2022 Enterprise，
rem 没有 BuildTools SKU——v4 系 alpha 的 release CI 自 alpha.3 起全部红在这一步
rem （脚本旧版只探 %ProgramFiles(x86)%\...\2022\BuildTools，找不到就走 :novs 退出 1）。
rem 唯一可靠的定位器是 vswhere（镜像自带，路径固定），没有时再按常见 SKU 逐个探测。
rem ---------------------------------------------------------------------------
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSPATH="
if exist "%VSWHERE%" (
    for /f "usebackq tokens=* delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
)
if not defined VSPATH (
    for %%S in (BuildTools Enterprise Professional Community) do (
        if not defined VSPATH if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\%%S\VC\Auxiliary\Build\vcvars64.bat" set "VSPATH=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\%%S"
    )
)
if not defined VSPATH (
    echo [native] 未找到带 C++ 工具集（VC.Tools.x86.x64）的 Visual Studio 2022。
    echo [native] vswhere=%VSWHERE% 存在性见上一节探测；CI 镜像若变更请更新探测列表。
    exit /b 1
)
echo [native] VS: %VSPATH%
call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
    echo [native] vcvars64.bat 初始化失败。
    exit /b 1
)
set "MSBUILD=%VSPATH%\MSBuild\Current\Bin\MSBuild.exe"
if not exist "%MSBUILD%" (
    echo [native] MSBuild.exe 不存在：%MSBUILD%
    exit /b 1
)

echo [native] Beautiful.Native %CONFIG% x64
"%MSBUILD%" "src\Beautiful.Native\Beautiful.Native.vcxproj" /p:Configuration=%CONFIG% /p:Platform=x64 /m /v:minimal /nologo
if errorlevel 1 exit /b 1

echo [native] Beautiful.TrayBridge %CONFIG% x64
"%MSBUILD%" "src\Beautiful.TrayBridge\Beautiful.TrayBridge.vcxproj" /p:Configuration=%CONFIG% /p:Platform=x64 /m /v:minimal /nologo
if errorlevel 1 exit /b 1

echo [native] NativeSmoke %CONFIG% x64
"%MSBUILD%" "tools\NativeSmoke\NativeSmoke.vcxproj" /p:Configuration=%CONFIG% /p:Platform=x64 /m /v:minimal /nologo
if errorlevel 1 exit /b 1

echo [native] done.
exit /b 0
