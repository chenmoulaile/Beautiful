@echo off
setlocal EnableExtensions
chcp 65001 >nul
cd /d "%~dp0..\.."
call scripts\build-native.cmd Release
if errorlevel 1 exit /b 1
tools\NativeSmoke\bin\Release\NativeSmoke.exe src\Beautiful.Native\bin\Release\Beautiful.Native.dll
echo.
echo 把以上整段输出发回；同时可附 logs\native.log 与 %TEMP%\Beautiful.TrayBridge.log。
pause
