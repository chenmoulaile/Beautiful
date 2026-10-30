# Beautiful

Windows 11 的 macOS 桌面还原壳：悬浮 Dock（Big Sur 风）+ macOS 菜单栏 + Alt+Tab 应用切换器 + 悬停窗口预览 + Spotlight 启动器 + 控制中心 + 访达。WPF + .NET 8，自包含发布，中文界面。

> 版本 `4.0.0-alpha.11`。界面截图不入库（`logs/`、`docs/` 已 gitignore），观感以发布包实跑为准。
> 完成度一律以**实测**为准：没跑过的写"未验证"，不写"已支持"。

## 现在能用的

**Dock**
- 固定应用 / 运行窗口 / 废纸篓 / 媒体胶囊，连续高斯放大、悬停 DWM 实时缩略图、genie·zoom·suck 最小化动画。
- 角标：微信/QQ/TIM 窗口标题里的未读数 → 图标右上角红色胶囊（非白名单不画）。
- 进度条：Explorer 拷贝对话框的 UIA 读数 → 图标下沿细条，300ms 采样 + 逐帧插值。
- 行尾小组件：时钟 + 天气（wttr.in，15 分钟缓存，离线显示 `--°`）。
- 右键菜单：固定/取消固定、打开文件位置、属性、更换图标（自定义 PNG/ICO，只存本地路径）。
- 隐藏方式三档：常显 / 自动隐藏（离开 0.5s 滑下只露 4px，触底滑回）/ 半透明（离开时 45%，移上去恢复）。
- **实验**：停靠边可切左/右（竖排面板 + 悬停放大 1.12，无高斯放大场/倒影，且不占工作区）；新窗口淡入（`AnimateWindow`，跨进程不保证生效）。

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

当前：**build 0 警告 / 0 错误；test 1309 通过 / 0 失败**。

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
