# Beautiful

Windows 11 的 macOS 桌面还原壳：悬浮 Dock（Big Sur 风）+ macOS 菜单栏 + Alt+Tab 应用切换器 + 悬停窗口预览 + Spotlight 启动器 + 控制中心 + 访达。WPF + .NET 8，自包含发布，中文界面。

> 版本 `4.0.0-alpha.13`。界面截图不入库（`logs/`、`docs/` 已 gitignore），观感以发布包实跑为准。
> 完成度一律以**实测**为准：没跑过的写"未验证"，不写"已支持"。

## 现在能用的

**Dock**
- 固定应用 / 运行窗口 / 废纸篓 / 媒体胶囊，连续高斯放大、悬停 DWM 实时缩略图、genie·zoom·suck 最小化动画。
- 角标：微信/QQ/TIM 窗口标题里的未读数 → 图标右上角红色胶囊（非白名单不画）。
- 进度条：Explorer 拷贝对话框的 UIA 读数 → 图标下沿细条，300ms 采样 + 逐帧插值。
- 行尾小组件：时钟 + 天气（wttr.in，15 分钟缓存，离线显示 `--°`）。
- 右键菜单：固定/取消固定、打开文件位置、属性、更换图标（自定义 PNG/ICO，只存本地路径）。
- 隐藏方式三档：常显 / 自动隐藏（离开 0.5s 滑下只露 4px，触底滑回）/ 半透明（离开时 45%，移上去恢复）。
- **实验**：停靠边可切左/右（竖排面板 + 悬停放大 1.12，无高斯放大场/倒影，且不占工作区）。新窗口淡入已由下方「打开 / 关闭」的 ghost 版接管（alpha.13），AW_BLEND 旧路径仅在 ghost 版被关闭时兜底。

**打开 / 关闭（v4.0-alpha.15）**
- 打开（默认开）：EVENT_OBJECT_SHOW 触发，占位帧设计——替身先以全透明占住窗口矩形，
  PrintWindow 后台重试 8ms×12，就绪当帧换入真画面起播（360ms easeOutQuint，0.90→1.0；
  **alpha.15 起真窗口 DWM Cloak，替身成为真入场而非叠在已可见窗口上的重影**；Cloak 不可用时自动回退淡入模式）；
  只有"第一次显示"是打开，hide/show 回归不重播（macOS 语义）。实验形态 `fromIcon`（从 Dock 图标飞出）默认关。
- 关闭（**alpha.15 起默认开**）：DESTROY 触发，画面取 2Hz 前台缓存帧，龄超 1s（`CloseCacheMaxAgeMs`）判废；
  260ms 收拢→92% 淡出；追索 mac "关闭干脆消失"语义可在设置里关回。
- 时长/比例全部在 `config/anim-params.json` 热加载（alpha.15 起时长类参数对运行中的主程序**即时生效**）；
  设置页"动画速度"滑条同样即时生效（alpha.15 接线）。
- 自家窗口（设置/关于本机）一律真动画：内容收拢后真正 Close，不受上面的开关约束。
- Dock 启动弹跳：点未运行应用 = 图标**反复弹跳**至首窗出现收口（或 10s 超时）；
  单击已运行应用仍是单次弹跳。
- 挂载自检行扩为五钩子（MINIMIZE/LOCATION/SHOW/DESTROY/FOREGROUND）+ 打开/关闭设置实值；
  打开批量沿用门禁（同进程 1s 内 >3 窗只播首个）；旧 AW_BLEND 实验路径在 ghost 版开启时自动让位。

**动画（v4.0-alpha.15，macOS genie）**
- 四条触发路径（Dock 点击 / 标题栏按钮 / Win+↓ / 任务栏）的最小化与还原动画；Dock 点击走"ShowWindow 前同步预抓"，
  其余三条靠前台帧缓存兜底（2Hz 采样、700ms 新鲜龄、1500ms 判废、1 全 4 半 LRU）；
  **alpha.15 新增：EVENT_SYSTEM_FOREGROUND 钩子——窗口成为前台时立即补抓一帧**（250ms 节流），
  "刚切到窗口就最小化"不再因缓存无帧而整段不播（真机矩阵里 caption 按钮两连未播的根因）。
- 全部时长/比例在 `config/anim-params.json`，**改文件即热加载**（首次运行自动生成默认表）；逐字段的"为什么"见 `Core/Animation/AnimParams.cs`。
- 门禁（Core AnimPolicy，纯函数+单测）：风暴抑制（200ms 内 4 个最小化整批不播）、并发幽灵 ≤3、同进程 300ms 节流、排除清单、全屏零轮询不播、速度倍率 0.5-2.0。
- 保真度两档（诚实声明）：**我方触发（Dock）= 完整编排档**（预捕获）；**系统触发 = 事后补偿档**（事件到手时窗口已 iconic，播缓存帧），允许 <100ms 过渡痕迹。MyDockFinder/Lume 靠注入拿完整编排权，本程序不注入，差距如实写。
- 每次播放/判废都有结构化日志（`动画触发：源= 层= 引擎= 时长= 帧数= P95=` / `动画未播：源= 层= 因=`），`scripts/diag.ps1` 的 [动画] 节汇总触发率与判废分布；取证脚本 `scripts/verify/anim-trigger-matrix.ps1`。
- 已知限制：24H2 不响应 SPI_SETANIMATION（系统"缩向任务栏"动画可能并存，有 WARN 行）；mesh 引擎 P95 80-146ms（native DComp 渲染器是根治路径，待工具链）；从后台进程触发的"冷启动第一下"不播（缓存无帧，真实交互不受影响）。

**菜单栏**
- 24px 高，logo 菜单真动作、前台应用菜单镜像、时钟/日历、音量/网络/电量、媒体迷你项。
- 控制中心：Big Sur 瓦片 + 系统状态行（电量/网络/蓝牙/输入法，5s 采样）；顶栏时钟左侧有输入法徽标（中/EN/あ）。
- 通知中心：右侧全高面板（大时钟 + 月历 + 通知卡片 + 清除），点顶栏时钟开合；新通知右上角弹横幅。
- 音量 HUD：按音量键弹横向胶囊（图标 + 16 根竖条），接管媒体键以取代系统浮层。

**启动台**
- 列/行随屏幕自适应（3–9 列，图标优先不缩），搜索、翻页、悬停放大、开合动画。
- 文件夹：拖应用建夹、拖入、覆盖层、右键移出，`config/launchpad.json` 持久化。

**访达**
- 列表 / 分栏 / 图标 / 画廊四视图，Quick Look（含 PDF 首页）、显示简介、每文件夹视图选项、标签页、搜索、文件操作（IFileOperation）。

**其它**：Alt+Tab 切换器、Spotlight 视觉、触发角、Dock/顶栏全屏避让。

## 本地运行

```powershell
# 1) 下载 Release 的 zip，解压后直接运行 Beautiful.exe（自包含，无需 .NET）
# 2) 或从源码构建
dotnet build -c Release
src\Beautiful.App\bin\Release\net8.0-windows10.0.19041.0\Beautiful.exe
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
| `--notif-probe` / `--progress-probe` | 通知与拷贝进度探针 |
| `--launchpad --launchpad-folder <名>` | 启动台与文件夹 |

## 构建与门禁

```powershell
dotnet build -c Release          # 0 警告 0 错误
dotnet test tests/Beautiful.Core.Tests
```

当前：**build 0 警告 / 0 错误；test 1419 总，Windows 上全通过**（Linux 上 46 个平台/区域差异用例红为已知基线）。
（注：在 Linux/macOS 上用 `-p:EnableWindowsTargeting=true` 交叉编译可以过；
CI 门禁 `ci.yml` 每次推送在 windows-latest 全套把关。）

## 结构与边界

- 分层：`Beautiful.Core`（纯函数与规则，全部可单测）/ `Beautiful.App`（WPF 外壳）/ `Beautiful.Native`（C++ 材质与捕获，可选）。
- 视觉：`Core/Design/MacTokens` 单一事实源（亮/暗两套），App 侧注入 `Mac*` 资源键。
- **托盘真实像素（截屏路线，实验）**：顶栏托盘面板顶部按 2s 截系统通知区并显示；截到纯色（任务栏被隐藏）时整块隐藏，不展示假图。以下为旧记录：——探针证明 UIA 读不到，改走截屏：`--tray-capture` 能按 `TrayNotifyWnd`
  矩形截到图，但在**默认的任务栏隐藏策略下系统托盘本身是空的**，截到的是空白条；要真正显示真实图标，
  需要用户保持系统任务栏可见（或改走 native 捕获 + 自绘）。左右停靠的放大/倒影与工作区预留、托盘真实像素的呈现同样未做。
- 已知差异：第三方拷贝进度只能轮询（原生任务栏是进程内直写，跨进程无接口）；天气依赖第三方免费接口。
- 交互类（悬停放大、拖拽建夹、点通知跳转）仍需人工点一次确认，代码与提交信息标 `UNVERIFIED(owner)`。

## 发布

推送 `v*` 标签触发 `.github/workflows/release.yml` 构建 zip 与安装包（CI 独占 Release 资产）。
`v4.0.0-alpha.2` 起 CI 未触发、构建机也没有 Inno Setup，故 alpha 系列只提供免安装 zip。

### 免 Windows 机器的交叉交付包（v4.0.0-alpha.14 起）

非 Windows 开发机可直接产出桌面 zip（self-contained，解压即用，无需装 .NET）：

```bash
bash scripts/package-release.sh [输出目录]   # 交叉编译 → PE 校验 → 组装 zip
```

产物：`Beautiful-<版本>-win64.zip`（Beautiful.exe + AnimLab.exe 调参台 + 全套运行时 + 交付说明）。
不含 C++ 原生层（Beautiful.Native.dll 需 MSVC，由 CI 构建线负责），缺失时主程序自动降级为纯 C# 形态。
验证口径：交叉编译 0 警告 0 错误 + PE 头校验（x64 GUI）+ 全量单测（Linux 基线红集合无回归）+ CI windows-latest 终审。
