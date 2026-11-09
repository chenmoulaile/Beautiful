# Beautiful

Windows 11 的桌面还原壳：悬浮 Dock（Big Sur 风）+ macOS 菜单栏 + Alt+Tab 应用切换器 + 悬停窗口预览 + Spotlight 启动器 + 控制中心 + 访达 + 台前调度。WPF + .NET 10，自包含发布，中文界面。

> 版本 `5.0.0`（单一来源：`Directory.Build.props`，scripts/release.ps1 与 CI 都读它）。
> 界面截图不入库（`logs/`、`docs/` 已 gitignore），观感以发布包实跑为准。
> 完成度一律以**实测**为准：没跑过的写"未验证"，不写"已支持"。

## 现在能用的

**Dock**
- 固定应用 / 运行窗口 / 废纸篓 / 媒体胶囊，连续高斯放大、悬停 DWM 实时缩略图、genie·zoom·suck 最小化动画。
- 角标：微信/QQ/TIM 窗口标题里的未读数 → 图标右上角红色胶囊（非白名单不画）。
- 进度条：Explorer 拷贝对话框的 UIA 读数 → 图标下沿细条，300ms 采样 + 逐帧插值。
- 行尾小组件：时钟 + 天气（wttr.in，15 分钟缓存，离线显示 `--°`）。
- 右键菜单：固定/取消固定、打开文件位置、属性、更换图标（自定义 PNG/ICO，只存本地路径）。
- 隐藏方式九档（`dockHideMode`，对标 MyDockFinder）：`off` 常显 / `auto` 自动隐藏（离开 0.5s 滑下只露 4px，触底滑回）/ `smart` 智能隐藏（前台 + 全量遮挡采样，背景窗口盖住 Dock 也让位）/ `fullscreen` 全屏隐藏 / `maximized` 最大化隐藏 / `edge` 双击边缘开关 / `modifier` 按住修饰键显示（默认 Ctrl，可配）/ `desktop` 仅桌面显示 / `dim` 半透明 45%（离开时降，移上去恢复）。smart 档的遮挡采样全程零 SEND 消息（`OccludingWindowProbe`），卡死窗口拖不住 UI 线程。
- 多屏：Dock 与顶栏可分处不同显示器（`dockMonitor` / `topBarMonitor`：auto=光标所在屏、primary、或 1 起的序号），每屏还有独立的 Dock 覆盖配置（`dockMonitorProfiles`，图标尺寸/隐藏模式/停靠边可按屏差异化）。
- 停靠边：底/左/右（左右停靠走独立竖排面板，无放大场/倒影、不占工作区，实验）；新窗口淡入（`AnimateWindow`，跨进程不保证生效）。

**动画（genie/mesh，参数全量热加载）**
- 四条触发路径（Dock 点击 / 标题栏按钮 / Win+↓ / 任务栏）的最小化与还原动画；Dock 点击走"ShowWindow 前同步预抓"，其余三条靠前台帧缓存兜底（2Hz 采样、700ms 新鲜龄、1500ms 判废、1 全 4 半 LRU）。
- 全部时长/比例在 `config/anim-params.json`，**改文件即热加载**（首次运行自动生成默认表）；逐字段的"为什么"见 `Core/Animation/AnimParams.cs`。调参台 `tools/AnimLab/`（独立 WPF 小工具，复用同一份 JSON 校验，逐帧预览打开/关闭/genie/最大化还原 + 实时帧间隔图）。
- 门禁（Core AnimPolicy，纯函数+单测）：风暴抑制（200ms 内 4 个最小化整批不播）、并发幽灵 ≤3、同进程 300ms 节流、排除清单、全屏零轮询不播、速度倍率 0.5-2.0。
- 保真度两档（诚实声明）：**我方触发（Dock）= 完整编排档**（预捕获）；**系统触发 = 事后补偿档**（事件到手时窗口已 iconic，播缓存帧），允许 <100ms 过渡痕迹。MyDockFinder/Lume 靠注入拿完整编排权，本程序不注入，差距如实写。
- 每次播放/判废都有结构化日志（`动画触发：源= 层= 引擎= 时长= 帧数= P95=` / `动画未播：源= 层= 因=`），`scripts/diag.ps1` 的 [动画] 节汇总触发率与判废分布；取证脚本 `scripts/verify/anim-trigger-matrix.ps1`。
- 已知限制：24H2 不响应 SPI_SETANIMATION（系统"缩向任务栏"动画可能并存，有 WARN 行）；mesh 引擎 P95 80-146ms（native DComp 渲染器是根治路径，待工具链）；从后台进程触发的"冷启动第一下"不播（缓存无帧，真实交互不受影响）。
- 窗口关闭动画（**默认关**）：macOS 忠实——关窗口没有动画，这不是没做完。设置里显式打开后，第三方窗口销毁用前台缓存帧在原位播 150ms 的 fade+scale→92%（缓存龄 >1000ms 不播——窗口销毁后实时抓帧物理上不可能，画面只能来自缓存，所以只有"刚才还是前台"的窗口才有）；自家窗口（设置/关于本机/访达）始终走真窗口 fade+scale，不受该设置控制。

**菜单栏**
- 24px 高，logo 菜单真动作、前台应用菜单镜像、时钟/日历、音量/网络/电量、媒体迷你项。
- 控制中心：Big Sur 瓦片 + 系统状态行（电量/网络/蓝牙/输入法，5s 采样）；顶栏时钟左侧有输入法徽标（中/EN/あ）。
- 通知中心：右侧全高面板（大时钟 + 月历 + 通知卡片 + 清除），点顶栏时钟开合；新通知右上角弹横幅。
- 音量 HUD：按音量键弹横向胶囊（图标 + 16 根竖条），接管媒体键以取代系统浮层。
- 温度芯片（**默认关**）：CPU 温度来自 LibreHardwareMonitor / OpenHardwareMonitor 经 WMI 发布的传感器（用户自装的工具），兜底 ACPI 热区；没有任何数据源时芯片整体隐藏，绝不显示编造的读数。轮询间隔 5-120s 可调（默认 5s）。
- 托盘入口：顶栏托盘区入口点开显示被系统收起的第三方图标，点击/右键转发回图标自己的窗口；Dock 行尾还有托盘收纳列（`dockTrayPosition`，点击展开弹层、3s 自动重读，**默认关**）。

**启动台**
- 列/行随屏幕自适应（3–9 列，图标优先不缩），搜索、翻页、悬停放大、开合动画。
- 文件夹：拖应用建夹、拖入、覆盖层、右键移出，`config/launchpad.json` 持久化。

**访达**
- 列表 / 分栏 / 图标 / 画廊四视图，Quick Look（含 PDF 首页）、显示简介、每文件夹视图选项、标签页、搜索、文件操作（IFileOperation）。

**切换器 / 台前调度 / 其它**
- Alt+Tab 替换切换器（macOS 卡片式）；呼出热键 `SwitcherOpenHotkey` 默认 `ctrl+grave`（Ctrl + `），只开面板、不吞系统 Alt+Tab。
- 台前调度（**默认关**）：屏幕左缘舞台条，靠近展开显示各应用最近窗口的 DWM 实况缩略图，点击切换（最小化自动还原）；收合/展开有滑动动画（可关）。
- 屏幕四角圆角遮罩（**默认关**）：每屏四角浮输入穿透小遮罩（半径 4-48 DIP、遮罩色可配、每屏排除名单），整块屏幕看上去就是 macOS 的圆角。
- 墙纸自适应取色（**默认关**）：随当前桌面壁纸色相/饱和度轻混一层 tint（强度 0-100，默认 60；幻灯片/纯色/灰阶壁纸不取色），Dock 玻璃三停按权重混入——玻璃仍是玻璃。
- Aurora 极光主题：第二套完整主题（Inter 字体合规入库、accent 强调色），设置里与 macOS 一键切换。
- 切换器/Spotlight 视觉、触发角、Dock/顶栏全屏避让。

## 本地运行

```powershell
# 1) 下载 Release 的 zip，解压后直接运行 Beautiful.exe（自包含，无需 .NET）
# 2) 或从源码构建
dotnet build -c Release
src\Beautiful.App\bin\Release\net10.0-windows10.0.19041.0\Beautiful.exe
# 3) 或一键发布到固定目录
scripts\install-local.cmd
```

常用诊断参数（均不改用户设置）：

| 参数 | 用途 |
|---|---|
| `--finder --path <dir> --view gallery` | 访达指定目录/视图 |
| `--notification-center` / `--banner-test` | 通知中心 / 横幅 |
| `--osd-volume 62` / `--osd-mute` | 音量 HUD |
| `--dock-shot` / `--dock-badge 3` / `--dock-progress 0.42` | Dock 截图 / 角标 / 进度条 |
| `--dock-edge left\|right` / `--dock-autohide` | 停靠边实验 / 自动隐藏 |
| `--tray-capture` | 顶栏托盘真实像素截屏 |
| `--notif-probe` / `--progress-probe` | 通知与拷贝进度探针 |
| `--launchpad --launchpad-folder <名>` | 启动台与文件夹 |

## 构建与门禁

```powershell
dotnet build -c Release          # 0 警告 0 错误
dotnet test tests/Beautiful.Core.Tests
python tools/xaml-check/xaml_check.py .   # XAML↔代码一致性静态网
```

- SDK 由根 `global.json` 钉死（.NET 10，latestFeature），构建产物不依赖"哪台机器装了什么"。
- 最新实测：**build 0 警告 / 0 错误；test 1989 通过 / 0 失败**（2026-11 全面审查实跑；日常以 CI 为准）。
- 每次推送都过的 CI 门禁：`.github/workflows/ci.yml`（native /W4 /WX + NativeSmoke + XAML 静态网 + sln 构建 + 全量测试 + Linux 交叉编译哨兵）；`v*` 标签走 `release.yml` 打 zip 与安装包。

## 结构与边界

- 分层：`Beautiful.Core`（纯函数与规则，全部可单测）/ `Beautiful.App`（WPF 外壳）/ `Beautiful.Native`（C++ 材质与捕获，可选）。
- 视觉：`Core/Design/MacTokens` 与 `AuroraTokens` 单一事实源（亮/暗两套），App 侧注入 `Mac*` / `Aurora*` 资源键；不许新增硬编码色值或 Segoe 字形码点。
- **托盘真实像素（截屏路线，实验）**：顶栏托盘面板顶部按 2s 截系统通知区并显示；截到纯色（任务栏被隐藏）时整块隐藏，不展示假图。以下为旧记录：——探针证明 UIA 读不到，改走截屏：`--tray-capture` 能按 `TrayNotifyWnd`
  矩形截到图，但在**默认的任务栏隐藏策略下系统托盘本身是空的**，截到的是空白条；要真正显示真实图标，
  需要用户保持系统任务栏可见（或改走 native 捕获 + 自绘）。左右停靠的放大/倒影与工作区预留、托盘真实像素的呈现同样未做。
- 已知差异：第三方拷贝进度只能轮询（原生任务栏是进程内直写，跨进程无接口）；天气依赖第三方免费接口。
- 交互类（悬停放大、拖拽建夹、点通知跳转）仍需人工点一次确认，代码与提交信息标 `UNVERIFIED(owner)`。

## 发布

推送 `v*` 标签触发 `.github/workflows/release.yml` 构建 zip 与安装包（CI 独占 Release 资产），包内随附《测试清单.md》验收单（A/B/C 验收组 + 已知限制 + 异常记录栏）。
