# Beautiful

Windows 11 桌面美化套件：悬浮 Dock（macOS 风格）+ macOS 顶栏 + Alt+Tab 替换切换器 + 悬停实时窗口预览。WPF + .NET 8，自包含发布，中文界面。

> 界面截图不随仓库分发（`docs/` 为内部文档目录，已 .gitignore 排除）；观感以功能描述与发布包实运行为准。

Beautiful 接管日常桌面三件套：屏幕底部是悬浮 Dock（应用固定与启动、连续高斯放大、媒体胶囊与双语歌词、悬停 DWM 实时缩略图预览），屏幕顶部是 macOS 风格菜单栏（logo 菜单、前台应用名、真实"窗口"菜单、时钟/日历、音量/网络/电量、媒体迷你项），Alt+Tab 被替换为 macOS Cmd+Tab 风格的应用切换器。原生任务栏可选择完全隐藏、自动隐藏或保留。

## 功能特性

### 悬浮 Dock

- AppBar 注册常驻屏幕底部（停靠边固定为下，两条 AppBar 各自硬编码边位；**可配置停靠边为有意延后项**，见「已知限制」）；左下角为固定应用，右侧为媒体胶囊
- **连续高斯放大场**（v2 重写）：以真实物理光标坐标为输入、`CompositionTarget.Rendering`（vsync）驱动、指数阻尼收敛——动画速度只与时间有关、与帧率无关；高斯公式为 Core 纯函数（`MagnificationField`，可单测）
- **图标倒影与启动弹跳**（v2）：倒影渐隐遮罩（macOS 主题下开启），启动固定应用播放 ~600ms ElasticOut 弹跳；均可在设置开关、即时生效
- **悬停实时窗口预览**（v2）：悬停窗口条目 350ms 后弹出该窗口的 **DWM 实时缩略图**（纯 Win32 宿主窗口承载，规避 WPF airspace；缩略图句柄全路径注销不泄漏；源窗口消失/最小化自动收起）
- 条目右键菜单为自绘 FluentMenu：固定/取消固定应用，固定清单持久化于 `config/dock.json`

### macOS 顶栏（v2）

- 顶部 `ABE_TOP` AppBar 条带，与底部 Dock 两条带共存，最大化窗口自动让位
- 左区：**logo（Beautiful 应用菜单：关于/设置…/主题▸/重载 Dock/退出，全部真实动作）** + **前台应用名**（WinEventHook 跟踪）+ 文件/编辑/视图/**窗口**/帮助——"窗口"是按当前前台窗口实际执行最小化/还原/关闭的**真实窗口菜单**
- 右区：**媒体迷你**（有 SMTC 会话时显示标题，点击展开面板）· **时钟**（点击弹出日历浮层）· **音量**（点击打开系统混音器）· **网络**（联网状态指示）· **电量**（`GetSystemPowerStatus`，仅在存在电池时显示）；共用一条每分钟定时器对账，空闲零开销
- 走 Win11 系统背板（Mica/Acrylic，见下）

### Alt+Tab 替换切换器（v2）

- `WH_KEYBOARD_LL` 低阶键盘钩子拦截 Alt+Tab（热键解析层支持 `alt+tab`/`ctrl+alt+tab` 等组合，**当前设置界面固定为 Alt+Tab、不提供修改**）；同应用多窗口按 exe 归并并可**展开**逐个选择（Core 纯函数 `WindowListMerger`）
- **三样式**：macOS（居中大圆角亚克力 + 字幕）/ Linux（GNOME 实底覆盖条）/ 鸿蒙（胶囊 + 强调色）
- 按住触发键循环、松开提交、**Esc 取消**（不改变前台）；看门狗兜底，绝不留下"按住 Alt 面板不消失"的卡死态
- **全屏直通**：前台为全屏应用（游戏/独占全屏）时自动放行原生 Alt+Tab，可在设置关闭
- **应急停用 Win+Alt+K**：任何时刻按下即在进程内卸载接管、恢复原生 Alt+Tab（只认真实体键；不改设置值，重开在设置中操作）

### 菜单与背板（v2）

- **自绘 FluentMenu 统一所有菜单**：托盘右键菜单、顶栏全部菜单/浮层、Dock 条目右键——同一套 macOS 质感弹层（DWM 背板 + 激活式弹出 + 键盘可达 + DPI 锚点屏换算）
- **Win11 系统背板**：`DWMWA_SYSTEMBACKDROP_TYPE(38)`（Mica/Acrylic，22H2+）→ 未公开 `DWMWA_MICA_EFFECT(1029)` → 旧 `SetWindowCompositionAttribute` 亚克力，三级自动降级；使用背板的窗口为非分层（`AllowsTransparency=false`）+ 圆角由 `DWMWA_WINDOW_CORNER_PREFERENCE` 提供，Dock 分层点击穿透窗体仍走旧亚克力

### 三套主题（默认鸿蒙）

- **鸿蒙**：深色毛玻璃胶囊、squircle（超椭圆）图标、#0A59F7 强调色
- **macOS**：放大场 + 倒影（可开）
- **Linux GNOME**：实底左条布局

### 原生任务栏接管（三档策略，默认隐藏）

- `隐藏`：先释放任务栏的底部条带预留（`ABM_SETSTATE(ABS_AUTOHIDE|ABS_ALWAYSONTOP)`，Win11 实测唯一有效手段），再把任务栏窗口移出屏幕（被 shell 回摆时降级整窗隐藏兜底）——**Dock 与屏幕真底边 0px 间隙**；AppBar 注册后 0.8s/3s 各重申一次，退出或切换策略时完整还原（含写回改动前的原始状态位）
- `自动隐藏`：调用系统级自动隐藏（SHAppBarMessage），同样释放条带 → Dock 贴真底
- `保留`：不动原生任务栏，Dock 让位在任务栏上方
- explorer 重启自愈：监听 `TaskbarCreated` 广播后自动重新应用当前策略

### 媒体小组件

- 基于 SMTC（System Media Transport Controls）自动发现播放会话——Spotify、网易云音乐（UWP/注册了 SMTC 的客户端）、主流浏览器、Apple Music 等即插即用，无需逐个适配
- 实况窗胶囊：Dock 上常驻一条迷你胶囊，显示封面/标题与播放状态；顶栏右区亦有媒体迷你项
- 展开面板：大封面、可拖动进度条、上一首/播放暂停/下一首
- **双语歌词**：网易云 `tlyric` 为主源，LRCLIB 兜底，两者皆无时降级为"暂无歌词"；支持 ±2000ms 对齐偏移；歌词按曲目 JSON 缓存（`cache/`）

### 设置窗口（v2 重构）

- 分组：**外观**（主题/图标尺寸/放大强度/倒影/弹跳）· **系统**（任务栏策略/顶栏开关/开机自启）· **窗口切换器**（接管开关/样式/全屏直通/应急键说明——触发键当前固定 Alt+Tab，UI 只读展示）· **媒体与歌词**（胶囊/失焦收起/偏移/悬停预览）· **固定应用**
- **live 生效**：改动即时应用到运行中的界面（尺寸/强度/倒影重放、样式与背板重设、预览即关即隐），多数项无需重启进程

### 壳层

- 单实例（第二实例提示已在运行后退出）、托盘菜单（FluentMenu 自绘；设置/主题/重载 Dock/退出，双击图标 = 设置）、日志（`logs/`）、全局异常兜底

## 系统要求

- **使用**：Windows 11。无需安装 .NET 8 Desktop Runtime——发布为自包含（self-contained），运行时已随包附带。
- **构建**：.NET 8 SDK（仅构建需要，运行发布产物不需要）。

## 构建

```bash
git clone https://github.com/chenmoulaile/Beautiful.git
cd Beautiful
```

仓库结构（与发布后仓库内容一致；`docs/` 为内部规划文档，已被 .gitignore 排除、不随仓库发布）：

```
Beautiful.sln
├── src/Beautiful.Core/      # 无 UI 核心库：高斯放大场/弹跳曲线/歌词解析与双语对齐/切换器窗口归并/
│                            # 顶栏时钟格式化/主题定义/设置模型（net8.0，可单测）
├── src/Beautiful.App/       # WPF 壳层：Dock（自绘 DockSurface）/TopBar/Switcher/Preview(DWM 缩略图)/
│                            # Controls(FluentMenu)/MediaWidget/Settings/Shell(托盘)/Services/Interop
├── tests/Beautiful.Core.Tests/  # Core 单元测试（151 项）
├── scripts/                 # env.sh / publish.cmd / release.ps1 / shot.ps1 / 探针脚本
├── .github/workflows/       # release.yml（推 v* 标签云端发布）
├── publish/                 # 发布产物（gitignore，由 publish.cmd 生成）
├── Directory.Build.props    # 版本号单一来源（<Version>）
└── THIRD-PARTY-NOTICES.md
```

**开发构建与测试**（Git Bash / WSL，使用仓库内置 SDK 时）：

```bash
. ./scripts/env.sh && dotnet build
. ./scripts/env.sh && dotnet test tests/Beautiful.Core.Tests
```

**本地发布产物**：

```cmd
scripts\publish.cmd
```

脚本内部使用本机仓库自带的 SDK 与 NuGet 缓存（`.dotnet/`、`.nuget/`，均已 gitignore，不随仓库分发，且路径按本机布局硬编码为 `E:\Beautiful`）。如果你的环境没有这两个目录或仓库位于其他位置，请安装 .NET 8 SDK 后手动执行：

```bash
dotnet publish src/Beautiful.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
```

产物输出在 `publish/`（约 200MB，自包含运行时占大头），入口为 `publish\Beautiful.exe`。

## 发布（v2）

- **一键发布（本机）**：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1`
  流程：读取/改写版本号 → 工作树检查 → **构建 + 测试门禁（不绿不发布）** → 提交 `chore(release)` → 打 `v*` 标签并推送（推送失败会被退出码检出的硬失败，终审 M-3）。脚本**到此为止**——Release 创建与资产上传由 CI 独占。
- **云端发布（Release 资产唯一归属方）**：`.github/workflows/release.yml` —— 推送 `v*` 标签即由 GitHub Actions（windows-latest）执行 构建 → 测试门禁 → publish → 压缩 → 建 Release 上传资产（`Beautiful-win-x64-v<版本>.zip`）。本地与 CI 双写同名资产会互相竞态且 GitHub 拒绝重复资产名，故本地脚本不再上传（终审 M-4）。
- **版本号单一来源**：`Directory.Build.props` 的 `<Version>`；`release.ps1` 与 CI 均以它为准（`release.ps1 -Version x.y.z` 会同步改写该文件）。

## 使用说明

- **首次启动**：双击 `publish\Beautiful.exe`。顶栏出现在屏幕顶部、Dock 出现在屏幕底部（默认任务栏策略为"隐藏"，可在设置更改）；系统托盘出现 Beautiful 图标。
- **退出路径**：顶栏 logo 菜单"退出"，或托盘菜单"退出"（任务栏策略为"隐藏"时原生托盘不可见——**顶栏就是逃生入口**），或设置窗口。
- **切换器**：按住 Alt+Tab 弹出面板，同应用多窗口可展开选择，松开提交、Esc 取消。若接管行为异常，按 **Win+Alt+K** 立即应急停用（原生 Alt+Tab 当场恢复；重新启用去设置里打开开关）。
- **悬停预览**：鼠标在 Dock 窗口条目上停留 350ms 即弹出该窗口的实时缩略图；可在设置中关闭。
- **固定应用**：右键 Dock 图标选择"固定/取消固定"；固定清单保存在 `config/dock.json`，也可直接编辑该文件（应用列表）。
- **托盘菜单**：设置 / 主题（鸿蒙、macOS、Linux 子菜单）/ 重载 Dock / 退出；双击托盘图标直接打开设置。
- **歌词说明**：媒体胶囊依赖播放器注册 SMTC 会话。双语歌词优先取网易云翻译（tlyric），取不到时回退 LRCLIB（lrclib.net），两者皆无时显示"暂无歌词"；歌词结果按曲目缓存于 `cache/`。歌词偏移可在设置中微调。
- **日志排查**：运行日志位于 `logs/`，包含启动流程、会话发现、背板降级路径与异常记录。

## 已知限制

1. **QQ音乐 Win32 客户端与 PotPlayer 不注册 SMTC**，因此不会出现媒体胶囊，顶栏也没有媒体迷你项（QQ 音乐 Win32、PotPlayer 均未接入系统媒体控制）。
2. **网易云音乐 Win32 客户端需 SMTC 桥接插件**（如 [inflink-rs](https://github.com/apoint123/inflink-rs)）才会注册会话；注册后即可即插即用。
3. **网易云接口为非官方接口**，仅供个人使用，不保证可用性；请勿用于商业用途。
4. **真机复验项**：本项目的开发与验证在**不合成 DWM 背板与 DWM 缩略图**的虚拟机上进行——"真模糊"（Mica/Acrylic 系统背板的实际模糊效果）与"实时缩略图画面"（悬停预览里显示的窗口实时内容）两项在开发机上**视觉未验证**；两者的代码路径、HRESULT 判定、句柄生命周期与布局已验证（日志可查背板降级档与缩略图注册/注销计数）。需在正常合成的实机 Win11 上确认最终视觉。
5. **隐藏任务栏后原生托盘不可见**：策略为"隐藏"时托盘图标随任务栏消失，退出需经**顶栏 logo 菜单"退出"**或设置窗口（启动时可用 `--settings` 参数直接打开）——顶栏即是逃生入口。
6. **强杀进程的 autohide 残留**：本进程被强杀时任务栏窗口由 explorer 自行摆回屏幕，但系统 autohide 位可能留在"开"——表现为任务栏缩到屏幕外、**把指针顶到屏幕最底行即可唤出**（不是卡死，无需重启 `explorer.exe`）；再次启动 Beautiful 并正常退出/切"保留"即清掉（只清这一位）。若用户自身的原生 autohide 偏好恰为"开"，退出 Beautiful 会一并清掉，可在系统设置重开。
7. **多显示器为单显示器假设**：`HideAll` 把任务栏窗口摆到**主屏**底边那一行，副屏 `Shell_SecondaryTrayWnd` 会被挪到主屏上（跑错显示器），副屏条带预留是否被一并释放未实测；还原不受影响（各窗口基准矩形按句柄留存）。且 **Dock 与顶栏按构造仅覆盖主屏**——两条 AppBar 的条带几何都取 `GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)`（主屏尺寸），未按窗口所在显示器定位，多屏机器上它们只会出现/铺满主屏。多屏机器暂用"自动隐藏"或"保留"策略规避。
8. **Alt+Tab 接管的系统边界**：低级键盘钩子在 **UAC 安全桌面**（提权确认弹窗）收不到按键——此时切换器不出现、原生行为不受影响；提权窗口场景存在同类系统限制。游戏/独占全屏已做**全屏直通**（前台全屏时放行原生 Alt+Tab，可关）。如接管出现任何异常，**Win+Alt+K** 应急停用。
9. **悬停放大动画与部分主题视觉在受限环境下未逐一实机验证**（多显示器缩放、不同 DPI 等），效果可能因机器而异；放大场/弹跳/倒影/预览均有代码级与单测级验证。
10. **流畅度验收口径如实声明**：本项目对放大动画的验收门槛是"帧间隔 max < 32ms"。开发虚拟机实测 P95 帧间隔 **36.1ms**（丝滑重写后由 65.7ms 改进而来），**该门槛在本 VM 上未达标**，归因于 VM 分层窗口（WS_EX_LAYERED）合成路径的呈现下限；此数据**未在真实 DWM 合成的实体机上复验**，实机帧时间通常应优于 VM，但不保证。

### 未实现（有意延后）

以下能力 v2.0.0 **未实现**（避免"看起来像清单遗漏"的误解，明确列出）：

- Dock 停靠边可配置（当前 Dock 固定底边、顶栏固定顶边）
- Dock 图标拖拽排序
- Spotlight 式启动器
- 中键关闭窗口
- 切换器按窗口显示实时缩略图列表（当前为按应用归并的图标列表）
- 控制中心（音量/网络/电量等的聚合面板；当前顶栏各项独立点击）

## 许可证

本项目以 [MIT](LICENSE) 许可证开源。

第三方组件、设计资源与数据来源的许可与致谢见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 借鉴来源

与 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 一致。MIT/Apache 项目允许在署名下复制代码，本项目**未复制其任何代码**，仅借鉴实现模式与规范；GPL/AGPL 项目**仅借鉴思路，绝不复制代码**。

| 项目 | 许可证 | 借鉴内容（均未复制代码） |
| --- | --- | --- |
| [TheAgencyMGE/aero-dock](https://github.com/TheAgencyMGE/aero-dock) | MIT | 悬浮 Dock 实现模式参考 |
| [ModernFlyouts-Community/ModernFlyouts](https://github.com/ModernFlyouts-Community/ModernFlyouts) | MIT | GSMTC 媒体会话管理模式、`SetWindowCompositionAttribute` 亚克力用法 |
| [wwh1004/NeteaseCloudMusicApi](https://github.com/wwh1004/NeteaseCloudMusicApi) | MIT | 网易云歌词接口端点用法 |
| [tranxuanthang/lrclib](https://github.com/tranxuanthang/lrclib) | MIT | LRCLIB 开放 API 协议 |
| [stianhoiland/cmdtab](https://github.com/stianhoiland/cmdtab) | MIT | Alt+Tab 拦截机制参考（`AltTabHook.cs` 就地标注） |
| [dososo/HarmonyOS-Design](https://github.com/dososo/HarmonyOS-Design) | Apache-2.0 | 鸿蒙主题设计 token |

以下 GPL/AGPL 项目仅借鉴思路与行为参照，**未复制任何代码**：

- [lwouis/alt-tab-macos](https://github.com/lwouis/alt-tab-macos)（GPL-3.0）— 切换器交互规格参考
- [micheleg/dash-to-dock](https://github.com/micheleg/dash-to-dock)（GPL-2.0）— 高斯放大公式思路
- [KDE Latte Dock](https://invent.kde.org/plasma/latte-dock)（GPL-2.0+）· [Plank](https://github.com/ricotz/plank)（GPL-3.0）— Dock 布局/交互参照
- [eythaann/Seelen-UI](https://github.com/eythaann/Seelen-UI)（AGPL-3.0）— 切换器/工具栏产品形态参照
- [ejbills/DockDoor](https://github.com/ejbills/DockDoor)（GPL-3.0，GitHub 标注为 Other）— 悬停预览交互参照
- [TranslucentTB](https://github.com/TranslucentTB/TranslucentTB)（GPL-3.0）· [ExplorerPatcher](https://github.com/valinet/ExplorerPatcher)（GPL-2.0）· [Windhawk taskbar-styler](https://github.com/ramensoftware/windows-11-taskbar-styling-guide)（GPL-3.0）— 任务栏控制思路参照

数据来源：[网易云音乐](https://music.163.com/)接口（非官方，仅个人使用）、[LRCLIB](https://lrclib.net/)。

**商标声明**：本项目不使用任何 Apple 商标；顶栏 logo 为自绘原创标记（主题强调色圆底 + 字母 B），非 Apple 标志。HarmonyOS Sans 字体与 HarmonyOS Symbol 图标为华为专有授权，声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)（本项目当前未嵌入字体文件）。
