@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0.."
set "CONFIG=%1"
if "%CONFIG%"=="" set "CONFIG=Release"

rem ---------------------------------------------------------------------------
rem VS 定位（CI / 本机通用）。
rem 【为什么不能只认 BuildTools 路径】windows-latest 镜像预装的是 VS2022 Enterprise，
rem 没有 BuildTools SKU——v4 系 alpha 的 release CI 自 alpha.3 起全部红在这一步。
rem 【批处理陷阱】%ProgramFiles(x86)% 的值含括号，绝不能以 %% 展开形式出现在
rem if/for 的括号块或 for 的括号组里（解析时会把块/组截断，报
rem "\Microsoft was unexpected at this time"）。所以本脚本：
rem ① 含路径的变量一律用延迟展开 !VAR!（执行期展开，不参与解析）；
rem ② 控制流用 goto 线性跳转，不用含路径展开的括号块。
rem ---------------------------------------------------------------------------
set "PF86=%ProgramFiles(x86)%"
set "VSWHERE=!PF86!\Microsoft Visual Studio\Installer\vswhere.exe"
set "VSPATH="

if exist "!VSWHERE!" for /f "usebackq tokens=* delims=" %%i in (`"!VSWHERE!" -latest -products * -property installationPath`) do set "VSPATH=%%i"
if defined VSPATH goto havevs

if exist "%PF86%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" set "VSPATH=%PF86%\Microsoft Visual Studio\2022\BuildTools"
if defined VSPATH goto havevs
if exist "%PF86%\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat" set "VSPATH=%PF86%\Microsoft Visual Studio\2022\Enterprise"
if defined VSPATH goto havevs
if exist "%PF86%\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat" set "VSPATH=%PF86%\Microsoft Visual Studio\2022\Professional"
if defined VSPATH goto havevs
if exist "%PF86%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" set "VSPATH=%PF86%\Microsoft Visual Studio\2022\Community"
if defined VSPATH goto havevs

echo [native] 未找到带 C++ 工具集的 Visual Studio 2022^(vswhere 与四个 SKU 都试过^)。
echo [native] vswhere=%VSWHERE%
exit /b 1

:havevs
echo [native] VS: !VSPATH!
call "!VSPATH!\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 (
    echo [native] vcvars64.bat 初始化失败。
    exit /b 1
)
set "MSBUILD=!VSPATH!\MSBuild\Current\Bin\MSBuild.exe"
if not exist "!MSBUILD!" (
    echo [native] MSBuild.exe 不存在：!MSBUILD!
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
