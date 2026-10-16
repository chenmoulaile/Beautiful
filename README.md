# Beautiful

Windows 11 桌面美化套件：悬浮 Dock（macOS 风格）+ macOS 顶栏 + Alt+Tab 替换切换器 + 悬停实时窗口预览 + Spotlight 启动器 + 控制中心。WPF + .NET 8，自包含发布，中文界面。**只想跑起来 → 看「本地运行」**；想知道 v3 改了什么 → 看「v3 变更（对着反馈的七条）」。

> 界面截图不随仓库分发（`docs/` 为内部文档目录，已 .gitignore 排除）；观感以功能描述与发布包实运行为准。

Beautiful 接管日常桌面三件套：屏幕底部是悬浮 Dock（应用固定与启动、连续高斯放大、媒体胶囊与双语歌词、悬停 DWM 实时缩略图预览），屏幕顶部是 macOS 风格菜单栏（logo 菜单、前台应用名、真实"窗口"菜单、时钟/日历、音量/网络/电量、媒体迷你项），Alt+Tab 被替换为 macOS Cmd+Tab 风格的应用切换器。原生任务栏可选择完全隐藏、自动隐藏或保留。

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
- **控制中心**（v3）：右区最末图标唤出的快速设置面板（同一套分层弹层 + 抓屏模糊背板）。内含主题三选一、系统任务栏策略三选一、**放大 / 倒影 / 媒体胶囊 / 壁纸取色四个开关砖块**、**真实系统音量滑杆 + 静音**（Core Audio `IAudioEndpointVolume`，手写 interop、无第三方包），以及"打开设置…/重载 Dock"两个动作。每一项都走与设置页/托盘菜单同一条落地路径；打开时读真值（主题、策略、开关、端点实际音量），再点一次/Esc/点面板外均收起。音频端点不可用时音量行**置灰并写明原因**（可用 `BEAUTIFUL_NOAUDIO=1` 自检该形态），不伪造读数
- 顶栏自绘半透明底（tint + 顶部内高光 + 下缘亮边），**不做实时抓屏背板**（常驻表面重抓会把自己的像素抓进背板 = 鬼影，见「菜单与背板」）

### Alt+Tab 替换切换器（v2）

- `WH_KEYBOARD_LL` 低阶键盘钩子拦截 Alt+Tab（热键解析层支持 `alt+tab`/`ctrl+alt+tab` 等组合，**当前设置界面固定为 Alt+Tab、不提供修改**）；同应用多窗口按 exe 归并并可**展开**逐个选择（Core 纯函数 `WindowListMerger`）
- **三样式**：macOS（居中大圆角毛玻璃 + 字幕，"玻璃"同样是抓屏自绘）/ Linux（GNOME 实底覆盖条）/ 鸿蒙（胶囊 + 强调色）
- 按住触发键循环、松开提交、**Esc 取消**（不改变前台）；看门狗兜底，绝不留下"按住 Alt 面板不消失"的卡死态
- **全屏直通**：前台为全屏应用（游戏/独占全屏）时自动放行原生 Alt+Tab，可在设置关闭
- **应急停用 Win+Alt+K**：任何时刻按下即在进程内卸载接管、恢复原生 Alt+Tab（只认真实体键；不改设置值，重开在设置中操作）

### 菜单与背板（v3 重写）

- **自绘 FluentMenu 统一所有菜单**：托盘右键菜单、顶栏全部菜单/浮层、Dock 条目右键——同一套 macOS 质感弹层（激活式弹出 + 键盘可达 + 锚点屏 DPI 换算 + 抓屏模糊背板）
- **弹层的"玻璃"是我们自己抓屏算出来的高斯模糊，不是系统 Mica/Acrylic**：显示前把面板下方的屏幕内容取回 → 1/4 降采样 → 三次盒式模糊 → 用 `Viewbox` 瞄准"面板在捕获区里的那一块"交回画刷（面板长大时只是露出旁边已抓好的内容，不重抓屏也不重缩放）。抓屏失败 / 取不到显示器 / 全零帧一律返回 null → 内容自铺**实心**卡底，不变黑也不开洞；`BEAUTIFUL_NOBLUR=1` 可整体自检降级形态。日志可查：`背板=抓屏模糊 面板=… 区=… 图=138×153 半径=8×4`、`背板=实心降级（抓屏未产出）`
- **常驻表面（顶栏、Dock）不做实时抓屏，也不向系统要任何材质**：它们本来就画在屏幕上，实时重抓会把**自己的像素**抓进背板（鬼影），而为了抓屏先隐藏一帧就是可见的闪烁。顶栏是三层自绘底（tint + 顶部内高光 + 下缘亮边），Dock 是 `ThemeDockBackground` + 1px 亮边，**这就是唯一可见底**；条带里面板之外的像素恒为真透明（分层窗口未绘制区，既点得穿也看得见桌面）。抓屏路径只给"显示一次"的瞬态表面（切换器 / 菜单弹层 / 媒体面板）
- **v2 的"系统背板协商"与 v3.0.0 的"旧亚克力增强"都已从渲染路径删除**：`DWMWA_SYSTEMBACKDROP_TYPE(38)`（Mica/Acrylic）与未公开的 `DWMWA_MICA_EFFECT(1029)` 不再调用——`hr==0` 只证明属性被接受，不证明屏幕上真糊了，协商结果在真机上会骗人。v3.1 把 `SetWindowCompositionAttribute(ACCENT_ENABLE_ACRYLICBLURBEHIND)` 也一并删掉：Win11 24H2/25H2 上它已经不模糊，而是把 accent 的 `GradientColor`（我们传的主题 tint）**平铺满整个窗口矩形**，Dock 的窗口矩形是全显示器宽 ×（面板 + 放大余量），于是用户真机看到屏幕底边一条全宽深色带并遮挡后面窗口的下一行。现在代码里只剩一种 accent 状态可写（`Disabled`），用途是把老版本留在窗口上的状态清掉一次（`AcrylicWindow.ClearLegacyAccent`）。保留的系统侧调用只剩悬停预览那个**纯 Win32 宿主**窗口的两项：`DWMWA_WINDOW_CORNER_PREFERENCE(33)` 圆角，与 `DwmExtendFrameIntoClientArea(-1)` 玻璃边距——缩略图由 DWM 合成，只在玻璃像素上显形，缺了它就是"HRESULT 全 0 但屏上一片黑"（该项豁免只给这个窗口，任何 WPF 表面依旧禁止）

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

### 启动器（Spotlight 式）

- 入口两个：顶栏右区搜索按钮、`Ctrl+Alt+Space` 全局热键（设置项 `LauncherEnabled` 可关，关掉后热键与按钮一起收）；Esc / 点别处即收。控制中心里**没有**启动器开关（那四项砖块是放大/倒影/媒体胶囊/壁纸取色）
- 热键与接管 Alt+Tab **共用同一条 `WH_KEYBOARD_LL`**：钩子装/摘的唯一收口是 `SwitcherService.SetHookEnabled`，"接管关、启动器开"时钩子仍为启动器保活（`Enabled=false`，只投递不吞键）。配置侧硬约束：至少一个修饰键 + 可识别主键，且**单修饰键 + 空格**（`ctrl+space` / `win+space` / `alt+space`）一律拒绝——启动器要配对吞掉按下与抬起，吞这三个等于在所有应用里废掉输入法切换 / 键盘布局切换 / 窗口系统菜单；带这类遗留值升上来的配置会被自愈成默认的 `ctrl+alt+space`
- 诚实边界：`LauncherEnabled` / `LauncherHotkey` 目前**没有设置界面开关**，只能改 `config\settings.json`（设置页里那一项还没接线）
- 索引来源两条：**开始菜单 Programs 目录**（用户区 + 机器区，解析 .lnk 目标与自定义图标）与 **MSIX/UWP 包**（Win11 的记事本、计算器、画图、Store 等已不再有 .lnk，缺这一路等于"搜不到记事本"）；MSIX 那一路整体 try/catch，失败只写成一行日志并少一批条目，桌面条目照常可用，且全程在后台线程（UI 线程零 COM/WinRT）
- 检索字段两个：**显示名**与**启动目标名**（`charmap.exe` → `charmap`、`Everything.lnk` → `everything`、AppsFolder 的 AUMID → 包名词）。用户敲的是 exe 名而不是显示名，只按显示名打分就会出现"列表里明明有、搜索说没有"；目标档整体压在同名次档之下一档，唯一例外是"目标完全相等"高于"名称前缀"
- 中文支持：手写拼音首字母表（`jsq` → 计算器），零第三方依赖
- 启动：解析出的目标以 `UseShellExecute=true` 交给 shell；MSIX 应用走 `explorer.exe shell:AppsFolder\<AUMID>`，绝不 `cmd /c`
- 已知限制：MSIX 条目在当前 Windows SDK 投影（19041）里取不到 logo 路径，图标留空框而不是错画成资源管理器图标

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
- **托盘菜单**：设置 / 主题（鸿蒙、macOS、Linux 子菜单）/ 重载 Dock / 退出；双击托盘图标直接打开设置。
- **歌词说明**：媒体胶囊依赖播放器注册 SMTC 会话。双语歌词优先取网易云翻译（tlyric），取不到时回退 LRCLIB（lrclib.net），两者皆无时显示"暂无歌词"；歌词结果按曲目缓存于 `cache/`。歌词偏移可在设置中微调。
- **日志排查**：运行日志位于 `<运行入口>\logs\Beautiful-yyyyMM.log`（自包含产物就是 `dist\Beautiful\logs\`），包含启动流程、条带几何与落位回读、会话发现、背板降级路径与异常记录。不用手抄：`powershell -NoProfile -ExecutionPolicy Bypass -File scripts\diag.ps1` 自动挑最新日志，连同每块屏的 `rcMonitor`/有效 DPI 与当前设置打成一整段（可 `-Path` 指定日志）。

## 已知限制

1. **QQ音乐 Win32 客户端与 PotPlayer 不注册 SMTC**，因此不会出现媒体胶囊，顶栏也没有媒体迷你项（QQ 音乐 Win32、PotPlayer 均未接入系统媒体控制）。
2. **网易云音乐 Win32 客户端需 SMTC 桥接插件**（如 [inflink-rs](https://github.com/apoint123/inflink-rs)）才会注册会话；注册后即可即插即用。
3. **网易云接口为非官方接口**，仅供个人使用，不保证可用性；请勿用于商业用途。
4. **真机复验项（v3.1 更新）**：本项目的开发与验证在**既不合成 DWM 背板、也不合成 DWM 缩略图**的虚拟机上进行，因此以下三项**在开发机上无法目视证明**，只能给出代码路径/日志级证据：① **Dock 底边全深色带**（缺陷 1）的消失——VM 里 `ACCENT_ENABLE_ACRYLICBLURBEHIND` 根本不产出那层填充，所以"黑带没了"只能在用户真机上看；本地能证明的是**源码里再无可写的非 Disabled accent 状态**，且运行日志每个常驻表面各出现一条 `旧亚克力=清除(state=Disabled(0))`。② **DWM 实时缩略图悬停预览**里实际显示的画面（缺陷 2）——本地能证明的是宿主窗口的 `DwmExtendFrameIntoClientArea(-1)`、`DwmRegisterThumbnail`、`DwmUpdateThumbnailProperties` 三条 HRESULT 与"注册数==注销数"；玻璃请求被拒（hr≠0）的环境现在**直接不弹卡片**，日志有一条只播一次的成因。诚实边界：本 VM 实测这一步 `hr=0x00000000`（日志 `预览宿主玻璃层 … → 缩略图可显形`），守卫并不触发，所以"缩略图真的画在屏上"这一条依旧只能真机目视——缺陷 2 里用户看到的实心黑框，本次是靠"玻璃边距恢复 + 类刷从黑色换成主题卡底色"两点一起处理的，最坏情况是一张与卡片同色的空卡而不是黑洞。③ **混合 DPI / 多屏 / AppBar 条带与系统真实保留区的错位**（v3 已有单测与 `scripts\diag.ps1` 对账，见第 7 条）。
   系统 Mica/Acrylic 属性路径（`DWMWA_SYSTEMBACKDROP_TYPE` / `DWMWA_MICA_EFFECT`）与旧 `SetWindowCompositionAttribute` 增强在 v3.1 都已退出渲染路径，不是任何表面的可见性前提；弹层的玻璃是自己抓屏算的高斯模糊，这一项在开发机上已目视验证（日志 `背板=抓屏模糊`）。
5. **隐藏任务栏后原生托盘不可见**：策略为"隐藏"时托盘图标随任务栏消失，退出需经**顶栏 logo 菜单"退出"**或设置窗口（启动时可用 `--settings` 参数直接打开）——顶栏即是逃生入口。
6. **强杀进程的 autohide 残留**：本进程被强杀时任务栏窗口由 explorer 自行摆回屏幕，但系统 autohide 位可能留在"开"——表现为任务栏缩到屏幕外、**把指针顶到屏幕最底行即可唤出**（不是卡死，无需重启 `explorer.exe`）；再次启动 Beautiful 并正常退出/切"保留"即清掉（只清这一位）。若用户自身的原生 autohide 偏好恰为"开"，退出 Beautiful 会一并清掉，可在系统设置重开。
7. **多屏与混合 DPI：代码已按屏取数，实机未复验**：v3 已把条带几何与副屏任务栏的判定全部改成按窗口所在显示器取（`MonitorFromWindow`/`MonitorFromPoint` + `GetMonitorInfo` + `GetDpiForMonitor`，见「不同分辨率、DPI 与多屏」），v2 那条"Dock 与顶栏按构造仅覆盖主屏 / `HideAll` 把副屏任务栏挪到主屏"的限制在代码层面已消除；但开发机是**单显示器 1024×768 @100%**，混合 DPI、非零原点副屏、拔屏等路径**只有单元测试与代码路径级验证**。真机上先跑 `scripts\diag.ps1`：`[显示器与 DPI]` 一节列每块屏的 `rcMonitor`/`rcWork`/有效 DPI/主屏标志，`[顶栏几何]`、`[Dock 几何]`、`[AppBar]` 三节给落位与协商数字，两边一对就能定位是哪一块屏、哪一步算错。
8. **Alt+Tab 接管的系统边界**：低级键盘钩子在 **UAC 安全桌面**（提权确认弹窗）收不到按键——此时切换器不出现、原生行为不受影响；提权窗口场景存在同类系统限制。游戏/独占全屏已做**全屏直通**（前台全屏时放行原生 Alt+Tab，可关）。如接管出现任何异常，**Win+Alt+K** 应急停用。
9. **只有"单显示器 @100%"这一条路径做过端到端目视**：不同 DPI 缩放（125%/150%）、原点非零的副屏、把 Dock 与顶栏分到两块屏（`DockMonitor`/`TopBarMonitor`）这些组合在这台开发机上根本产生不出来，属**单元测试 only**；悬停放大动画与部分主题视觉在受限环境下未逐一实机验证，效果可能因机器而异。放大场/弹跳/倒影/预览/条带几何算术均有代码级与单测级验证。
10. **流畅度验收口径如实声明**：本项目对放大动画的验收门槛是"帧间隔 max < 32ms"。开发虚拟机实测 P95 帧间隔 **36.1ms**（丝滑重写后由 65.7ms 改进而来），**该门槛在本 VM 上未达标**，归因于 VM 分层窗口（WS_EX_LAYERED）合成路径的呈现下限；此数据**未在真实 DWM 合成的实体机上复验**，实机帧时间通常应优于 VM，但不保证。

11. **MSIX/UWP 启动器条目没有图标**：当前 Windows SDK 投影（19041）不暴露 logo 路径，取不到就是空框——不伪造、也不错画成资源管理器图标（条目本身可搜可启动）。
12. **Dock 拖拽交互由进程内 `FileDrop` 代理验证**：拖入固定 / 块内排序 / 甩出取消固定的处理路径由自己构造的 `DragEventArgs` 喂进同一条代码路径并逐项留日志，**没有用真实 Explorer 拖拽**复验。真机上若"拖进去没反应"，差异大概率出在 shell 侧的数据格式或 drop 效果回调，请把 `logs` 里的 `拖入固定` / `Dock 排序` 行发回来。
13. **壁纸取色在这台 VM 上只验到"读得到壁纸"这一半**：`UseWallpaperAccent=true` 实测日志为 `壁纸取色：c:\windows\web\wallpaper\windows\img0.jpg → #9BBBD4`（取到有效强调色、非黑非白、无异常）。"读不到壁纸路径（纯色桌面 / 壁纸幻灯片）→ 沿用主题强调色"与"灰阶图 → 判不可取"两条降级分支由代码路径与单元测试（`DominantColorTests`）保证，**未在这台 VM 上实机触发**（它本来就有可读壁纸）。
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
   - **玻璃壳**：几何与"该不该画"有单测；装饰效果、以及与第三方任务栏工具共存时的表现**未验证**。

### 未实现（有意延后）

以下能力 v3 **仍未实现**（避免"看起来像清单遗漏"的误解，明确列出）：

- Dock 停靠边可配置：`AppSettings.DockPosition` 只是模型字段，设置页与运行时都未接线（当前 Dock 固定底边、顶栏固定顶边）
- Dock 自动隐藏：`AppSettings.AutoHideDock` 同上，只有模型字段，未接线
- 中键关闭窗口
- 切换器按窗口显示实时缩略图列表（当前为按应用归并的图标列表）
- **托盘图标常驻排在顶栏上**（v3.2 只做"点开才读"的面板）：经典读取路是从屏幕截图上抠图标，常驻要么持续跨进程读 explorer，要么把 Windows 自己的隐藏区展开来抠——后者会在顶栏留下一次可见的展开动作，与"常驻表面不做实时抓屏"的裁定冲突
- **玻璃壳的运行指示条**（v3.2）：要持续跟踪原生任务栏按钮的几何，Win11 24H2 只在 UIA 里暴露这些矩形，且随角标/语言/图标增减实时变动。做不到稳定就不做，不画一条会画歪的线
- **逐应用混音器**（v3.2 音量面板只做主音量与静音）：`IAudioSessionManager2` 那一套是另一个量级的会话枚举，本版没做，面板里给的是"系统声音设置…"这条明确出路
- 控制中心的**屏幕亮度**：需要 `WmiMonitorBrightness` 系 WMI 类（.NET 侧要额外引 `System.Management` 包，本仓库 src 侧目前零第三方包），且无背光硬件的机器（虚拟机/台式机）上只能永远停在"置灰"形态——按"不放死滑杆"的裁定整项不做，音量与网络/电量维持原样

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
