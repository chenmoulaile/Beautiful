# Beautiful

Windows 11 桌面美化套件：悬浮 Dock（macOS 风格）+ macOS 顶栏 + Alt+Tab 替换切换器 + 悬停实时窗口预览 + Spotlight 启动器 + 控制中心。WPF + .NET 8，自包含发布，中文界面。**只想跑起来 → 看「本地运行」**；想知道 v3 改了什么 → 看「v3 变更（对着反馈的七条）」。

> 界面截图不随仓库分发（`docs/` 为内部文档目录，已 .gitignore 排除）；观感以功能描述与发布包实运行为准。

Beautiful 接管日常桌面三件套：屏幕底部是悬浮 Dock（应用固定与启动、连续高斯放大、媒体胶囊、悬停 DWM 实时缩略图预览），屏幕顶部是 macOS 风格菜单栏（logo 菜单、前台应用名、真实"窗口"菜单、时钟/日历、音量/网络/电量、媒体迷你项），Alt+Tab 被替换为 macOS Cmd+Tab 风格的应用切换器。原生任务栏可选择完全隐藏、自动隐藏或保留。

> **v4.0-alpha 进行中（2026-10-30）**：正在把 v3 的"Windows 桌面美化套件"收敛为 macOS Big Sur/Monterey 还原壳。
> Phase 1（收敛与基底）已完成；Phase 2（C++ native 层脚手架/材质/捕获/场景/注入）已落地骨架，真机目视项待所有者回执。
> 本仓库的完成度声称一律以实测为准。

## v4.0-alpha 变更（Phase 1 已完成部分，反馈→现在→证据）

| 反馈/目标 | 现在 | 证据（可复跑） |
|---|---|---|
| 只保留 macOS，删除鸿蒙/Linux/Windows 三主题 | 3 套主题 XAML、`ThemeDefinition` 三个工厂、主题/切换器 UI 与"Windows 主题抬升任务栏"逻辑全部删除；旧配置值自愈并写迁移日志 | `grep` 零残留；`SettingsStoreTests.旧主题与已删除字段_加载自愈并逐条报告迁移` |
| 删除双语歌词链（非 macOS 语义、带网络依赖） | `Core/Lyrics/` 11 文件、歌词测试 4 文件、MVVM 歌词状态机、设置项 `LyricsOffsetMs`、面板歌词区全部删除；SMTC 只保留曲目/进度/三键 | `dotnet test` 774 通过；`grep -ri "lyric\|歌词\|lrclib\|163.com" src/` 只剩迁移取证字符串（已删除字段名 `LyricsOffsetMs`）与说明性注释，功能代码为零 |
| 删除玻璃壳与壁纸取色 | `TaskbarGlassOverlay`、`TaskbarGlassMath`、`WallpaperAccentService`、控制中心两块砖、持久化字段与测试全部删除；`FullscreenAvoidance` 保留给 Phase 3 | 构建 0 警告 0 错误；测试全绿；diag 相关判据后续 Phase 3 再接 |
| macOS 视觉 token 单一事实源 | `Core/Design/MacTokens`（亮/暗两套颜色/度量/字体/动效）、`MacAccents`（12 色板）、`AppearanceModes`；App 侧 `MacTokenDictionary` 同时注入新 `Mac*` 键与旧 `Theme*` 兼容键 | `MacDesignTokensTests`（12 色唯一、24px 菜单栏、56px Dock、圆角/时长/字体回退链） |
| 外观模式（深/浅/跟随系统）即时切换 | 设置页"外观 + 强调色"接线；注册表 `AppsUseLightTheme` + `WM_SETTINGCHANGE` 监听，`auto` 时换 token 不重建窗口 | `SystemAppearance` + `ThemeManager.ApplyAppearance`；**亮/暗真机目视 UNVERIFIED(owner)** |
| SF Pro 随产物分发（D-FONT） | `.gitignore` 排除 `Assets/Fonts/`；csproj 条件 Resource；解析链 内嵌 → 系统 SF Pro → Segoe；运行时探测写日志，设置页底部显示实际来源 | `FontResolutionTests`；**字体命中 UNVERIFIED(owner)**（需所有者本地放置字体后回执） |
| SF Symbols 图标集（T6 起步） | `Assets/Symbols/Symbols.xaml`：60 个 24×24 自绘 Path + `MacSymbol` 单色描边样式（首批清单全覆盖：WiFi/音量/电池四档、蓝牙、亮度、输入法、账户、USB、锁屏/睡眠/重启/关机、播放控制、视图/边栏/Quick Look 等）；设置侧栏已实际使用；**不新增 Segoe Fluent Icons 码点** | XAML 实际解析通过（60 键）；菜单栏/托盘既有图标仍为 Segoe，按后续 Phase 逐步替换（**目视 UNVERIFIED(owner)**） |
| 设置页 macOS 侧栏（T7） | 左侧 11 组导航（外观/Dock 语义/菜单栏/显示器/切换器/媒体/启动台与搜索/动画/固定应用/系统/关于）+ 右侧卡片滚动；补齐启动器开关与热键 UI、动画引擎选择、关于卡 | 代码/XAML 编译；**目视 UNVERIFIED(owner)**。Dock/菜单栏/触发角/底层增强等分组在对应 Phase 落地后继续细化 |

> 门禁现状：`dotnet build -c Release` **0 警告 / 0 错误**；`dotnet test` **1058 通过 / 0 失败**（基线 828，差额全部来自删除功能的测试）。
> Phase 1 收尾项：SF Symbols 全量替换（本批 60 个覆盖首批清单；菜单栏/托盘/弹层仍用 Segoe）、设置页完整 IA（Dock/菜单栏/触发角/底层增强分组随 Phase 3/4 细化）、真机亮暗/SF Pro/侧栏目视回执。Phase 2 native 层已开始（见下节）。

## v4.0-alpha Phase 2 变更（Native 层，进行中）

| 目标 | 现在 | 证据/边界 |
|---|---|---|
| C++ 进程内层 + 注入 payload | `src/Beautiful.Native/`（C ABI、材质/捕获/场景/注入）、`src/Beautiful.TrayBridge/`（空 payload：心跳/卸载/宿主 Mutex 自卸载）、`tools/NativeSmoke/`；`scripts/build-native.cmd` 用 VS2022 v143 /W4 + /WX 编译 | native 构建 **0 警告 0 错误**；`NativeSmoke`：ABI/health/material probe/scene/screen capture/inject gate **6 PASS / 0 SKIP / 0 FAIL** |
| 协议固定布局 | `Core/Native/NativeProtocol`（BnRect/BnHealth/BnMaterialDesc/BnCaptureResult）+ 尺寸/偏移单测；NativeSmoke 打印同一组 `sizeof` | `NativeProtocolTests`；C# 1058 通过 |
| 材质引擎（D-BLUR 一级路） | DComp + `Windows.UI.Composition` HostBackdrop 背板窗口；`NativeMaterialHost` 按 `GetWindowRect` 物理像素同步菜单栏/Dock，探针可用时让出 WPF 静态底 | HostBackdrop 探针通过（modules=0xF）；agent smoke 已在真实 Win11 创建 Dock/菜单栏两块背板（`logs/native.log`: `Material created`），截图无黑块；**材质目视仍待所有者按 §2.5 回执** |
| 捕获服务 | WGC（Graphics Capture）优先 → native PrintWindow → 屏幕 BitBlt；5×4 纯色判废；`WindowCapture` 在开关开启时优先走 native | `NativeSmoke` 屏幕捕获 PASS；WGC 真窗口路径 **UNVERIFIED(owner)**（本 VM 无控制台窗口可抓） |
| 场景渲染骨架 | D3D11 离屏纹理/回读基准图（渐变+网格），为 Phase 3-T7 genie 铺路 | `NativeSmoke scene offscreen render` PASS |
| 注入框架 | 仅 explorer、OS build 闸门、心跳、卸载闭环、宿主 Mutex 自卸载兜底；设置页「底层增强」总/分开关 + 注入演练二次确认 | **默认关闭 `NativeTrayBridge`**；注入演练 **UNVERIFIED(owner)**，核对清单 `scripts/verify/native-inject-checklist.md` |
| 纯 C# 形态 | `BEAUTIFUL_NONATIVE=1`/DLL 缺失/坏镜像/探针失败全部只记日志并降级；`BEAUTIFUL_NONATIVE=1` 仍是 CI/无桌面形态 | 所有 native 调用过 `NativeLoader` Guard；失败不抛给 UI |
| 发布/诊断 | `install-local.cmd` 先编译 native 再 publish；CI 增 native 构建 + NativeSmoke；`diag.ps1` 增 `[Native 模块]` 段 | 本机 `dist\Beautiful\Beautiful.exe` 已重新发布；版本 `4.0.0-alpha.1` |

> Phase 2 未完成/待回执：材质真机目视、WGC 对 Chrome/Electron 窗口的实测、注入演练 explorer 健康回执、看门狗"explorer 异常重启 ≥2 次/10min 自动禁用"（当前只有手动开关与 payload 自卸载兜底）。

## v4.0-alpha Phase 3 变更（Dock，进行中）

| 目标 | 现在 | 证据/边界 |
|---|---|---|
| Big Sur Dock 度量（P3-T1 部分） | 默认图标 56px（范围 32-96）、放大 1.7x、面板圆角 25、间距 7；新增「距底边距」设置（0-24，默认 4）并真正进入 AppBar 预留带与 PanelHost 底边距 | `SettingsStoreTests`/`ThemeDefinitionTests` 1058 通过；**真机观感 UNVERIFIED(owner)** |
| 全屏避让（P3-T6） | `FullscreenAvoidanceService`：1s 读前台 DWM 可见矩形，Core `FullscreenAvoidance` 判定；真全屏隐藏菜单栏/Dock，退出恢复；设置「全屏应用时隐藏菜单栏与 Dock」默认开 | 纯函数 28 条既有测试 + 服务构建通过；**真全屏触发 UNVERIFIED(owner)** |
| 材质（P3-T2） | 已接 Phase 2 `NativeMaterialHost`（探针可用让出静态底） | 同 Phase 2 边界 |
| 文件夹堆栈（P3-T3 起步） | 文件夹可作为 Dock 固定项；Core `DockPinOps.CanPin` 允许目录；点击弹 FluentMenu 列表（Shell 图标、在访达中打开、最多 10 项）；二级目录暂以资源管理器打开 | `DockPinCanPinTests`；**列表形态，fan/grid 布局、OLE 拖出、嵌套堆栈与下载堆栈仍未完成** |
| 最小化效果（P3-T7 部分） | `MinimizeEffect` = genie/zoom/suck；速度 0.5-2.0x；按进程排除清单；设置页动画卡接线。zoom 目前是源窗口内中心缩放淡出，suck 是更紧的条带吸吮 | 设置归一化单测 + 1058 测试通过；**native shader 网格渲染与精确 Dock 位移仍未完成**，真机动画目视 UNVERIFIED(owner) |
| 最近使用的项目（P3-T4 起步） | 从 `%APPDATA%\Microsoft\Windows\Recent\*.lnk` 读取，按目标扩展名的默认打开程序（AssocQueryString）匹配 Dock 应用；Dock 右键出现“最近使用的项目”子菜单；空表整段不出现，缺失文件置灰 | `DockContextMenuPlanTests`；**不是 AutomaticDestinations-ms/Jump List 解析**，浏览器等自定义 AppID 可能匹配不到，已在 README 如实标注 |
| 停靠边/自动隐藏（P3-T5） | **未开始**（需所有者在场验收） | 卡点：`StripEdge` 只有 Top/Bottom，左右停靠要同时改 Core 几何、`MonitorGeometry.ForEdge`、`DockSurface` 的横排/竖排布局与 AppBar 四边协商；自动隐藏还涉及隐藏时回收/恢复工作区。这些是**注入/目视类交互**，盲改会直接弄坏桌面工作区，按 §2.5 需所有者配合验收，故本轮不动 |

> Phase 3 已完成"度量默认值 + 全屏避让 + 材质接线 + 文件夹堆栈列表形态 + 最近项目（Recent 快捷方式）+ 最小化三效果设置（WPF 路径）"；fan/grid 堆栈、AutomaticDestinations-ms 解析、左右/顶部停靠、四种隐藏模式、native shader 动画仍待实现。真机对比截图是出口判据。

## 本地运行（真正能跑的那份在哪）

- **装起来用（推荐）**：下载 GitHub Release 里的 `Beautiful-Setup-v<版本>.exe`，双击安装。
  它是 **按用户安装**（不弹 UAC、不需要管理员），装到 `%LOCALAPPDATA%\Programs\Beautiful`，
  给开始菜单快捷方式，控制面板里可以卸载。为什么不走 `Program Files`：本应用把
  `config\dock.json`、`logs\`、`cache\` 写在 exe 旁边，装进系统目录后这些写入会被 UAC 虚拟化到
  VirtualStore，表现为"改了设置没生效 / 日志找不到"。
  卸载或覆盖安装前，安装器会先调 `Beautiful.exe --exit` 让在跑的实例走完整退出链路
  （还原任务栏、卸键盘钩子、`ABM_REMOVE`），而不是 `taskkill` —— 强杀会把系统状态留在接管态。
- **免安装**：下载 `Beautiful-win-x64-v<版本>.zip`，解压后运行其中的 `Beautiful.exe`。
  与安装器打包的是**同一件产物**（CI 里同一个 `publish` 目录），只是多了卸载器与快捷方式。
- **本机一键**：双击 `scripts\install-local.cmd`。它把应用发布到**固定目录**，结束时在屏幕上打印最终路径与 `Beautiful.exe` 大小：

  `E:\Beautiful\dist\Beautiful\Beautiful.exe` ← **这就是真正软件的地址**，双击即运行。

  `dist\Beautiful\` 是自包含产物（约 260 个文件，.NET 8 运行时随包附带），不需要另装运行时；`dist/` 已被 .gitignore 排除，重复运行脚本会先清空 `dist\Beautiful` 再重新发布（只动 `dist/` 里面的东西）。脚本里的 `dotnet publish` 参数与 `.github/workflows/release.yml` 的"发布自包含产物"步骤**逐字一致**（只有 `-o` 输出目录不同），所以 `dist\Beautiful\` 与 GitHub Release 里的 zip 是同一件产物。
- **没克隆仓库的人**：下载 GitHub Release 的 `Beautiful-win-x64-v<版本>.zip`，解压后运行其中的 `Beautiful.exe`（同样自包含）。
- **别拿构建产物当发布产物**：`src\Beautiful.App\bin\<配置>\...` 下那个 `Beautiful.exe` 是**框架依赖**版，脱离开发环境要设 `DOTNET_ROOT` 才起得来（弹 ".NET not found" 就是它）；面向使用的产物只有自包含那一份。
- **一条命令出诊断块**：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\diag.ps1`（可加 `-Path <日志文件>` 覆盖）。它自动挑出**最新**的应用日志，把 v3 各项修复的判据行（顶栏几何 / Dock 几何 / AppBar / 背板 / 图标提取 / 启动器 / 掉帧 / 策略）、每一块显示器的 `rcMonitor` 与有效 DPI、以及当前 `config\settings.json` 打成一整段——整段复制回来即可排障。

## v3 变更（对着反馈的七条）

| 反馈 | 现在 | 证据（日志/截图可核） |
| --- | --- | --- |
| **顶栏没贴屏幕顶，上方露一截** | 条带几何改按**窗口所在显示器**取：`MonitorFromWindow`/`MonitorFromPoint` + `GetMonitorInfo` 的 `rcMonitor` + `GetDpiForMonitor`，不再用 `GetSystemMetrics`（只有主屏）。另外 shell 收到 AppBar 注册后会把自己的条带往下推一条带高度，故 `ABN_POSCHANGED`/`WM_WINDOWPOSCHANGED` 时重申窗口落位 | `顶栏几何 window.top=0 rcMonitor.top=0 → 贴边OK`；`顶栏几何 窗口被移到 (0,30)-(1024,60) ≠ 目标 (0,0)-(1024,30)（偏移 30px）→ 重申贴边` |
| **Dock 只要 dock 那么大，不要一整条** | Dock 窗口是逐像素透明的分层窗口：可见面板收到图标行 + 分隔符 + 内边距，窗口矩形 = 预留带 + 放大生长余量，余量部分是透明的、点得穿 | `Dock 工作区 预留高=76px（=面板 76.0DIP，行高 60.0DIP，图标 48DIP，倒影 关）窗口高=98px 净得=22px`；`Dock 几何 window.bottom=768 rcMonitor.bottom=768 → 贴底OK` |
| **Alt+Tab 面板圆角不对** | 切换器改为分层窗口 + `Card` 自绘圆角（macOS 18 / Win11 8 / 鸿蒙胶囊），"玻璃"由显示前抓屏自绘模糊提供；v2 的 `SetWindowRgn` 裁形与 DWM 圆角属性两条路径整枝删除（真机上圆角缺口里照画深色块） | `SwitcherWindow.xaml.cs` 类型注释；三样式各有开发机本地截图（v3-t2 / v3-t2b 系列，不入库） |
| **缺少分隔符** | 三主题 Dock 原生还原：固定块与运行窗口块之间画分隔符，macOS/鸿蒙/Linux 各自的间距与收边分别对齐 | 开发机本地截图 v3-t4-hover-sep 系列（不入库） |
| **固定软件的行为** | 外部拖入即固定、块内拖拽排序、甩出 Dock 取消固定、回收站落点为显式 no-op（不删文件、不取消固定）；右键菜单 v3.1 起是完整的条目菜单（打开/窗口▸/关闭/退出/结束任务/固定/属性/回收站），见下「悬浮 Dock」 | `拖入固定：… → 固定块槽 N`、`Dock 排序：… 固定槽 A → B`、`甩出 Dock 取消固定：…` |
| **图标糊** | 图标提取按 256px 取最大帧，遮罩/内高光/alpha 投影一次性烘焙成图块并缓存，换主题或换尺寸才重烘。诚实边界：`.lnk` 的目标不是可执行文件（如 `ms-msdt:` 这类协议目标）时，降级链仍会落到第 ⑤ 层 `SHGetFileInfo(PIDL)` 的 **32px 系统图标**（日志里能看到 `层=SystemIcon 尺寸=32×32`）——这类条目比主流少一层，不是全链路都保证 256 | `图标提取 explorer.exe: 层=ShellDef 尺寸=256×256 格式=Bgra32 请求=256`、`图标烘焙参数: 主题=HarmonyOS 尺寸=48DIP dpi=1 图块=48px 形状=Squircle …` |
| **新增功能** | 控制中心（主题/任务栏策略/放大·倒影·媒体胶囊·壁纸取色砖块/真实系统音量）、Spotlight 式启动器（含 MSIX/UWP 条目、exe 名可直接搜）、壁纸取色强调色、Dock 显示器选择器 | 开发机本地截图 v3-t8-cc-crop.png（不入库）；`启动器：索引就绪，188 条可启动项`；`壁纸取色：… → #9BBBD4` |

## v3.2 变更（这轮六条）

| 需求 | 现在 | 证据口径 |
| --- | --- | --- |
| **弹层与按钮不在同一列**（点顶栏"窗口"，菜单跑到别处） | 病根不是锚点算错，而是锚点被解释成了**窗口外框**：分层窗口的自绘投影只能画在自己表面上，内容根元素带一圈 `Margin`（菜单 20/18/20/24 DIP），于是可见卡片整体右下偏移一圈投影，**偏移量随 DPI 放大**（真机 200% 上实测卡片左边落在 x=495 而按钮左边在 x=413）。落位算术下沉为 `Core/Layout/PopupPlacement.cs` 纯函数，锚点按**卡片**解释；投影留白由内容自己报（`IPopupShadow`，取自它根元素的 `Margin`，不在代码里抄第二份常数）。顶栏菜单/托盘菜单/Dock 菜单/子菜单/控制中心/启动器一次全修 | 10 条新单测：卡片左上角精确等于锚点、翻转贴的是卡片边而不是外框、留白允许溢出屏外而卡片不行 |
| **自动获取原生任务栏固定的应用** | 读 `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar` 的 `.lnk`，**首次运行**（还没有 `dock.json`）时并进 Dock 固定区。**收录 .lnk 本体而不是解析后的 exe**：本机 4 条固定项里 "File Explorer.lnk" 的 TargetPath 是空的，而 UWP 的 pin 解析出来全是 `explorer.exe`，按 exe 收录会把它们塌成一个；去重按"目标 + 参数"，同一个 exe 的快捷方式与本体不会双份 | 已有 `DockPinOps` 的目标级去重单测覆盖合并语义；`ImportTaskbarPins` 默认开 |
| **顶栏的系统托盘图标区** | 新增一颗飞轮按钮，点开自家面板：把通知区域（含"隐藏的图标"）里的第三方图标平铺出来，左键/右键转发回图标自己的窗口（`SendMessageTimeout` + 前置 `IsHungAppWindow`）。两路枚举：经典 `ToolbarWindow32`（跨进程读 explorer）与 UIA 路（24H2 的 XAML 岛），两路各自回报状态 | 归类/分页/矩形合理性等 Core 单测；**跨进程取图与 24H2 的 UIA 字符串未经真机验证**，取不到图时画中性占位并写明原因，绝不错画成别的图标 |
| **窗口最小化/最大化动画（像 macOS）** | 最小化：`EVENT_SYSTEM_MINIMIZESTART` 那一刻抓一帧当替身盖在原位，替身切成 22 条水平带各自向该应用的 Dock 图标收拢（genie 的弯曲感来自"靠近目标的带先动"的滞后），真窗口在替身底下正常最小化。最大化/还原：同机制做一段缩放过渡。时长固定、进度按真实经过时间算，vsync 驱动 | 16 条单测锁端点/单调/不交叉/越界钳制。**观感本身未经真机目视**；GPU 合成窗口可能整幅抓成纯色，此时判为不可用并**不做动画**（宁可不动画也不甩一块黑卡） |
| **单独的 WiFi 栏与声音控制（不用 Windows 的东西）** | 顶栏点网络开自家 **WiFi 面板**（wlanapi 列网络、四根柱子画信号、就地输口令、连接走 WLANProfile XML），点音量开自家**音量面板**（滑杆 + 静音 + 当前默认端点友好名）。原来这两处分别 `ms-settings:network-status` 与 `sndvol.exe` | 39 条 Core 单测：信号档位、排序、加密方式标签、可直连判定、profile XML（含 SSID 含 `& < > " '` 时逐字转义往返）。**实际连接与列表内容未经真机验证**；企业级/证书网络一律置灰写明原因，只给"系统网络设置…"这条出路，不放填了也连不上的自建表单 |
| **只美化原生任务栏（不照搬任何已知系统任务栏）** | "玻璃壳"：盖在原生任务栏上的**点击穿透**装饰层，只画它自己画不了的三样——面向屏幕内侧的圆角收口、1px 高光亮边、主题强调色发丝线。不移动、不隐藏、不拦截，按钮/跳转列表/Win+T 全部照常 | 18 条单测锁"什么时候该画、贴哪条边"。**外观效果未经真机目视**；策略为"隐藏"时装饰自动拆掉，砖块同时置灰并写明原因 |

## v3.3 变更（genie 换成真网格 + 安装程序）

| 项 | 现在 | 证据口径 |
| --- | --- | --- |
| **最小化动画不再是"条带缩放"** | 用户要的是"mac os 那种"。v3.2 那版把窗口切成水平条带整体收拢，**没有"脖子"** —— 而"同一进度下越靠下的行收得越窄"才是 macOS genie 的全部特征。现在渲染一张真正的纹理三角网，顶点走 macOS 的**灯罩（lamp）曲线**：两阶段，前 30% 把窗口拉长到下缘触到图标，之后逐行收进图标，另有一项只在前一阶段起作用的正弦摆动给出"甩进去"的弹性。算法参考 Windhawk 的 `macos-minimize-animation` mod（MIT，其 Modern 引擎由 Potassiumuncher 贡献），**只移植数学**：原实现是注入 explorer 的 C++/Direct2D，本项目是 WPF 进程内 `MeshGeometry3D` + `EmissiveMaterial`，没有一行代码相同，也不引入 Windhawk 本体（出处见 `THIRD-PARTY-NOTICES.md` 第 5 节） | 14 条单测钉住端点（起点逐顶点=源窗口、终点=塌进图标那一行）、行宽单调（下缘恒窄＝脖子）、摆动项两端归零（否则首尾会闪）、退化情形、越界钳制；另用 `.tools/genie-preview` 把网格离屏画成线框逐帧看过形状（未启动产品、未动桌面） |
| **还原方向也放** | 只装最小化、点开窗口却瞬间出现，反而把动画衬得更假。现在 `EVENT_SYSTEM_MINIMIZEEND` 反向放同一条曲线 | 同一套数学，方向由进度倒放 |
| **`GenieEngine` 可回退** | `mesh`（默认）走 WPF 3D 贴在分层窗口上；这条组合在缺硬件加速的机器上**只能实机见分晓**，而"动画变成一块黑卡"比"动画朴素一点"糟得多，所以保留 `strips` 作为一行配置就能退回的兜底 | 两个引擎都有单测覆盖其几何 |
| **Setup 安装程序** | `installer\Beautiful.iss`（Inno Setup 6）产出 `Beautiful-Setup-v<版本>.exe`，与 zip 同为 CI 产物。按用户安装、免管理员；打包时排除 `logs/config/cache`（那是开发机的运行数据）；安装/卸载前都走 `--exit` | 本机实装→校验载荷→卸载→确认目录与开始菜单快捷方式都清干净，全流程跑过 |

## v3.4 变更（这一轮三条反馈 + 顺手补的通道）

| 项 | 现在 | 证据口径 |
| --- | --- | --- |
| **点 Dock 图标能隐藏窗口了** | 三个各自独立的原因叠在一起，缺一个都表现为"点了没反应"。① 固定项过去无条件 `Process.Start`，而"隐藏"的语义只长在运行项上——现在先看这个应用有没有在跑的窗口，有就切换（前台→最小化 / 最小化→还原 / 其它→置前），没有才启动。② 匹配口径从"整路径相等"改成**进程映像基础名**：Win11 的 `System32\notepad.exe` 只是个桩，真正在跑的镜像在 `Program Files\WindowsApps\Microsoft.WindowsNotepad_...\Notepad.exe`，按整路径比永远对不上。③ 判定"它是不是前台"的时机错了：点 Dock 这个动作本身会把前台换给 Dock，等到 `MouseLeftButtonUp` 再问 `GetForegroundWindow()` 拿到的是 Dock 自己——现在由共享的 `EVENT_SYSTEM_FOREGROUND` 钩子记住"点之前用户面对的是谁" | **真机端到端**：日志 `Dock 条目点击：Pinned 'explorer' 前台=0x90B02` → 紧接着 `IsIconic(0x90B02)=True`，同一图标再点一次还原。中途还实测到 ShellHook 路线不可用（见最后一行），所以前台信号取自 WinEvent |
| **悬停放大的清晰度** | 图块过去按**静止尺寸**烘（48DIP×2dpi=96px），放大 1.8× 时是把 96px 拉到 173px 显示——这就是"质感很水"的全部来源。现在 `IconBakeSpec` 多带一个 `Magnification`，烘到放大上限那一档，放大时是 1:1 搬运 | 真机烘焙日志：`主题=MacOS 尺寸=48DIP dpi=2 放大上限=1.8× 图块=173px`（鸿蒙主题不开放大时仍是 `放大上限=1× 图块=96px`，不白占内存）；另在真机悬停截图上确认放大后边缘不糊 |
| **设置窗口换成 macOS 自绘标题栏** | 系统 chrome 退役：`WindowStyle=None` + `WindowChrome`，左侧红/黄/绿三点（关闭 / 最小化 / **置顶开关**），标题居中，DWM 圆角与真阴影保留。**没有用 `AllowsTransparency`**——分层窗口会关掉 ClearType 次像素抗锯齿，而设置页是全程序文字密度最高的一屏，灰度抗锯齿会把"质感很水"换个地方再演一遍。绿点做的是置顶而不是最大化：本窗口 `SizeToContent` + `MaxHeight`，最大化只会得到一块居中的窄条，是个没有意义的动作 | 真机截图确认三点、居中标题、条带与内容区的发丝分界、全部设置行照常可用；底色与文字色一律没覆盖，正文对比度与改造前同一 |
| **"窗口"菜单收窄** | 标题压成一行：去掉 `" • "` 那类装饰段、路径只留末段、超长截断加省略号；完整标题进悬停提示，不丢信息。14 条单测钉住"哪一段该留、什么时候不该留" | 单测；真机目视待补（菜单需要一次点击） |
| **`--settings` 会转发给在跑的实例** | 第二实例过去只会弹"Beautiful 已在运行"的模态框，用户要的那个窗口并没出现。现在带 `--settings` 就发一条注册消息让在跑的实例开设置页，本实例静默退出；转发失败才退回原来的提示。顺带把 `--exit` 的收件窗口从"顶栏"改成**先 Dock 再顶栏**——顶栏是用户可关的设置项，只认顶栏等于让关掉顶栏的人失去安装器的干净退出通道 | 代码路径同一（`PostToRunningInstance`），退出通道此前已随安装器实测过 |
| **HSHELL 事件码的实测更正** | 本机 `RegisterShellHookWindow` 实际只发 2/4/6/0x10，文档里的 `HSHELL_ACTIVATED=3`（及 `0x8003`）**一条都不发**。原代码写的 4/0x8004 恰好是这台机器真正会发的那一对，所以"按文档修正"反而会让最小化状态退回等 2s 兜底对账。现在两组都接，并在注释里写清实测与文档不一致 | 探针日志逐条比对（激活、关闭、切前台各事件的 wParam） |
| **设置窗口底部：版本号 + 配置目录** | README 里"日志在 exe 旁边"这句话对用户没用，他找不到 exe 在哪。底部一行直接给出 `Beautiful v<版本> · 配置与日志在 <目录>` | 版本号取 `InformationalVersion` 并砍掉 SourceLink 的 `+哈希` 后缀 |

## v3.5 变更（真机实测翻出来的四个"装了但没生效"）

| 项 | 现在 | 证据口径 |
| --- | --- | --- |
| **点 Dock 图标隐藏窗口：真实使用下仍失效** | v3.4 的前台判定依赖"WinEvent 记录的最后一次外部前台"，而真实会话里那是**陈旧样本**——日志里连续四次点击，前台永远是同一个不相干窗口（`0x60132`）。v3.5 改用**悬停快照**：光标扫过条带时前台必然还是用户的窗口（点击把前台换给 Dock 是下一瞬间的事），点击时若前台已是自己进程的窗口就用这份快照顶替。另外每条切换分支都留结果日志（最小化/还原/置前 + 当时前台），再失效一眼可见 | 旧日志 `Dock 条目点击：Window '…' 前台=0x60132` ×4（四次点击同一前台，全是激活分支）；新代码 `DockWindow.SnapshotForegroundAtHover` |
| **genie 在真机上一次都没播过** | 两处时序错位：① `EVENT_SYSTEM_MINIMIZESTART` 走系统队列送达时，我们那次同步的 `ShowWindow(SW_MINIMIZE)` 早已完成、窗口已经 iconic，事件路径开头的 IsIconic 早退**必然命中**——所以由 Dock 在 ShowWindow **之前**预抓画面与矩形，切换完成后用预抓帧放动画。② 抓帧对 DirectComposition 窗口（Chrome/Electron 全家）经常整幅纯色，加了第二条退路：**屏幕 DC 抠窗口矩形**（`genie-minimize-animation` mod 的同一条退路）。抓帧来源写进日志（`层=PrintWindow/屏幕`） | 全天日志里动画相关输出只有"已挂载"，零次播放记录；`.tools/windhawk-ref/genie-minimize-animation.wh.cpp:298` 的 BitBlt(hScreenDC) 退路 |
| **最大化/关闭动画** | 最大化/还原的缩放动画共享同一条抓帧退路后应可用。**关闭动画明确不做**：Windows 没有"关闭前"信号（要拦只能全局 CBT 注入别的进程，Lume 的 `LumeMinimizeHook32/64.dll` 正是注入宿主），为一个动画引入注入组件不值，且 macOS 本身不给关闭加动画 | `scripts` 无注入组件的既有裁定；`D:\lume\LumeMinimize.SOURCES.md` 佐证其注入架构 |
| **WiFi 面板误报"不可用"** | `WLAN_INTERFACE_STATE` 只写了两档还都抄错了：2、5 实际是 `ad_hoc_network_joined`/`associating`，**"已连接"是 1**——常态网卡反而过不了可用判定。改为"枚举到网卡即可扫描"，`not_ready` 只作瞬态提示 | `netsh wlan show interfaces`：Intel AX201 已连接 chaomeng-5G；与 ShellHook 的 `HSHELL_ACTIVATED` 同为"抄码没核对文档"家族 |
| **顶栏常驻托盘条** | v3.2 裁定"点开才读"，用户点名要 macOS 菜单栏的后台图标。现在顶栏时钟左侧常驻一条：最多 8 格、45s 一读 + explorer 重建即时重读（对"每帧抓屏"禁令不是一个量级），左键/右键都转发回应用自己，一格都没有就整体隐藏并把两路读数写日志 | **UNVERIFIED**：任务栏被本程序移出屏幕后屏幕上没有那一格像素，占位字形 + 应用名 tooltip 是当前兜底；本机两路各读到什么，等下次运行看 `顶栏托盘条` 日志 |
| **控制中心砖块间距 + 设置窗口正文** | 砖块上下贴死（真机截图实锤）→ 行距 4；设置窗口正文按钮从 WPF 默认 3D 方角灰块换成 macOS 四态圆角（常态/悬停/按下/禁用），列表行圆角高亮 | 用户两张截图；`ControlCenterPanel` 的 `CcTileStyle`、`SettingsWindow` 的隐式 Button/ListBox 样式 |
| **D:\lume 调研结论** | 它的最小化实现与本项目 v3.3 同源（同一个 MIT Windhawk mod v3.1.2，Abdullah Masood / Potassiumuncher），差别只在它是**进程内注入**（`LumeMinimizeHook*.dll` 宿主），所以抓帧天然无障碍；`ui/` 是 WebView2 界面、`taskbar-runtime` 是给 explorer 任务栏打补丁的另一套架构——**无可照搬素材**，本项目的解法是预抓帧 + 屏幕抠图退路 | `D:\lume\LumeMinimize.SOURCES.md`、`LumeMinimize.NOTICE.md` |

## v3.6 变更（托盘在 24H2 上读得到的真办法 + 控制中心补真开关）

> 先记一条流程教训：用户日常运行的是 `dist\Beautiful\`（本机自测产物），而 v3.4/v3.5 的修复只进了
> `src\...\bin\Release` 与 GitHub Release。于是他看到的"一个都没生效"其实是**跑的份不是改的份**。
> 现在每次交付都要同时问一句"他启动的那条路径重建了吗"。

| 项 | 现在 | 证据口径 |
| --- | --- | --- |
| **托盘图标读不到的真因** | 不是"24H2 换了 XAML 岛所以读不到"，而是**UIA 不会跨岛的片段根**：从 `Shell_TrayWnd` 的 HWND 做 `FindAll(Descendants, Button)` 得到 **0 个**，而从它的子窗口 `Windows.UI.Composition.DesktopWindowContentBridge` 的 HWND 进同一棵树得到 **23 个**。现在按 HWND 分别进树（岛宿主作为 `Shell_TrayWnd` 的直接子窗口用 `FindWindowEx` 找，不枚举全部子窗口）。 | 只读探针 `.tools/trayprobe.ps1` / `trayprobe2.ps1`（不建窗口、不发输入），逐条打印 23 个按钮的 Name/AutomationId/ClassName/矩形 |
| **岛里只收第三方图标** | 岛把"开始 / 任务视图 / 每个任务栏应用按钮 / 音量 / 电源 / 时钟 / 显示桌面 / 第三方托盘图标"混在同一层同一 ControlType 里。实测**只有 `AutomationId == NotifyItemIcon` 是应用自己注册的图标**（本机：华为电脑管家、华为控制中心、更新提示三条），其余是 shell 部件；按名字筛会被"音量 本机扬声器 (Audio Device): 已静音"这类带动态后缀的名字骗过去，所以按 id 收 | 探针输出逐条比对；旧路径（23H2 及以下）不受这条筛选影响 |
| **岛里的图标像素：两条路都试过，都拿不到** | ① "整窗 `PrintWindow` + 按 UIA 矩形抠这一格"——本机实测 **XAML 岛不参与 PrintWindow**（对 `Shell_TrayWnd` 与岛子窗口都得到全黑），而任务栏被移出屏幕后屏幕上也没有那一格可裁。② "取所属进程的主程序图标"——**更错**：24H2 的岛把所有托盘图标都托管在 explorer 进程里，UIA 报的 `ProcessId` 三格全是 explorer(14148)，于是三格一律画成资源管理器的黄色文件夹。最终按项目既有裁定收口：**取不到图就中性占位**（画应用名首字、tooltip 给全名），不画一个错的图标。任务栏策略为"保留/自动隐藏"时第①条仍然有效，所以代码留着 | 装上 v3.7.0 后本机日志：`UIA[读到 3 个按钮元素] 平铺 3 格，跳过系统项 0 个，有图 0 格，可转发 3 格`；顶栏截图是"华 华 安"三格占位，三格都能点回自己的应用 |
| **控制中心 Wi-Fi 真开关** | 一块真的无线电开关：`WlanSetInterface(opcode=interface_state, TRUE/FALSE)`，读回用 `WlanQueryInterface`。三态判据：**启用 / 禁用 / 问不出来**——最后一种置灰写原因，不画成"关"（那是给了一个看起来正常、点下去没反应的假开关）。写失败回读真值把界面拉回来 | 接口是 wlanapi 公开文档；改动网卡会影响用户联网，**未在本机实机点过** |
| **控制中心亮度滑杆** | 内置屏只能走 `root\WMI` 的 `WmiMonitorBrightness` + `WmiMonitorBrightnessMethods.WmiSetBrightness`（gdi32 的 DDC/CI 只对外接屏有效，本机是 `DISPLAY\EDO0142` 内置面板）。用 WMI 的**自动化接口**（`WbemScripting.SWbemLocator` + C# `dynamic`）而不是手写 `IWbemServices` vtable——后者要按顺序声明 `ExecQuery` 之前十几个槽位，错一个就是内存踩踏；零新增包、零手写 vtable。读不到（台式机/虚拟显卡/驱动没暴露）→ 整行置灰 + 中文原因 | 本机实测：`InstancesOf('WmiMonitorBrightness')` 读到 **61**，`WmiMonitorBrightnessMethods` 上确有 `WmiSetBrightness` |
| **蓝牙 / 飞行模式** | 只做**打开系统对应设置页**的入口（`ms-settings:bluetooth` / `ms-settings:network-airplane`），不做假开关：Windows 不给普通进程这两个无线电开关——WinRT `Radios.SetState` 需要打包应用才有的 `radios` 能力，禁用蓝牙设备要管理员权限。诚实的入口比"点了没反应的开关"值钱 | 能力边界来自 API 约束本身 |

## v3.7 变更（装上你自己那台机器验一遍）

v3.6 的 Setup 装到你机器上（`C:\Users\chenk\AppData\Local\Programs\Beautiful`，按用户安装、免管理员，
配置从旧实例带过来）之后，逐条实测：

| 项 | 结果 |
| --- | --- |
| 托盘读取 | `UIA[读到 3 个按钮元素] 平铺 3 格，跳过系统项 0 个，可转发 3 格` —— 从"一个都读不到"变成 3 个第三方图标，且三个都能点回自己的应用 |
| 托盘图标像素 | **拿不到**（原因见上一张表最后一行）。撤掉 v3.6 里"取主程序图标"的兜底——实测它把三格全画成资源管理器的黄色文件夹，那是错画；改回中性占位（顶栏现在显示"华 华 安"三格 + tooltip 全名） |
| 放大清晰度 | `图标烘焙参数: 主题=MacOS 尺寸=48DIP dpi=2 放大上限=1.8× 图块=173px` |
| 点 Dock 图标隐藏窗口 | 你自己在 3.5.0 上的点击日志已经出现 `窗口切换：最小化`×3 / `还原`×3，抓帧 `层=PrintWindow`×10 + `层=屏幕`×5 |

## v3.8 变更（第四条线：Windows 原生美化）

用户一句话点破的缺口："好像只有 mac os linux 和 鸿蒙三个而已呢，windows 版的美化并没有"。
v3.7 之前"美化 Windows"这件事确实是散的——玻璃壳、托盘、控制中心各干一段，但**主题只有三套，
没有一套是照着 Windows 自己画的**。这一轮把第四条线立起来，并把 TaskbarLyrics（MIT）里能复用的
无依赖策略并进来。

| 项 | 做法与证据 |
| --- | --- |
| **第四主题 Windows 11** | `ThemeDefinition.Windows()` + `Themes/Windows.xaml`：深色 Mica 底 `#F02B2B2B`、8px 圆角、**图标不加遮罩**（Win11 的图标就是原样方图，套 squircle  mask 一眼假）、`MaxScale=1.0` 无悬停放大、运行指示改成"图标下方居中短横线"。**禁模糊是刻意的**：Win11 深色任务栏是一层接近实色的 Mica，不是 macOS 那种透出桌面轮廓的亚克力，做成半透明模糊反而不像它 |
| **主题清单改成一处数据源** | 三处 UI（托盘右键子菜单、控制中心主题砖、设置页主题行）此前各自硬列三套主题，加第四套要改三处且必然漏。现在全部从 `QuickSettings.Themes` 生成；设置页那行原本是 `sender == X ? "a" : …` 的三元链，换成每个 RadioButton 自带 `Tag` 主题 id |
| **切到 Windows 主题时抬升任务栏策略** | `App.ApplyTheme`：策略为 `hide`（任务栏整窗移出屏幕）时抬到 `autohide`——原生任务栏都不在场，贴在它上面的增强层就没有落点。抬升会写日志，不是静默改用户设置 |
| **全屏避让（玻璃壳）** | 移植 `SmartTopmostPolicy` 的判定为 Core 纯函数 `FullscreenAvoidance`（几何比较与资格条件全在 Core，可单测），App 侧只留 `FullscreenProbe` 取值。玻璃壳此前**无条件 `Topmost=true`**：那是一条用户关不掉、又不属于任何应用的横线，会盖在全屏游戏与 F11 视频上。现在"有窗口把整块屏四条边都盖住"就落到它下面，屏空着就把置顶施加回去 |
| **两处"全屏"判据的收口** | 切换器的 `IsExclusiveFullscreen` 与装饰层的让位判据保守方向相反（前者错判=Alt+Tab 还给系统一次，后者错判=美化层凭空消失），所以**判据不合并、几何合并**：那条"四条边盖住显示器"的比较统一走 `FullscreenAvoidance.CoversMonitor` |
| **单测** | `FullscreenAvoidanceTests` 28 条：四边各差 1px 就不算盖住、副屏负坐标、显示器矩形非法、五种资格条件（含 cloak）、让位对象换人、"浮着每轮重施 / 让位只做一次"。全库 Core 单测 **759 通过**（上轮 731），构建 **0 警告 0 错误** |

### 真机取证（`.tools\zorder-probe`，跑的就是生产代码）

只读取证工具，全程不激活、不移动、不关闭任何窗口（`NativeMethods` 是 internal，工具自己声明取样的
P/Invoke，不为取证去放宽生产可见性）：

| 观测 | 数值 |
| --- | --- |
| 任务栏与显示器 | 任务栏 `0,1824,2880,1920`（2880×96px），显示器 `0,0,2880,1920` @缩放 2，`IsDecoratable=True Edge=Bottom` |
| 最大化的窗口会不会误触发 | 实测最大化的 Chrome：DWM 扩展边框 `0,0,2880,1824`（正好等于工作区），`GetWindowRect` `−13,−13,2893,1837`（外扩一圈不可见缩放边）→ `CoversMonitor=False`。**这就是"最大化不触发让位"的实机依据**，也是取值必须用 DWM 矩形而不是窗口矩形的原因 |
| 必须靠 cloak 判据的那个窗口 | `TextInputHost.exe` 的 `Windows.UI.Core.CoreWindow`：`IsWindowVisible=true`、矩形 `0,0,2880,1920` 正好铺满、`DWMWA_CLOAKED=2`。全量体检 21 个可见顶层窗口中，铺满整屏的只有它和 `Progman`，分别被 cloak 与 shell 类名挡掉 → **会真的让位的 = 0** |
| 为什么不按类名拉黑 `Windows.UI.Core.CoreWindow` | 同一个类名既属于输入法宿主（该排除）也属于 UWP 全屏游戏（该让位）；按类名一刀切必然误伤一边，按 `cloaked` 判则两边都对 |
| 尚未做实机触发的一半 | "出现真全屏应用时装饰层确实落下"这一方向需要一个占满屏幕的全屏程序，会在验证期间挡住正在使用的机器 → 未触发；该方向由单测与上面那组排除性取证共同覆盖 |

## 不同分辨率、DPI 与多屏

几何的**唯一来源**是"这条带实际落在哪块屏"，不是主屏尺寸：

1. **选屏**：设置项 `DockMonitor` / `TopBarMonitor` 决定，取值 `auto`（启动时光标所在显示器，解析结果按进程粘住，避免拖动滑杆时 Dock 整条跳到副屏）/ `primary` / 序号（设置面板的下拉标签里直接写着每块屏的真实分辨率与原点）。任何解析失败都往 `auto` 收敛，最终兜到"窗口所在屏 → 首块枚举屏"，宁可让条带待在一块合理的屏上，也不返回一个猜出来的尺寸。
2. **取数**：`MonitorFromWindow`/`MonitorFromPoint` 拿显示器句柄 → `GetMonitorInfo` 拿 `rcMonitor`（贴边要用真边缘，不是 `rcWork`）→ `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` 拿该屏有效缩放。**条带（顶栏 / Dock）几何**里 `GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)` 一次都不出现——那是 v2 "副屏错位 + 高 DPI 屏顶栏上方露壁纸" 的根因；弹层落位、切换面板居中与窗口预览钳制这三处只在 `GetMonitorInfo` 失败的最后一道兜底里仍用它（不是条带路径）。混合 DPI 多屏下也**不拿刚注册窗口的 `GetDpiForWindow` 为准**（它返回的是**初置屏**的 DPI，副屏 150% 会把条带算成 2/3 高），只在 `shcore` 不可用时才降级到它，最后才兜 1.0。
3. **预留与生长**：上报给 shell 的 `ABM_QUERYPOS/ABM_SETPOS` 只包含**可见面板**那一截，真实窗口矩形 = 预留带 + 越界生长余量（放大 headroom）。于是最大化窗口让位的是面板的高度，而放大时"长出来"的那部分画在预留带之外、盖在桌面上，不会把桌面重新挤一遍。
4. **重申落位**：shell 处理 AppBar 注册时会把条带整体往下推一条带高度（顶栏表现为"贴在工作区顶而不是屏幕顶"），因此收到 `ABN_POSCHANGED` 与 `WM_WINDOWPOSCHANGED` 后按目标矩形重申一次 `SetWindowPos`——**只重摆窗口、不重发 `ABM_SETPOS`**（预留带没变 → 不会再触发 shell 重排 → 不构成"重申 → 广播 → 再重申"的环）。
5. **副屏任务栏**：`TaskbarManager` 判"任务栏在不在自己那块屏"用的是它所在显示器的 `rcMonitor.Bottom`，把副屏的 `Shell_SecondaryTrayWnd` 推出**它自己的**屏幕，而不是主屏那一行。

> 这些路径的**算术**（`StripMath`/`MagnificationField` 等）是 Core 纯函数、由单元测试覆盖；但本项目的开发机是**单显示器 1024×768 @100%**，所以"混合 DPI 多屏"的端到端呈现只有单元测试与代码路径级验证，**未在实机复验**。真机上请按「本地运行」里的 `scripts\diag.ps1` 出诊断块——`[显示器与 DPI]` 一节会列出每块屏的 `rcMonitor`/`rcWork`/有效 DPI/主屏标志，与 `[顶栏几何]`、`[Dock 几何]` 两节的数字对一眼就能定位是哪一块屏、哪一步算错。

## 功能特性

### 悬浮 Dock

- AppBar 注册常驻屏幕底部（停靠边固定为下，两条 AppBar 各自硬编码边位；**可配置停靠边为有意延后项**，见「已知限制」）；左下角为固定应用，右侧为媒体胶囊
- **连续高斯放大场**（v2 重写）：以真实物理光标坐标为输入、`CompositionTarget.Rendering`（vsync）驱动、指数阻尼收敛——动画速度只与时间有关、与帧率无关；高斯公式为 Core 纯函数（`MagnificationField`，可单测）
- **图标倒影与启动弹跳**（v2）：倒影渐隐遮罩（macOS 主题下开启），启动固定应用播放 ~600ms ElasticOut 弹跳；均可在设置开关、即时生效
- **悬停实时窗口预览**（v2）：悬停窗口条目 350ms 后弹出该窗口的 **DWM 实时缩略图**（纯 Win32 宿主窗口承载，规避 WPF airspace；缩略图句柄全路径注销不泄漏；源窗口消失/最小化自动收起）。v3.1 修正：该宿主窗口需要玻璃边距（`DwmExtendFrameIntoClientArea(-1)`）缩略图才显形，缺它时三步 HRESULT 全 0 而屏上是一只实心黑框——现在**玻璃开不出来就直接不弹卡片**，宁可没有预览也不留黑框
- 条目右键菜单为自绘 FluentMenu，v3.1 起是完整的条目菜单（条目/顺序/置灰理由全在 Core 纯函数 `Dock/DockContextMenuPlan.cs`，Win32 落地只在 `Dock/DockContextMenuExecutor.cs`）：固定项 = 打开 / 打开新窗口 / 窗口▸ / 显示所有窗口 / 关闭窗口 / 退出 / 取消固定 / 以管理员身份运行 / 打开文件位置 / 属性… / 结束任务；运行窗口项 = 切到该窗口 / 最小化 / 最大化·还原 / 关闭窗口 / 关闭所有窗口 / 固定到 Dock / 结束任务；回收站 = 打开 / 清空回收站。安全边界：**结束任务与清空回收站必须再点一次确认格**才生效；系统关键进程（csrss/wininit/winlogon/services/lsass/svchost/dwm/explorer/System/Memory Compression）一律置灰禁止，执行时按**当时的**进程名再核一次；正常关闭一律 `WM_CLOSE`（不用 `DestroyWindow`/`TerminateProcess`）；做不到的条目一律置灰并把原因写进标签，不留"点了没反应"的死项（裸 `.exe` 的属性页在 Win11 24H2 已不可用 → 那一格直接不给）。固定清单持久化于 `config/dock.json`
- **拖拽交互（v3）**：从资源管理器等外部窗口把应用拖进 Dock 即固定、块内拖动即排序、甩出 Dock 即取消固定；拖到回收站图标上是**显式 no-op**（既不删文件也不取消固定）。⚠ 诚实标注：本项在开发机上用**进程内 `FileDrop` 代理**（自己构造 `DragEventArgs` 喂给同一条处理路径）验证，**没有用真实 Explorer 拖拽**复验——真实拖拽的数据格式与 drop 效果回调可能有差异

### macOS 顶栏（v2）

- 顶部 `ABE_TOP` AppBar 条带，与底部 Dock 两条带共存，最大化窗口自动让位
- 左区：**logo（Beautiful 应用菜单：关于/设置…/主题▸/重载 Dock/退出，全部真实动作）** + **前台应用名**（WinEventHook 跟踪）+ 文件/编辑/视图/**窗口**/帮助——"窗口"是按当前前台窗口实际执行最小化/还原/关闭的**真实窗口菜单**
- 右区：**媒体迷你**（有 SMTC 会话时显示标题，点击展开面板）· **时钟**（点击弹出日历浮层；**显示秒数 / 日期 / 星期 / 24 小时制四项可在设置页勾选**，形态与刷新步长由 `Core/TopBar/ClockFormat.cs` 纯函数折算并单测，输出与机器 locale 无关）· **音量**（点击打开系统混音器）· **网络**（联网状态指示）· **电量**（`GetSystemPowerStatus`，仅在存在电池时显示）；网络/电量共用一条每分钟定时器对账，时钟单独一条：**显示秒数时对齐秒边界逐秒刷新，关闭时对齐分边界逐分钟刷新**（不存在 100ms 轮询，也不会"没勾秒却逐秒醒"）
- **控制中心**（v3）：右区最末图标唤出的快速设置面板（同一套分层弹层 + 抓屏模糊背板）。v4 起含**系统任务栏策略三选一**、**放大 / 倒影 / 媒体胶囊三个 macOS 语义开关砖块**、**真实系统音量滑杆 + 静音**（Core Audio `IAudioEndpointVolume`，手写 interop、无第三方包），以及"打开设置…/重载 Dock"两个动作。每一项都走与设置页/托盘菜单同一条落地路径；打开时读真值，再点一次/Esc/点面板外均收起。音频端点不可用时音量行**置灰并写明原因**（可用 `BEAUTIFUL_NOAUDIO=1` 自检该形态），不伪造读数
- 顶栏自绘半透明底（tint + 顶部内高光 + 下缘亮边），**不做实时抓屏背板**（常驻表面重抓会把自己的像素抓进背板 = 鬼影，见「菜单与背板」）

### Alt+Tab 替换切换器（v2）

- `WH_KEYBOARD_LL` 低阶键盘钩子拦截 Alt+Tab（热键解析层支持 `alt+tab`/`ctrl+alt+tab` 等组合，**当前设置界面固定为 Alt+Tab、不提供修改**）；同应用多窗口按 exe 归并并可**展开**逐个选择（Core 纯函数 `WindowListMerger`）
- **唯一样式（v4）**：macOS 卡片（居中大圆角毛玻璃 + 字幕，"玻璃"同样是抓屏自绘）；Linux/鸿蒙样式随三主题收敛删除，旧配置值自愈为 macOS
- 按住触发键循环、松开提交、**Esc 取消**（不改变前台）；看门狗兜底，绝不留下"按住 Alt 面板不消失"的卡死态
- **全屏直通**：前台为全屏应用（游戏/独占全屏）时自动放行原生 Alt+Tab，可在设置关闭
- **应急停用 Win+Alt+K**：任何时刻按下即在进程内卸载接管、恢复原生 Alt+Tab（只认真实体键；不改设置值，重开在设置中操作）

### 菜单与背板（v3 重写）

- **自绘 FluentMenu 统一所有菜单**：托盘右键菜单、顶栏全部菜单/浮层、Dock 条目右键——同一套 macOS 质感弹层（激活式弹出 + 键盘可达 + 锚点屏 DPI 换算 + 抓屏模糊背板）
- **弹层的"玻璃"是我们自己抓屏算出来的高斯模糊，不是系统 Mica/Acrylic**：显示前把面板下方的屏幕内容取回 → 1/4 降采样 → 三次盒式模糊 → 用 `Viewbox` 瞄准"面板在捕获区里的那一块"交回画刷（面板长大时只是露出旁边已抓好的内容，不重抓屏也不重缩放）。抓屏失败 / 取不到显示器 / 全零帧一律返回 null → 内容自铺**实心**卡底，不变黑也不开洞；`BEAUTIFUL_NOBLUR=1` 可整体自检降级形态。日志可查：`背板=抓屏模糊 面板=… 区=… 图=138×153 半径=8×4`、`背板=实心降级（抓屏未产出）`
- **常驻表面（顶栏、Dock）不做实时抓屏，也不向系统要任何材质**：它们本来就画在屏幕上，实时重抓会把**自己的像素**抓进背板（鬼影），而为了抓屏先隐藏一帧就是可见的闪烁。顶栏是三层自绘底（tint + 顶部内高光 + 下缘亮边），Dock 是 `ThemeDockBackground` + 1px 亮边，**这就是唯一可见底**；条带里面板之外的像素恒为真透明（分层窗口未绘制区，既点得穿也看得见桌面）。抓屏路径只给"显示一次"的瞬态表面（切换器 / 菜单弹层 / 媒体面板）
- **v2 的"系统背板协商"与 v3.0.0 的"旧亚克力增强"都已从渲染路径删除**：`DWMWA_SYSTEMBACKDROP_TYPE(38)`（Mica/Acrylic）与未公开的 `DWMWA_MICA_EFFECT(1029)` 不再调用——`hr==0` 只证明属性被接受，不证明屏幕上真糊了，协商结果在真机上会骗人。v3.1 把 `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` 也一并删掉：Win11 24H2/25H2 上它已经不模糊，而是把 accent 的 `GradientColor`（我们传的主题 tint）**平铺满整个窗口矩形**，Dock 的窗口矩形是全显示器宽 ×（面板 + 放大余量），于是用户真机看到屏幕底边一条全宽深色带并遮挡后面窗口的下一行。现在代码里只剩一种 accent 状态可写（`Disabled`），用途是把老版本留在窗口上的状态清掉一次（`AcrylicWindow.ClearLegacyAccent`）。保留的系统侧调用只剩悬停预览那个**纯 Win32 宿主**窗口的两项：`DWMWA_WINDOW_CORNER_PREFERENCE(33)` 圆角，与 `DwmExtendFrameIntoClientArea(-1)` 玻璃边距——缩略图由 DWM 合成，只在玻璃像素上显形，缺了它就是"HRESULT 全 0 但屏上一片黑"（该项豁免只给这个窗口，任何 WPF 表面依旧禁止）

### macOS 主题 + 外观模式（v4）

- **单一主题线**：Big Sur/Monterey。鸿蒙 / Linux GNOME / Windows 11 三套主题及其 XAML、工厂、UI 入口已删除；旧配置值由 `AppSettings.Normalized` 自愈为 `macos` 并留迁移日志
- **外观模式**：深色（默认）/ 浅色 / 跟随系统。系统模式读 `HKCU\...\Themes\Personalize\AppsUseLightTheme`，`WM_SETTINGCHANGE` 到达时重读并只重写 token 键——不重建窗口；所有 `DynamicResource` 表面即时响应
- **强调色**：macOS 12 色板（`Core/Design/MacAccents`），设置页可选；默认蓝
- **token 单一事实源**：`Core/Design/MacTokens`（亮/暗两套颜色/度量/字体/动效）+ `docs/macos-spec.md`；渲染器禁止 `if (theme == mac)`，新代码只读 `Mac*` 资源键，旧 `Theme*` 键作为兼容别名同步换色
- **字体**：SF Pro 解析链见下节；未携带字体时回退 Segoe UI Variable，应用完整可用

### 原生任务栏接管（三档策略，默认隐藏）

- `隐藏`：先释放任务栏的底部条带预留（`ABM_SETSTATE(ABS_AUTOHIDE|ABS_ALWAYSONTOP)`，Win11 实测唯一有效手段），再把任务栏窗口移出屏幕（被 shell 回摆时降级整窗隐藏兜底）——**Dock 与屏幕真底边 0px 间隙**；AppBar 注册后 0.8s/3s 各重申一次，退出或切换策略时完整还原（含写回改动前的原始状态位）
- `自动隐藏`：调用系统级自动隐藏（SHAppBarMessage），同样释放条带 → Dock 贴真底
- `保留`：不动原生任务栏，Dock 让位在任务栏上方
- explorer 重启自愈：监听 `TaskbarCreated` 广播后自动重新应用当前策略

### 媒体小组件

- 基于 SMTC（System Media Transport Controls）自动发现播放会话——Spotify、网易云音乐（UWP/注册了 SMTC 的客户端）、主流浏览器、Apple Music 等即插即用，无需逐个适配
- 实况窗胶囊：Dock 上常驻一条迷你胶囊，显示封面/标题与播放状态；顶栏右区亦有媒体迷你项
- 展开面板：大封面、可拖动进度条、上一首/播放暂停/下一首（v4 起不再有歌词区）
- **双语歌词已在 v4 Phase 1 删除**：`Core/Lyrics/`、网易云/LRCLIB 提供器、`LyricsOffsetMs`、面板歌词列表与相关测试全部移除；运行期零音乐服务网络请求

### 启动器（Spotlight 式）

- 入口两个：顶栏右区搜索按钮、`Ctrl+Alt+Space` 全局热键（设置项 `LauncherEnabled` 可关，关掉后热键与按钮一起收）；Esc / 点别处即收。控制中心里**没有**启动器开关（三个砖块是放大/倒影/媒体胶囊）
- 热键与接管 Alt+Tab **共用同一条 `WH_KEYBOARD_LL`**：钩子装/摘的唯一收口是 `SwitcherService.SetHookEnabled`，"接管关、启动器开"时钩子仍为启动器保活（`Enabled=false`，只投递不吞键）。配置侧硬约束：至少一个修饰键 + 可识别主键，且**单修饰键 + 空格**（`ctrl+space` / `win+space` / `alt+space`）一律拒绝——启动器要配对吞掉按下与抬起，吞这三个等于在所有应用里废掉输入法切换 / 键盘布局切换 / 窗口系统菜单；带这类遗留值升上来的配置会被自愈成默认的 `ctrl+alt+space`
- 诚实边界：`LauncherEnabled` / `LauncherHotkey` 目前**没有设置界面开关**，只能改 `config\settings.json`（设置页里那一项还没接线）
- 索引来源两条：**开始菜单 Programs 目录**（用户区 + 机器区，解析 .lnk 目标与自定义图标）与 **MSIX/UWP 包**（Win11 的记事本、计算器、画图、Store 等已不再有 .lnk，缺这一路等于"搜不到记事本"）；MSIX 那一路整体 try/catch，失败只写成一行日志并少一批条目，桌面条目照常可用，且全程在后台线程（UI 线程零 COM/WinRT）
- 检索字段两个：**显示名**与**启动目标名**（`charmap.exe` → `charmap`、`Everything.lnk` → `everything`、AppsFolder 的 AUMID → 包名词）。用户敲的是 exe 名而不是显示名，只按显示名打分就会出现"列表里明明有、搜索说没有"；目标档整体压在同名次档之下一档，唯一例外是"目标完全相等"高于"名称前缀"
- 中文支持：手写拼音首字母表（`jsq` → 计算器），零第三方依赖
- 启动：解析出的目标以 `UseShellExecute=true` 交给 shell；MSIX 应用走 `explorer.exe shell:AppsFolder\<AUMID>`，绝不 `cmd /c`
- 已知限制：MSIX 条目在当前 Windows SDK 投影（19041）里取不到 logo 路径，图标留空框而不是错画成资源管理器图标

### 设置窗口（v2 重构，v4 外观接线）

- 分组：**外观**（外观模式/强调色/图标尺寸/放大强度/倒影/弹跳）· **系统**（任务栏策略/顶栏开关/开机自启）· **显示器** · **窗口切换器**（接管开关/全屏直通/应急键说明——触发键当前固定 Alt+Tab，UI 只读展示）· **媒体**（胶囊/失焦收起/悬停预览）· **固定应用**
- **live 生效**：改动即时应用到运行中的界面（外观模式/强调色换 token、尺寸/强度/倒影重放、预览即关即隐），多数项无需重启进程
- 底部状态行显示版本、**实际字体来源**（内嵌/系统/回退）与配置目录

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
├── tests/Beautiful.Core.Tests/  # Core 单元测试（703 项）
├── scripts/                 # env.sh / install-local.cmd（本机发布到 dist\）/ diag.ps1（诊断块）/
│                            # publish.cmd / release.ps1 / shot.ps1 / 探针脚本
├── .github/workflows/       # release.yml（推 v* 标签云端发布）
├── dist/                    # 本机可运行产物（gitignore，由 install-local.cmd 生成）
├── publish/                 # 历史发布目录（gitignore，由 publish.cmd 生成）
├── Directory.Build.props    # 版本号单一来源（<Version>）
└── THIRD-PARTY-NOTICES.md
```

**开发构建与测试**（Git Bash / WSL，使用仓库内置 SDK 时）：

```bash
. ./scripts/env.sh && dotnet build
. ./scripts/env.sh && dotnet test tests/Beautiful.Core.Tests
```

**本地产物（推荐给使用者）**：

```cmd
scripts\install-local.cmd
```

双击即可。它把应用发布到**固定目录** `dist\Beautiful\`（重复运行会先清空该目录，只动 `dist/` 里面），结束时打印最终路径与 `Beautiful.exe` 大小。脚本用 `%~dp0..` 推仓库根，因此不依赖 `E:\Beautiful` 这个具体盘符；仓库自带的 `.dotnet\`、`.nuget\` 存在时就用它们（构建产物与缓存都不落 C 盘），不存在则回落到系统 `dotnet`。它执行的发布命令与 CI 逐字一致，只有输出目录不同：

```bash
dotnet publish src/Beautiful.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseAppHost=true -o dist/Beautiful
```

产物约 260 个文件（自包含运行时占大头），入口为 `dist\Beautiful\Beautiful.exe`。`scripts\publish.cmd` 仍保留（输出到历史目录 `publish/`），但**面向使用的产物请以 `dist\Beautiful\` 为准**——`src\Beautiful.App\bin\` 下那份是框架依赖构建，需要 `DOTNET_ROOT` 才起得来。

## 发布（v2）

- **一键发布（本机）**：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1`
  流程：读取/改写版本号 → 工作树检查 → **构建 + 测试门禁（不绿不发布）** → 提交 `chore(release)` → 打 `v*` 标签并推送（推送失败会被退出码检出的硬失败，终审 M-3）。脚本**到此为止**——Release 创建与资产上传由 CI 独占。
- **云端发布（Release 资产唯一归属方）**：`.github/workflows/release.yml` —— 推送 `v*` 标签即由 GitHub Actions（windows-latest）执行 构建 → 测试门禁 → publish → 压缩 → 建 Release 上传资产（`Beautiful-win-x64-v<版本>.zip`）。本地与 CI 双写同名资产会互相竞态且 GitHub 拒绝重复资产名，故本地脚本不再上传（终审 M-4）。
- **版本号单一来源**：`Directory.Build.props` 的 `<Version>`；`release.ps1` 与 CI 均以它为准（`release.ps1 -Version x.y.z` 会同步改写该文件）。

## 使用说明

- **首次启动**：双击 `dist\Beautiful\Beautiful.exe`（由 `scripts\install-local.cmd` 生成；没自己构建就下载 Release 的 zip 解压，内容同一件产物）。顶栏出现在屏幕顶部、Dock 出现在屏幕底部（默认任务栏策略为"隐藏"，可在设置更改）；系统托盘出现 Beautiful 图标。
- **退出路径**：顶栏 logo 菜单"退出"，或托盘菜单"退出"（任务栏策略为"隐藏"时原生托盘不可见——**顶栏就是逃生入口**），或设置窗口。
- **切换器**：按住 Alt+Tab 弹出面板，同应用多窗口可展开选择，松开提交、Esc 取消。若接管行为异常，按 **Win+Alt+K** 立即应急停用（原生 Alt+Tab 当场恢复；重新启用去设置里打开开关）。
- **悬停预览**：鼠标在 Dock 窗口条目上停留 350ms 即弹出该窗口的实时缩略图；可在设置中关闭。
- **固定应用**：右键 Dock 图标选择"固定/取消固定"；固定清单保存在 `config/dock.json`，也可直接编辑该文件（应用列表）。
- **托盘菜单**：设置 / 主题（v4 只剩 macOS）/ 重载 Dock / 退出；双击托盘图标直接打开设置。
- **外观与字体**：设置页"外观"可选深色/浅色/跟随系统与 macOS 12 色强调色；底部状态行显示当前实际字体来源（内嵌 SF Pro / 系统 SF Pro / Segoe 回退）。
- **媒体说明**：媒体胶囊依赖播放器注册 SMTC 会话；v4 起只显示封面/标题/进度/三键，不再请求任何歌词服务。
- **日志排查**：运行日志位于 `<运行入口>\logs\Beautiful-yyyyMM.log`（自包含产物就是 `dist\Beautiful\logs\`），包含启动流程、条带几何与落位回读、会话发现、背板降级路径与异常记录。不用手抄：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\diag.ps1` 自动挑最新日志，连同每块屏的 `rcMonitor`/有效 DPI 与当前设置打成一整段（可 `-Path` 指定日志）。

## 已知限制

1. **QQ音乐 Win32 客户端与 PotPlayer 不注册 SMTC**，因此不会出现媒体胶囊，顶栏也没有媒体迷你项（QQ 音乐 Win32、PotPlayer 均未接入系统媒体控制）。
2. **网易云音乐 Win32 客户端需 SMTC 桥接插件**（如 [inflink-rs](https://github.com/apoint123/inflink-rs)）才会注册会话；注册后即可即插即用。
3. **双语歌词及其网络依赖已在 v4 Phase 1 删除**：`Core/Lyrics/` 与网易云/LRCLIB 提供器不再存在，运行期零音乐服务网络请求；这里保留记录是为了说明"以前为什么有"以及升级后旧配置字段 `LyricsOffsetMs` 只会被忽略并记一条迁移日志。
4. **真机复验项（v3.1 更新）**：本项目的开发与验证在**既不合成 DWM 背板、也不合成 DWM 缩略图**的虚拟机上进行，因此以下三项**在开发机上无法目视证明**，只能给出代码路径/日志级证据：① **Dock 底边全深色带**（缺陷 1）的消失——VM 里 `ACCENT_ENABLE_ACRYLICBLURBEHIND` 根本不产出那层填充，所以"黑带没了"只能在用户真机上看；本地能证明的是**源码里再无可写的非 Disabled accent 状态**，且运行日志每个常驻表面各出现一条 `旧亚克力=清除(state=Disabled(0))`。② **DWM 实时缩略图悬停预览**里实际显示的画面（缺陷 2）——本地能证明的是宿主窗口的 `DwmExtendFrameIntoClientArea(-1)`、`DwmRegisterThumbnail`、`DwmUpdateThumbnailProperties` 三条 HRESULT 与"注册数==注销数"；玻璃请求被拒（hr≠0）的环境现在**直接不弹卡片**，日志有一条只播一次的成因。诚实边界：本 VM 实测这一步 `hr=0x00000000`（日志 `预览宿主玻璃层 … → 缩略图可显形`），守卫并不触发，所以"缩略图真的画在屏上"这一条依旧只能真机目视——缺陷 2 里用户看到的实心黑框，本次是靠"玻璃边距恢复 + 类刷从黑色换成主题卡底色"两点一起处理的，最坏情况是一张与卡片同色的空卡而不是黑洞。③ **混合 DPI / 多屏 / AppBar 条带与系统真实保留区的错位**（v3 已有单测与 `scripts\diag.ps1` 对账，见第 7 条）。
   系统 Mica/Acrylic 属性路径（`DWMWA_SYSTEMBACKDROP_TYPE` / `DWMWA_MICA_EFFECT`）与旧 `SetWindowCompositionAttribute` 增强在 v3.1 都已退出渲染路径，不是任何表面的可见性前提；弹层的玻璃是自己抓屏算的高斯模糊，这一项在开发机上已目视验证（日志 `背板=抓屏模糊`）。
5. **隐藏任务栏后原生托盘不可见**：策略为"隐藏"时托盘图标随任务栏消失，退出需经**顶栏 logo 菜单"退出"**或设置窗口（启动时可用 `--settings` 参数直接打开）——顶栏即是逃生入口。
6. **强杀进程的 autohide 残留**：本进程被强杀时任务栏窗口由 explorer 自行摆回屏幕，但系统 autohide 位可能留在"开"——表现为任务栏缩到屏幕外、**把指针顶到屏幕最底行即可唤出**（不是卡死，无需重启 `explorer.exe`）；再次启动 Beautiful 并正常退出/切"保留"即清掉（只清这一位）。若用户自身的原生 autohide 偏好恰为"开"，退出 Beautiful 会一并清掉，可在系统设置重开。
7. **多屏与混合 DPI：代码已按屏取数，实机未复验**：v3 已把条带几何与副屏任务栏的判定全部改成按窗口所在显示器取（`MonitorFromWindow`/`MonitorFromPoint` + `GetMonitorInfo` + `GetDpiForMonitor`，见「不同分辨率、DPI 与多屏」），v2 那条"Dock 与顶栏按构造仅覆盖主屏 / `HideAll` 把副屏任务栏挪到主屏"的限制在代码层面已消除；但开发机是**单显示器 1024×768 @100%**，混合 DPI、非零原点副屏、拔屏等路径**只有单元测试与代码路径级验证**。真机上先跑 `scripts\diag.ps1`：`[显示器与 DPI]` 一节列每块屏的 `rcMonitor`/`rcWork`/有效 DPI/主屏标志，`[顶栏几何]`、`[Dock 几何]`、`[AppBar]` 三节给落位与协商数字，两边一对就能定位是哪一块屏、哪一步算错。
8. **Alt+Tab 接管的系统边界**：低级键盘钩子在 **UAC 安全桌面**（提权确认弹窗）收不到按键——此时切换器不出现、原生行为不受影响；提权窗口场景存在同类系统限制。游戏/独占全屏已做**全屏直通**（前台全屏时放行原生 Alt+Tab，可关）。如接管出现任何异常，**Win+Alt+K** 应急停用。
9. **只有"单显示器 @100%"这一条路径做过端到端目视**：不同 DPI 缩放（125%/150%）、原点非零的副屏、把 Dock 与顶栏分到两块屏（`DockMonitor`/`TopBarMonitor`）这些组合在这台开发机上根本产生不出来，属**单元测试 only**；悬停放大动画与 macOS 亮/暗外观的实际观感在本机未逐一实机验证，效果可能因机器而异。放大场/弹跳/倒影/预览/条带几何算术、token 亮暗取值均有代码级与单测级验证；**亮暗切换与 SF Pro 命中需所有者回执**。
10. **流畅度验收口径如实声明**：本项目对放大动画的验收门槛是"帧间隔 max < 32ms"。开发虚拟机实测 P95 帧间隔 **36.1ms**（丝滑重写后由 65.7ms 改进而来），**该门槛在本 VM 上未达标**，归因于 VM 分层窗口（WS_EX_LAYERED）合成路径的呈现下限；此数据**未在真实 DWM 合成的实体机上复验**，实机帧时间通常应优于 VM，但不保证。

11. **MSIX/UWP 启动器条目没有图标**：当前 Windows SDK 投影（19041）不暴露 logo 路径，取不到就是空框——不伪造、也不错画成资源管理器图标（条目本身可搜可启动）。
12. **Dock 拖拽交互由进程内 `FileDrop` 代理验证**：拖入固定 / 块内排序 / 甩出取消固定的处理路径由自己构造的 `DragEventArgs` 喂进同一条代码路径并逐项留日志，**没有用真实 Explorer 拖拽**复验。真机上若"拖进去没反应"，差异大概率出在 shell 侧的数据格式或 drop 效果回调，请把 `logs` 里的 `拖入固定` / `Dock 排序` 行发回来。
13. **壁纸取色已在 v4 Phase 1 删除**：`UseWallpaperAccent`、`WallpaperAccentService` 与控制中心砖块一并移除（被 macOS 12 色强调色板替代）；旧配置字段只忽略并记迁移日志。`DominantColor` 纯函数保留给 Phase 5 Launchpad 壁纸处理，暂不接线。
14. **安装向导是英文界面**：Inno Setup 6 官方只随包带 29 种语言，**没有简体中文**（简体属于第三方翻译包，版权归翻译者，不随手贴进仓库）。所以 `Beautiful-Setup-*.exe` 的向导是英文，应用本体是中文。宁可不装这个本地化，也不做"看起来像中文其实半吊子"的混合界面。
15. **安装位置是"按用户"而不是 `Program Files`**：这是功能正确性选择，不是省事——本应用把 `config\dock.json`、`logs\`、`cache\` 写在 exe 旁边，装进系统目录后这些写入会被 UAC 虚拟化到 VirtualStore，表现为"改了设置没生效 / 日志找不到"。安装器允许在向导里改路径，但改到系统目录后需要该目录可写。
16. **v3.2 新增能力的验证边界（如实分开写）**：这一轮的六项都做到了"构建 0 警告 + 单测全绿"，但**没有一项做过真机目视**——开发机的验证纪律是"能在纯函数层锁住的锁在纯函数层，锁不住的标 UNVERIFIED"。具体边界：
   - **弹层落位修复**：算术有单测钉住（卡片左上角=锚点、翻转贴卡片边），错位本身是在真机截图上量出来的（按钮左边 x=413 对卡片左边 x=495），修复效果需回真机看一眼。
   - **任务栏固定项导入**：读取路径与解析在本机实测过（找到 4 条，其中 "File Explorer.lnk" 的 TargetPath 为空，正是"收录 .lnk 本体"这条决定的依据）。
   - **托盘图标区**：跨进程取图与 Win11 24H2 的 UIA 字符串/矩形口径**未验证**；经典路的工具栏消息号与 `TBBUTTON` 布局只做了运行时闸门（读出来的东西不合理就退回 UIA 路）。取不到图就是中性占位。
   - **genie**：几何单测齐（端点、单调、不交叉、越界钳制），形状也用离屏线框逐帧看过
     （`t=0.15` 下缘先收、`t=0.45` 已成漏斗），但**纹理网格在真窗口上的实际渲染没有目视过**——
     WPF 3D 贴在分层窗口上这条组合需要实机确认；因此保留 `GenieEngine=strips` 作为一行配置即可退回的兜底。
     替身抓帧对 GPU/DirectComposition 窗口可能整幅纯色，此时判为不可用并**不做动画**（判据是 5×4 取样点全同＝纯色）。
   - **WiFi/音量面板**：profile XML、信号档位、排序、加密方式判定都有单测。真机日志已出现 `背板=抓屏模糊 面板=(1833,28)-(2573,292) …` + `WiFi 面板已打开`（面板打开与抓屏背板这条链在实机走通），但**实际扫描到的网络列表、连接结果与端点友好名仍未验证**。
   - **任务栏玻璃壳（已删除）**：v4 Phase 1 删除了整个模块与其几何/测试；控制中心的对应砖块与 `TaskbarGlass` 持久化字段一并移除。历史上它只在 VM 验到"该不该画"的几何单测，装饰效果从未真机目视。
17. **全屏避让在 v4 Phase 1 后暂时没有生产挂载**：`Core/Layout/FullscreenAvoidance` 纯函数与其 28 条测试保留，但唯一消费者（原生任务栏玻璃壳）已删除；Phase 3-T6 会把它接到 Dock/顶栏（真全屏→滑出/隐藏，退出恢复）。当前 Dock 与顶栏仍是无条件 `Topmost`，它们注册了 AppBar，但置顶带照样会压在别人的全屏内容上——这是 Phase 3 的已知欠账，不假装已解决。

### 未实现（有意延后）

以下能力 v3 **仍未实现**（避免"看起来像清单遗漏"的误解，明确列出）：

- Dock 停靠边可配置：`AppSettings.DockPosition` 只是模型字段，设置页与运行时都未接线（当前 Dock 固定底边、顶栏固定顶边）
- Dock 自动隐藏：`AppSettings.AutoHideDock` 同上，只有模型字段，未接线
- 中键关闭窗口
- 切换器按窗口显示实时缩略图列表（当前为按应用归并的图标列表）
- **托盘图标常驻排在顶栏上**（v3.2 只做"点开才读"的面板）：经典读取路是从屏幕截图上抠图标，常驻要么持续跨进程读 explorer，要么把 Windows 自己的隐藏区展开来抠——后者会在顶栏留下一次可见的展开动作，与"常驻表面不做实时抓屏"的裁定冲突
- **玻璃壳的运行指示条（已随玻璃壳删除）**：v4 Phase 1 不再有原生任务栏装饰层，这一项没有宿主；如果未来恢复任务栏美化线再议
- **逐应用混音器**（v3.2 音量面板只做主音量与静音）：`IAudioSessionManager2` 那一套是另一个量级的会话枚举，本版没做，面板里给的是"系统声音设置…"这条明确出路

> 本清单里此前写着"控制中心的**屏幕亮度**不做"，v3.7 已实现并从这条移走：走 `WbemScripting.SWbemLocator`
> 的 COM 自动化 + C# `dynamic`（`root\wmi` 的 `WmiMonitorBrightness` / `WmiMonitorBrightnessMethods`），
> 因此**没有**引入 `System.Management` 包，src 侧仍是零第三方依赖；读不到背光硬件时整行置灰并写中文原因，
> 不给一条死的滑杆。

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
| [ANYNC/TaskbarLyrics](https://github.com/ANYNC/TaskbarLyrics) | MIT | 全屏避让的 Z 序判定（`Core/Layout/FullscreenAvoidance.cs` 改写自其 `SmartTopmostPolicy`，详见 THIRD-PARTY-NOTICES 第 6 节）；其 WebView2/EF Core/OpenCC 栈一律未引入 |
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

## v4.0-alpha Phase 4 变更（菜单栏，进行中）

| 目标 | 现在 | 证据/边界 |
|---|---|---|
| 菜单栏 24px（P4-T1） | `BarHeightDip` 30→24、XAML 高度 24；token `MenuBarHeight=24` 已对齐；AppBar 条带同值 | 真实 Win11 smoke 截图确认排版无裁切；**Big Sur 并排对比 UNVERIFIED(owner)** |
| logo 菜单系统动作（P4-T2 起步） | 新增：系统设置… / Microsoft Store… / 任务管理器… / 睡眠 / 重新启动…（二次确认）/ 关机…（二次确认）/ 锁定屏幕 / 注销…（二次确认）；重启/关机会先走 Beautiful 完整退出链路；「关于本机」改为 Big Sur 风格非分层信息卡（壁纸缩略/机器名/型号/CPU/内存/Windows/字体，`--about` 可验） | 代码/XAML 编译；真实 Win11 `--about` 截图验证；**关机/重启/注销只做了二次确认，未在真机实际触发**（避免误操作） |
| 前台应用真菜单（P4-T3 起步） | 点顶栏前台应用名 → `GetMenu` 递归镜像真实 Win32 菜单，点击按命令 ID `PostMessage(WM_COMMAND)` 回发；自绘菜单应用（Chrome/msedge/Office/UWP）GetMenu 为空时给置灰说明，不伪造 | `MenuMirrorTextTests`；真实 Win11 smoke 用 msedge 验证了“无菜单”降级路径；**经典 Win32 应用（记事本等）的真菜单回发未逐项验证** |

| 控制中心 Big Sur 瓦片（P4-T4） | 连接组 2×2（Wi-Fi 真开关 / 蓝牙打开系统页 / 隔空投送置灰写明"Windows 无对应能力" / 专注模式打开专注助手）；显示与声音各收进整块瓦片；外观分段限宽；Dock 开关砖块与任务栏策略保留 | 真机截图 `logs/4-t4-control-center2.png`；`--control-center` 截图入口；**正在播放瓦片未做**（需复用 Dock 的 SMTC 实例） |
| Dock 时钟/天气小组件（P7-T3） | Dock 行尾（媒体胶囊之后）一块 112 DIP 的两行信息块：上行 `22:17`、下行 `29° 雾`；时钟由本地定时器 10s 刷一次（不联网），天气走 `wttr.in?format=j1`（免密钥、按出口 IP 定位、15 分钟缓存、8s 超时、失败静默保留上次读数）；点它开通知中心（含月历），与顶栏时钟同一语义；设置页可关（关掉即从行尾消失、不再联网） | `DockWidgetTests`（22：24/12 小时制、温度取整与 `--°` 占位、WWO 代码映射、**实测代码 149 用英文描述兜底**、缓存过期）；真机截图 `logs/dock-widget.png`（22:17 + 29° 雾，Guangzhou）；`WeatherService.Parse` 单测覆盖字段缺失；**天气依赖第三方免费接口，离线时只有 `--°`，不编造温度** |
| Dock 角标（P7-T2 起步） | `DockBadge` 纯规则（0 不画、99 封顶、从窗口标题最后一段括号解析未读）+ `DockSurface` 角标层（红色胶囊压在图标右上角，随放大一起缩放，只覆盖状态不改 `DockItem`）；`DockBadgeService` 每 3s 读白名单应用（微信/QQ/TIM）标题，同应用多窗口取最大值 | `DockBadgeTests`（15：格式化/封顶/括号解析/最右括号/进度钳制）；`--dock-badge 3` 真机截图 `logs/dock-badge.png`；**本机未装微信/QQ，实时未读→角标链路未端到端验证**（标 UNVERIFIED(owner)）；非白名单应用不显示角标——没有角标是"不知道"，错角标是"撒谎" |
| Dock 进度条（P7-T1 起步） | 图标下沿一条 3 DIP 细带（白色正常 / 红色出错 / 黄色暂停 / 不确定态静态段），随图标缩放；数据源按 MacDock 的实测结论只走白名单轮询——`ITaskbarList3` 的进度状态跨进程读不到，唯一可用路径是 Explorer 拷贝对话框（窗口类 `OperationStatusWindow`，标题为空）的 UIA 进度值（`56%` 或 RangeValue）；只映射到 `explorer.exe`，没接的应用不画 | `DockBadgeTests` 扩充到 24 条（新增 `ParsePercentText`：`56%`/`56 %`/`105%`→1、裸数字与乱码→-1）；`--dock-progress 0.42` 真机截图 `logs/dock-progress.png`；`--progress-probe` 在无拷贝时报 `found=False`（不崩、不误报）；**真实拷贝对话框→UIA 读数未端到端验证**（本机无法在无人值守下稳定制造长时间拷贝对话框），标 UNVERIFIED(owner)；不确定态暂为静态段，无来回动画；同步修正：采样 700→300ms、多条进度条取**最宽那条**（总体而非当前项）、连续 3 次找不到才清空（防对话框换阶段闪烁）、首次见到对话框 dump 各条 name/value/range 到日志；**实测 Win11 24H2 的 DirectUI 对话框 UIA 里根本没有 ProgressBar（日志"共 0 条"）**，已加兜底：扫全部元素名字里的百分数（取最宽匹配），并把前 30 个元素名字 dump 出来供下一步定位 |
| 自定义 Dock 图标（配合图标站） | Dock 右键「更换图标…」→ 选 PNG/ICO/JPG/BMP → Core `DockIconOverride.Validate` 校验（格式白名单、非空、≤8MB、必须真实存在）→ 写 `config/dock-icons.json`（只存用户自己选的路径）→ 重载固定项；覆盖过时菜单多出「恢复默认图标」。失效文件（被删/格式不支持）在载入阶段就丢弃并回落系统图标，绝不显示空白 | `DockIconOverrideTests`（5：合法/扩展名/不存在/空文件/空选择）；真机截图 `logs/dock-icon-override.png`（第二格换成自定义图）；**不内置、不分发任何 Apple 图标**——图标来源由用户自选（macosicons 等站点，注意各自 license） |
| 逐应用混音（P7-T5） | `IAudioSessionManager2` 互操作 + `AudioSessionService`（按进程聚合、读/写会话音量与静音）；控制中心「逐应用音量」分组（图标/名称/静音/滑杆/读数 + 刷新） | 真机截图 `logs/7-t5-mixer.png`：枚举到"系统声音"会话可调；**会话列表是快照**（不订阅会话通知）；其它应用需有音频会话才出现 |
| 通知横幅（P4-T5 续） | 新通知在右上角弹出 macOS 式横幅：应用字标/名称/相对时间/标题/正文（最多两行），最多同时 3 条向上堆叠、旧条被挤掉；3.8s 后 220ms 淡出，悬停暂停计时，点击按 AUMID 打开发通知的应用。横幅不持久化——历史始终只有通知中心一份（避免"同一条看两遍"）；设置页可关 | `--banner-test` 用固定示例内容截图 `logs/banner-01.png`（不含真实通知）；`Arrived` 事件由 `NotificationStore.Apply` 的"新到达"驱动，首次同步不弹；**真实到达→横幅链路需真机等一条通知**，标 UNVERIFIED(owner) |
| 音量 HUD（P4-T7） | 按音量键弹出 macOS 式 HUD：16 格音量条 + 四档喇叭图标（含静音斜杠），上方居中、不抢焦点、点击穿透、900ms 后 250ms 淡出。为此接管 `VK_VOLUME_UP/DOWN/MUTE`（吞掉按下与抬起）并直写端点音量——不吞的话系统音量浮层会和自家 HUD 同时出现；步进按 macOS 的 1/16（6.25%）而非 Windows 的 2%，且吸附格点。设置页可关（关掉后媒体键完全回到系统行为） | `VolumeOsdTests`（8：步进吸附/连按 16 次到 100/亮格取整/四档图标/淡出曲线/钳制）；`--osd-volume N` 与 `--osd-mute` 截图 `logs/osd-18|62|100|mute.png`；**亮度 HUD 未做**（台式机外接屏无通用亮度 API，笔记本 WMI 路径未接）；媒体键实按属交互类 UNVERIFIED(owner) |
| 通知中心（P4-T5） | 右侧贴边全高面板：今天 widget（大时钟 + 日期 + 复用 `CalendarFlyout` 的月历）+ 通知卡片列表（应用字标/名称/相对时间/标题/正文两行）+ 清除 + 空态；顶栏时钟左键开合（右键仍是月历浮层），设置页可关；点卡片按 AUMID `shell:AppsFolder\` 打开发通知的应用。数据走进程内 `UserNotificationListener`（.NET 8 直连，不用 sidecar）；首次同步只做基线、按 id 去重并集、上限 100 条、系统侧已清掉的旧条目仍保留在历史 | `NotificationStoreTests`（14：基线/去重覆盖/保留旧条目/上限丢最旧/相对时间六区间/脏文本清洗）；`--notif-probe` 真机实测 `access=Allowed raw=5 usable=5`；`--notification-center` 截图 `logs/nc-02.png`（5 条真实通知、月历当天高亮）；**`NotificationChanged` 事件在非打包进程订阅即 0x80070490 → 已降级为 10s 轮询**（事件路径保留，机器支持时自动用）；未授权时面板显示"无法读取通知"+打开系统设置，不假装没消息 |

> Phase 4 已完成"24px + logo 菜单真实系统动作 + 关于本机信息卡 + 前台应用真菜单镜像（含自绘菜单降级）+ 控制中心 Big Sur 瓦片 + 通知中心 + 音量 HUD"；
> **亮度 HUD、托盘真实像素（注入）、蓝牙/输入法/显示器状态项、正在播放瓦片仍待后续**。

## v4.0-alpha Phase 5 变更（Launchpad/系统级，进行中）

| 目标 | 现在 | 证据/边界 |
|---|---|---|
| 启动台几何优化（移植 MacDock） | 列/行不再写死 7×5：`LaunchpadGridRules` 由可用宽高反推（3–9 列、图标优先不缩、窄屏才缩到 48 DIP），图标尺寸随列宽自适应；搜索条按 MacDock 的 480×40/圆角 20；文件夹 chip 条（有夹才占位，点开覆盖层）；悬停图标放大 1.10（90ms/120ms 双程缓动）；开合动画 0.96↔1 + 淡入（180ms 开 / 140ms 关） | `LaunchpadGridRulesTests`（8：宽屏更多列/上限 9/窄屏仍 3 列/图标优先/退化输入不炸）；真机截图 `logs/launchpad-grid.png`（9 列×6 行、4 页点、文件夹合成图标 + chip）；**悬停放大与开合动画属交互类，UNVERIFIED(owner)**；启动台内拖拽建夹仍需手拖验证 |
| Launchpad 文件夹（P5 收尾） | 文件夹在启动台里排在最前（2×2 成员缩略图合成图标）；把应用拖到另一个应用上 → 建文件夹（Core `LaunchpadFolders.Create`），拖到文件夹上 → 加入；点文件夹开覆盖层（成员网格 + 完成按钮），右键成员 → 移出（剩 1 个时按 Core 解散判据整个文件夹消失）；布局落 `config/launchpad.json`（损坏自愈、成员少于 2 的文件夹读时丢弃、卸载过的应用不渲染空框） | `--launchpad-folder-demo` 真机截图 `logs/launchpad-overlay.png`（覆盖层 + 两个真实成员）；`--launchpad-folder <名>` 可直开指定文件夹；**拖拽建文件夹/加入需手拖验证（UNVERIFIED(owner)）**；文件夹重命名与"从覆盖层拖出"未做（移出走右键）；搜索时不显示文件夹（只搜应用） |
| 触发角（P5-T2 起步） | Core `HotCornerPlan` 四角命中 + `HotCornerDwellState` 驻留状态机（300ms、触发后须离开再武装）；`HotCornerService` 100ms GetCursorPos（不装鼠标钩子），真全屏时抑制；设置页新增「触发角」四角下拉：禁用/显示桌面/锁定屏幕/睡眠（默认全禁用） | `HotCornerPlanTests` + 1058 测试通过；**Launchpad/屏保动作未提供**（依赖 Phase 5 Launchpad 与屏保接线，不放假选项）；真机四角触发 UNVERIFIED(owner) |
| Launchpad（P5-T1 起步） | F4 全局热键（RegisterHotKey，失败只记日志）；全屏 7×5 网格、壁纸模糊底、搜索过滤、翻页/滚轮、Enter/点击启动；复用 LauncherService 索引（171 条真实应用 smoke） | 真实 Win11 `--launchpad` 截图验证；**文件夹、拖拽排序、自定义图标/持久化、Dock 启动台图标、壁纸幻灯片降级仍待后续**；搜索占位已修 |
| Spotlight 视觉（P5-T4） | 卡片 680 宽/圆角 12、搜索行留白与 20px 放大镜、结果行 32px 图标 + 主标题/副标题两行、选中态 ThemeAccent；`--spotlight` 截图入口 | 真机截图 `logs/5-t4-spotlight.png`；**窗口打开动画（P5-T3）仍未做**（依赖 native 幽灵渲染） |

> Phase 5 已完成触发角、Launchpad 基础版与 Spotlight 视觉校准；**窗口打开动画（P5-T3，需 native 幽灵渲染）与 Launchpad 文件夹/拖拽仍待后续**。

## v4.0-alpha Phase 6 变更（访达，进行中）

| 目标 | 现在 | 证据/边界 |
|---|---|---|
| Finder Core 地基（P6-a 起步） | `Core/Finder`：`NavigationHistory`（后退/前进栈、去重、容量裁剪）、`FinderPaths`（父目录/根/显示名/组合，含 UNC）、`NaturalStringComparer`（数字感知排序）、`FinderKeyMap`（Ctrl 充当 ⌘ 的键位映射）、`FinderSorting`（目录优先/名称/大小/时间/种类，稳定排序） | `NaturalStringComparerTests` 等 5 个新测试文件；**1058 测试通过**；纯 Core，无 App/WPF 接入 |
| Finder 窗口骨架（P6-a 起步） | 非分层 WindowChrome + DWM 圆角；52px 工具栏（红绿灯/前进后退/路径标题/搜索/视图占位）；190px 侧栏（最近使用/应用程序/桌面/文稿/下载/这台电脑）；ListView 四列（名称/修改日期/大小/种类）；异步枚举+取消、1500 截断、状态栏项目数/可用空间；双击/Enter 打开、Ctrl+[ ]、F5、Esc | 真实 Win11 `--finder` 截图验证（79 条目、状态 27.9 GB）；随后 hardening：载入态清空旧行/CTS 生命周期/空间缓存/后台图标（前 300 项）/导航清搜索；6-b 起表头可点击排序、⇧⌘. 显示隐藏项；**文件操作 UI/画廊仍未做** |
| Finder 文件操作规划 Core（P6-a 起步） | `FinderNameRules`（非法/保留名、尾空格点、255 限制、唯一名 `(n)`、复制名 copy）、`FinderPathSafety`（同/后代路径、目录不能移进自身）、`FinderDropPlanner`（去重/顺序/冲突改名/目标组合） | `FinderNameRulesTests`/`FinderPathSafetyTests`/`FinderDropPlannerTests`；**1058 测试通过**；纯 Core，尚未接 IFileOperation 与 UI |
| Finder 分栏视图（P6-b） | `FinderColumnState`（列栈/每列选中/焦点列；选择即截断右侧、选中文件夹才开新列、列数上限 8）、`FinderColumnChain`（任意目录 → 祖先链，相对路径不伪造绝对路径）、`FinderColumnMetrics`（中间列 260 固定、最右列吃剩余宽度）；窗口侧整列重建 + 父列高亮展开的子目录 + 最右预览列（图片缩略/文本头 12 行/图标 + 种类/大小/时间）；←/→ 列间移动、后退优先退一列 | `FinderColumnViewTests`（21）；真机 `--finder --view columns --select package-lock.json` 截图：C:\ → Users → Chenkx 三列 + 预览列显示 JSON 头 4 行与元数据；**列宽拖拽、水平滚动定位未做** |
| Finder 图标视图（P6-b） | `FinderIconGrid`（度量/命中/框选/方向键/尺寸档位 48-64-96-128）+ `FinderIconPanel`（自绘面板，布局与命中同一套公式）；框选（空白起手、Ctrl/Shift 并集）、方向键网格移动（左右不换行）、尺寸滑杆 + ⇧⌘[ / ]、双击打开 | `FinderIconGridTests`（10）；真机 `--view icons` 截图：8 列网格、真实系统图标、两行标签；**非虚拟化（目录已截断 1500 项）、标签换行未做逐行省略** |
| 每文件夹视图选项（P6-b） | `FinderViewOptionRules`（值域钳制/路径键归一/容量上限 200/枚举字符串往返）+ `FinderViewOptionsStore`（`config/finder-view-options.json`，损坏自愈不删文件）；视图/排序列/图标尺寸/隐藏项按文件夹记忆；列表表头点击排序（同列翻转，▲/▼） | `FinderViewOptionsTests`（11）；真机截图确认列表表头「名称 ▲」与图标尺寸滑杆随视图显隐 |
| Quick Look（P6-b） | `QuickLookRouter`（扩展名 → 文本/图片/媒体/PDF/信息卡）+ `QuickLookPager`（多选循环翻页）；窗口：文本 64KB BOM 感知、图片 OnLoad 解码、MediaElement 播放、**PDF 走 `Windows.Data.Pdf` 渲染首页**（StorageFile 失败自动退内存流）、失败一律信息卡并写明原因；多选空格 ←/→ 翻页、空格/Esc 关闭 | `QuickLookRouterTests` + `QuickLookPagerTests`（5）；真机 `--quicklook logs/pdf-smoke.pdf` 截图渲染出页面与「PDF 共 1 页」；**加密/系统 PDF 引擎不接受的文件回退信息卡（`logs/task05-sample.pdf` 实测 0x80048014）**；HEIC/IPreviewHandler 探针未做 |
| 显示简介（P6-b） | `FinderInfoWindow`（⌘I / `--info`）：通用（种类/大小/占用磁盘/创建/修改）+ 打开方式（AssocQueryString 友好名）+ 位置（可选中路径）+ 权限与属性（只读/隐藏/系统，只读展示） | 真机截图：PDF 卡显示 595 字节、占用磁盘 595 字节、默认程序 Microsoft Edge；**改权限/改打开方式不提供**（系统写入，按 §16.8 留给所有者） |
| 文件操作（P6-c） | `IFileOperation` 互操作（复制/移动/回收站/重命名/新建文件夹）+ macOS 风格进度卡（当前项/进度/停止）；取消走 `IFileOperationProgressSink.Pre*Item` 返回 `E_ABORT` 软中断；删除降级链 `IFileOperation → VB 回收站 API`；剪贴板 = 进程内 `FinderClipboard` + 系统 `CF_HDROP`（可与资源管理器互拷） | `FinderClipboardTests`（13）+ `--fileop-smoke` 真机链路：建/拷/改名/回收站全部 PASS，无残留；**跨进程剪切降级为复制**（README 已写明） |
| 标签页（P6-c） | `FinderTabSet`（每标签独立 `NavigationHistory`）+ Big Sur 标签条（多标签才出现）；⌘T 新建（插在当前右侧）、⌘W 关标签（最后一个才关窗）；标签点击切换并恢复各自视图选项 | `FinderTabSetTests`（14）；真机 `--tabs` 截图：4 标签、激活态高亮 |
| 搜索（P6-c） | 当前文件夹实时过滤（零 IO）+「这台 Mac」受控递归：`FinderSearchLimits` 深度 8 / 2000 条 / 8 秒、跳过重解析点（防目录环）；结果带「位置」列；Esc 退出搜索 | `FinderSearchTests`（9）；真机 `--search .cs`：776 命中、状态栏文案正确；**不是全局索引**（Everything 式索引不在范围） |
| Dock 互通（P6-c） | Dock 文件夹堆栈「在访达中打开」→ 自家访达（目录直接导航，文件打开所在目录） | 代码路径；真机点击 UNVERIFIED(owner) |
| 画廊视图（P6-c 收尾） | 第四种视图（⌘4 / 工具栏第四态）：上方大预览 + 下方横向胶片条。布局/容量/命中/滚动/方向键/大图缩放全部是 Core `FinderGallery` 纯函数；图片条目自己后台解码 84×62 缩略图（shell 图标对 jpg 只会给"图片程序"图标），其余回退 shell 图标+字形；文本给头 40 行等宽预览（每行字符数按可用宽度换算）、文件夹/PDF/媒体给大图标 + 名称/种类/大小/时间的信息卡；←/→/↑/↓ 移动选中、空格 Quick Look 带当前项、双击缩略图/预览打开 | `FinderGalleryTests`（10 条：容量/内容宽/原点/命中含 6px 间隙/滚动居中与两端夹取/方向键夹取/不放大小图/非法源尺寸）；`--finder --path <dir> --view gallery --select <file>` 真机截图 `logs/6c-gallery.png`（412 项、真实缩略图、蓝框选中）、`6c-gallery-text.png`（README 文本预览）、`6c-gallery-folder.png`（文件夹信息卡）；列表/图标视图回归截图 `6c-regress-*.png`。**画廊为单选**（⌘A 只给提示），胶片条非虚拟化、缩略图解码只做前 200 条——边界已写进代码注释 |
| 视图快捷键顺序修正 | 审计发现 Core `FinderKeyMap` 写的是 ⌘1 图标/⌘2 列表/⌘3 分栏/⌘4 画廊，而窗口实现把 ⌘1 当列表——两处相反。以 macOS 访达为准统一为 ⌘1 图标、⌘2 列表、⌘3 分栏、⌘4 画廊，工具栏提示同步 | `FinderKeyMapTests` 既有映射未变；`FinderViewOptions` 往返测试补 Gallery；窗口键位与 Core 表一致 |
| 标签拖出 / 注册为文件夹打开处理器 | **未做** | 拖标签出窗仍在 P6-c 清单上；DF-1（改写文件夹打开处理器）按 §16.8 留给所有者决策 |

> Phase 6-b 已完成分栏/图标两视图、Quick Look（含 PDF 首页渲染）、显示简介与每文件夹视图选项；
> Phase 6-c 已完成文件操作、标签页、搜索、Dock 互通与**画廊视图**（四视图齐了）。
> **剩余：拖标签出窗、注册为文件夹打开处理器（决策点 DF-1）**。
> 真机截图证据存于 `logs/6b-*.png` / `logs/6c-*.png`（不入库，`logs/` 已 gitignore）；注入类/拖拽类交互仍为 `UNVERIFIED(owner)`。
