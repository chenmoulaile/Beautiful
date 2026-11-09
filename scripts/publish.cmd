@echo off
setlocal
rem scripts\publish.cmd -- 自包含发布到 <repo>\publish\（CI release.yml 用同一组参数）。
rem 仓库根 = 本脚本上一级：脚本随仓库走，不写死盘符（旧版写死 E:\Beautiful，仓库搬家后即失效）。
rem 本文件与 install-local.cmd 以 GBK 存储（中文 Windows cmd 的 936 原生码页）：UTF-8 中文
rem 注释在 936 控制台会被双字节解析吞行，UTF-8+chcp 65001 又踩 cmd 批处理读取器的偏移 bug
rem （round7 实测：长中文 rem 被拆碎成未知命令）——批处理脚本存 GBK 是唯一两头都稳的形态。
set "REPO_ROOT=%~dp0.."
set "NUGET_PACKAGES=%REPO_ROOT%\.nuget"
rem 仓库内置 SDK（.dotnet）当年为"不占 C 盘"设为优先；net10 起 global.json 锁 10.0.100
rem （rollForward=latestFeature），内置目录若还停在 8.0.x 就满足不了 global.json——先探全局
rem PATH 有没有 10.x SDK，有就走全局；没有才指内置（内置也不满足时 SDK 解析会大声失败，
rem 绝不静默用旧 SDK 构建错版产物）。
set "GLOBAL_NET10="
where dotnet >nul 2>nul && (dotnet --list-sdks 2>nul | findstr /B /C:"10." >nul) && set "GLOBAL_NET10=1"
if not defined GLOBAL_NET10 if exist "%REPO_ROOT%\.dotnet\dotnet.exe" set "DOTNET_ROOT=%REPO_ROOT%\.dotnet"
if defined DOTNET_ROOT set "PATH=%DOTNET_ROOT%;%PATH%"
cd /d "%REPO_ROOT%"

rem 发布目录先清空再发布：dotnet publish 只做同名覆盖，旧形态残留（上一轮 self-contained 的
rem runtime 件、手工试验拷进来的 bin 输出）会整批混进 zip/安装包——round7 真机缺陷 1 的
rem “8.0 hostfxr + net10 应用文件”混装即此来源。与 install-local.cmd 对 dist\Beautiful 的
rem “先 rmdir 后发布”同一纪律。清不掉（旧实例还在跑锁住文件）就大声失败，绝不带残发布。
if exist "%REPO_ROOT%\publish" rmdir /s /q "%REPO_ROOT%\publish"
if exist "%REPO_ROOT%\publish" (
    echo [错误] %REPO_ROOT%\publish 清理失败：目录被占用（是否有 Beautiful.exe 正在运行？）。
    exit /b 1
)

dotnet publish src\Beautiful.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%REPO_ROOT%\publish"
if errorlevel 1 exit /b 1

rem 产物自检（与 install-local.cmd 同一套）：坏产物宁可失败也不要发出去。
rem 事故背景：bin\Release 构建输出被手工盖拷进发布目录，会把 runtimeconfig 退回
rem framework-dependent 形态，安装版双击即报 No frameworks were found。
findstr /C:"includedFrameworks" "%REPO_ROOT%\publish\Beautiful.runtimeconfig.json" >nul 2>nul
if errorlevel 1 goto badartifact
findstr /C:"runtimepack" "%REPO_ROOT%\publish\Beautiful.deps.json" >nul 2>nul
if errorlevel 1 goto badartifact
if not exist "%REPO_ROOT%\publish\hostfxr.dll" goto badartifact
rem round7 r3: write SC-STAMP.txt (self-contained shape marker) for the iss compile-time gate.
> "%REPO_ROOT%\publish\SC-STAMP.txt" echo self-contained %DATE% %TIME%

echo 完成：%REPO_ROOT%\publish\Beautiful.exe
exit /b 0

:badartifact
echo [错误] 发布产物自检失败：runtimeconfig/deps.json 不是自包含形态（缺 includedFrameworks/runtimepack）或缺 hostfxr.dll。
echo        这样的产物双击启动会报 "No frameworks were found"。请确认发布命令带 --self-contained true，
echo        且发布目录干净——不要把 bin\Release 构建输出手工拷进发布目录。
exit /b 1
