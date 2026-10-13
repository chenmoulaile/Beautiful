@echo off
setlocal
set NUGET_PACKAGES=E:\Beautiful\.nuget
set DOTNET_ROOT=E:\Beautiful\.dotnet
set PATH=%DOTNET_ROOT%;%PATH%
cd /d E:\Beautiful
dotnet publish src\Beautiful.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
echo 完成：E:\Beautiful\publish\Beautiful.exe
