#!/usr/bin/env bash
# scripts/env.sh — dotnet 环境引导脚本
#
# 用法（在仓库根目录）：. ./scripts/env.sh && dotnet restore && dotnet build
#
# 作用：
#   1) 将仓库内置的 .NET 8 SDK（.dotnet/）加入 PATH（系统 PATH 不含 dotnet）；
#   2) 将 NuGet 全局包缓存重定向到仓库内 .nuget/，避免占用 C 盘；
#   3) 关闭 CLI 遥测与 Logo，保持构建输出干净。

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]:-$0}")/.." && pwd)"
# cygpath 缺失时（非 Git Bash / WSL 环境）退回 POSIX 路径
repo_root_win="$(cygpath -m "$repo_root" 2>/dev/null || printf '%s' "$repo_root")"

export DOTNET_ROOT="$repo_root_win/.dotnet"
export NUGET_PACKAGES="$repo_root_win/.nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export PATH="$repo_root/.dotnet:$PATH"
