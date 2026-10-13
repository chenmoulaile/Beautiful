# Beautiful

Windows 11 浮动停靠栏（floating dock）桌面应用。WPF + .NET 8。

## 环境准备

仓库自带 .NET 8 SDK（`.dotnet/`，系统无需安装 dotnet）。执行任何 dotnet 命令前，先在仓库根目录 source 环境脚本：

```bash
. ./scripts/env.sh && dotnet restore && dotnet build
```

该脚本会将内置 SDK 加入 `PATH`，并把 NuGet 包缓存重定向到仓库内 `.nuget/`（不占用 C 盘）。

## 目录结构

- `src/Beautiful.Core` — 核心逻辑（与 UI 无关）
- `src/Beautiful.App` — WPF 应用入口（TFM `net8.0-windows10.0.19041.0`）
- `tests/Beautiful.Core.Tests` — Core 单元测试（xunit）
- `scripts/env.sh` — dotnet 环境引导脚本

## 测试

```bash
. ./scripts/env.sh && dotnet test tests/Beautiful.Core.Tests
```

## 许可

MIT，见 [LICENSE](LICENSE)。
