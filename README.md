# Beautiful

Windows 11 浮动停靠栏（floating dock）桌面应用。WPF + .NET 8，自包含发布，中文界面。

> 截图占位：QA 通过后补充真实截图（计划存放于 `docs/screenshots/`）。

Beautiful 在屏幕底部提供一个 macOS 风格的悬浮 Dock，替代/接管原生任务栏的日常角色：应用固定与启动、悬停高斯放大动画、正在播放的媒体胶囊与双语歌词。原生任务栏可选择完全隐藏、自动隐藏或保留。

## 功能特性

- **悬浮 Dock**
  - AppBar 注册，常驻屏幕底部；左下角为固定应用，右侧为媒体胶囊
  - 悬停高斯放大动画（macOS 风格，逐项放大幅与影响半径）
  - 右键固定/取消固定应用，固定清单持久化于 `config/dock.json`
- **三套主题**（默认鸿蒙）
  - **鸿蒙**：深色毛玻璃胶囊、squircle（超椭圆）图标、#0A59F7 强调色
  - **macOS**：悬停高斯放大（macOS 风格）
  - **Linux GNOME**：实底左条布局
- **原生任务栏接管**（三档策略，默认隐藏）
  - `隐藏`：先释放任务栏的底部条带预留（`ABM_SETSTATE(ABS_AUTOHIDE|ABS_ALWAYSONTOP)`，Win11 实测唯一有效手段），再把任务栏窗口移出屏幕（被 shell 回摆时降级整窗隐藏兜底）——**Dock 与屏幕真底边 0px 间隙**；AppBar 注册后 0.8s/3s 各重申一次，退出或切换策略时完整还原（含写回改动前的原始状态位）
  - `自动隐藏`：调用系统级自动隐藏（SHAppBarMessage），同样释放条带 → Dock 贴真底
  - `保留`：不动原生任务栏，Dock 让位在任务栏上方
  - explorer 重启自愈：监听 `TaskbarCreated` 广播后自动重新应用当前策略
- **媒体小组件**
  - 基于 SMTC（System Media Transport Controls）自动发现播放会话——Spotify、网易云音乐（UWP/注册了 SMTC 的客户端）、主流浏览器、Apple Music 等即插即用，无需逐个适配
  - 实况窗胶囊：Dock 上常驻一条迷你胶囊，显示封面/标题与播放状态
  - 展开面板：大封面、可拖动进度条、上一首/播放暂停/下一首
  - **双语歌词**：网易云 `tlyric` 为主源，LRCLIB 兜底，两者皆无时降级为"暂无歌词"；支持 ±2000ms 对齐偏移；歌词按曲目 JSON 缓存（`cache/`）
- **设置窗口**：主题、任务栏策略、开机自启（HKCU Run）、歌词偏移、媒体胶囊开关、面板失焦自动收起、固定应用管理
- **壳层**：单实例（第二实例提示已在运行后退出）、托盘菜单（设置/主题/重载 Dock/退出，双击图标 = 设置）、日志（`logs/`）、全局异常兜底

## 系统要求

- **使用**：Windows 11。无需安装 .NET 8 Desktop Runtime——发布为自包含（self-contained），运行时已随包附带。
- **构建**：.NET 8 SDK（仅构建需要，运行发布产物不需要）。

## 构建

```bash
git clone https://github.com/chenmoulaile/Beautiful.git
cd Beautiful
```

仓库结构（发布前与发布后一致）：

```
Beautiful.sln
├── src/Beautiful.Core/      # 无 UI 核心库：歌词解析/双语对齐/时间轴/主题定义/设置模型（net8.0，可单测）
├── src/Beautiful.App/       # WPF 壳层：Dock/AppBar、主题、媒体小组件、设置、托盘（net8.0-windows）
├── tests/Beautiful.Core.Tests/  # Core 单元测试（69 项）
├── scripts/                 # env.sh / publish.cmd / shot.ps1
├── docs/                    # 设计文档
├── publish/                 # 发布产物（gitignore，由 publish.cmd 生成）
└── THIRD-PARTY-NOTICES.md
```

**方式一：发布脚本（本机环境）**

```cmd
scripts\publish.cmd
```

脚本内部使用仓库自带的 SDK 与 NuGet 缓存（`.dotnet/`、`.nuget/`，均已 gitignore，不随仓库分发，且路径按本机布局硬编码为 `E:\Beautiful`）。如果你的环境没有这两个目录或仓库位于其他位置，请安装 .NET 8 SDK 后使用方式二。

**方式二：手动 dotnet 命令（通用）**

```bash
dotnet publish src/Beautiful.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
```

产物输出在 `publish/`（约 200MB，自包含运行时占大头），入口为 `publish\Beautiful.exe`。

**开发构建与测试**（Git Bash / WSL，使用仓库内置 SDK 时）：

```bash
. ./scripts/env.sh && dotnet build
. ./scripts/env.sh && dotnet test tests/Beautiful.Core.Tests
```

## 使用说明

- **首次启动**：双击 `publish\Beautiful.exe`。Dock 出现在屏幕底部（默认任务栏策略为"隐藏"，可在设置中更改）；系统托盘出现 Beautiful 图标（主题强调色圆点）。
- **托盘菜单**：设置 / 主题（鸿蒙、macOS、Linux 子菜单）/ 重载 Dock / 退出；双击托盘图标直接打开设置。
- **设置窗口**：切换主题与任务栏策略、开机自启、歌词偏移（-2000..2000ms，实时生效）、媒体胶囊显隐、展开面板失焦是否自动收起、固定应用管理。
- **固定应用**：右键 Dock 图标选择"固定/取消固定"；固定清单保存在 `config/dock.json`，也可直接编辑该文件（应用列表）。
- **歌词说明**：媒体胶囊依赖播放器注册 SMTC 会话。双语歌词优先取网易云翻译（tlyric），取不到时回退 LRCLIB（lrclib.net），两者皆无时显示"暂无歌词"；歌词结果按曲目缓存于 `cache/`。歌词偏移可在设置中微调。
- **日志排查**：运行日志位于 `logs/`，包含启动流程、会话发现与异常记录。

## 已知限制

1. **QQ音乐 Win32 客户端与 PotPlayer 不注册 SMTC**，因此不会出现媒体胶囊（QQ 音乐 Win32、PotPlayer 均未接入系统媒体控制）。
2. **网易云音乐 Win32 客户端需 SMTC 桥接插件**（如 [inflink-rs](https://github.com/apoint123/inflink-rs)）才会注册会话；注册后即可即插即用。
3. **网易云接口为非官方接口**，仅供个人使用，不保证可用性；请勿用于商业用途。
4. **悬停放大动画与部分主题视觉在受限环境下未逐一实机验证**（多显示器缩放、不同 DPI 等），效果可能因机器而异。
5. **任务栏"隐藏"策略实测结论**（build 26200 / 1024×768@100%，`scripts/strip-probe.ps1` 可复测）：
   (a) **无间隙——Dock 与屏幕真底边 0px**。实现手段是 `ABM_SETSTATE(ABS_AUTOHIDE|ABS_ALWAYSONTOP)`：这是本机实测**唯一**能释放任务栏底部条带预留的公开 API（`ABM_QUERYPOS(ABE_BOTTOM)` 的可用底边由 `720` 变 `768`，工作区底部缺口由 `146px` 变 `98px`＝Dock 条带自身高度）。
   曾实测否决的方案：对 `Shell_TrayWnd` 发 `ABM_REMOVE` 返回 `1`(TRUE) 但预留**不释放**（QUERYPOS 仍返回 `screenH-48`）；把任务栏窗口移出屏幕（`(0,768)-(1024,816)`）或 `SW_HIDE` 同样不释放——**Win11 shell 的底部条带不挂在 appbar 注册表上**，所以"解除注册"这条路在本机无效，未采用。
   autohide 的副作用（光标顶到屏幕最底行会把任务栏滑出，实测即使 Dock 的 topmost 条带盖住触发区也照样滑出）由随后"把窗口请出屏幕"消除：实测悬停底边 3.7s 任务栏不再滑出，且最底 4 行像素与 Dock 背板同色、无任务栏残条。
   (b) 本进程被**强杀**时：任务栏窗口由 explorer 自行摆回屏幕内（实测 `(0,720)-(1024,768)`），但系统 autohide 开关会留在"开"——表现为任务栏缩到屏幕外、**把指针顶到屏幕最底行即可唤出**（不是卡死，无需重启 `explorer.exe`）；再次启动 Beautiful 并正常退出/切到"保留"即清掉该开关（Hide 策略落地时无条件清一次 autohide 位）。代价：若用户自己的原生 autohide 偏好恰好为"开"，退出 Beautiful 会把它一并清掉，可在 系统设置 › 个性化 › 任务栏 › "自动隐藏任务栏" 重开。
   (c) 隐藏策略下原生托盘随任务栏一并不可见，托盘菜单（含"退出"）不可达——正常退出需先经设置窗口把任务栏策略切回"保留"（托盘随之恢复可见，再经托盘"退出"），或直接结束本进程（行为见 (b)）；设置窗口可在启动时以 `--settings` 参数打开。

## 许可证

本项目以 [MIT](LICENSE) 许可证开源。

第三方组件、设计资源与数据来源的许可与致谢见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 借鉴来源

| 项目 | 许可证 | 借鉴内容 |
| --- | --- | --- |
| [TheAgencyMGE/aero-dock](https://github.com/TheAgencyMGE/aero-dock) | MIT | 悬浮 Dock 实现模式参考（未复制代码） |
| [ModernFlyouts-Community/ModernFlyouts](https://github.com/ModernFlyouts-Community/ModernFlyouts) | MIT | GSMTC 媒体会话管理模式、`SetWindowCompositionAttribute` 亚克力用法 |
| [wwh1004/NeteaseCloudMusicApi](https://github.com/wwh1004/NeteaseCloudMusicApi) | MIT | 网易云歌词接口端点用法 |
| [tranxuanthang/lrclib](https://github.com/tranxuanthang/lrclib) | MIT | LRCLIB 开放 API 协议 |
| [dososo/HarmonyOS-Design](https://github.com/dososo/HarmonyOS-Design) | Apache-2.0 | 鸿蒙主题设计 token |

以下 GPL 项目仅借鉴思路与行为参照，**未复制任何代码**：

- [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB)（GPL-3.0）
- [KDE Latte Dock](https://invent.kde.org/plasma/latte-dock)（GPL-2.0+）
- [Plank](https://github.com/ricotz/plank)（GPL-3.0）
- [ExplorerPatcher](https://github.com/valinet/ExplorerPatcher)（GPL-2.0）
- [Windhawk taskbar-styler](https://github.com/ramensoftware/windows-11-taskbar-styling-guide)（GPL-3.0）

数据来源：[网易云音乐](https://music.163.com/)接口（非官方，仅个人使用）、[LRCLIB](https://lrclib.net/)。

HarmonyOS Sans 字体与 HarmonyOS Symbol 图标为华为专有授权，声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)（本项目当前未嵌入字体文件）。
