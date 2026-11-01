# SPRINT-STATE（v3.0 冲刺检查点）

- 冲刺开始：2026-10-30 / 最后更新：2026-10-31 01:35
- 工作目录：`D:\Beautiful-main`（所有者指定；`D:\Beautiful` 为旧分叉，本冲刺已从那里迁回）
- 基线提交：`13fdb45`（4.0.0-alpha.11）→ 冲刺提交：`0d7eb1b` 起
- 规格版本：`docs/specs/v3.0-autonomous-sprint.md`（v2.0/v2.1/v2.2 全文未随仓库提供，以 §4 浓缩规格执行）

## alpha.24（2026-11-01）：mac Dock 结构落地 + 最小化第三方覆盖 + 顶栏图标

用户真机反馈（alpha.23 测试清单回填）驱动，三项全修：

1. **启动台滚轮翻页（A2 两轮未愈的真根因）**：事件路由（alpha.23 改 PreviewMouseWheel）一直是对的，
   错在处理器算总页数用 `Tiles.Items.Count + _page*PageSize`——只数到当前页末尾，第 0 页恒得 1 页直接早退。
   修复：`_allTiles` 全量表 + 滚轮/键盘同源 + 120ms 触控板惯性节流 + 文件夹覆盖层打开时滚轮放行。
2. **最小化第三方覆盖**：SW_HIDE 代管从"仅自家进程"扩展到全部未挂死可见窗口（`WindowTransitionGuard.Cloak`）；
   `WindowEligibility.HiddenByUsOverride` 豁免代管窗口被 2s 对账删除（否则用户失去还原入口）。
   原生淡出动画被直接掐掉，"替身特别透明"（双动画叠加）随之消失。
3. **mac Dock 结构（D6 首批）**：新增 `DockItemKind.MinimizedWindow`；分隔符规则改为 mac 真实语义——
   运行应用与固定应用同属应用区**不分界**，分隔符 = 应用区 | 最小化区 | 废纸篓；
   最小化窗口驻留分隔符右侧、点击还原/右键菜单同路、指示点不亮、不可拖拽。
4. **顶栏图标（用户截图对照）**：右区 Segoe 字形全部换 `Symbols.xaml` 单色描边矢量
   （音量/Wi-Fi/电池五档+充电闪电/放大镜/控制中心），新增通知铃铛入口，描边绑定前景色随主题。

门禁：build 0W0E；Core 测试 1349 过 / 48 失败（Linux `Path.GetFileName` 平台差异，Windows CI 全绿，零新增）；
分隔符单测按新语义重写 13/13 过。交付：zip + 测试清单 + tag `v4.0.0-alpha.24`。
遗留：Dock 最小化区显示应用图标（mac 是窗口缩略图，需帧缓存接条目，下轮）；还原方向个别机型可能一帧原生残影。

## 队列状态

| 任务 | 状态 | 证据 | 备注 |
|---|---|---|---|
| Q1 盘点+归档 | done | 本文件、docs/specs/ | 基线 build 0 警告 / 1345 测试通过 |
| Q2-1 挂载自检 | done | 日志 `动画子系统已挂载：MINIMIZE=0x… LOCATION=0x… 设置=开 引擎=… 参数[…]`；设置页降级声明=DisabledReason | 钩子句柄+设置实值+生效引擎全部可读 |
| Q2-2 系统动画接管 | done(降级) | `SystemAnimationSuppressor`；本机 24H2 不响应 SPI（tools/spi-probe 复现）→ 如实记录不接管 | OV-02 待所有者核验 |
| Q2-3 四触发路径时序 | done | 前台帧缓存 2Hz/700ms 龄/1全4半；Dock 预抓保持；`动画触发：源=MINIMIZESTART 层=缓存` 实测出现 | 冷启动无帧不播=诚实降级（日志可见） |
| Q2-4 几何对账 | done | `几何对账：… 结论=一致` 每次动画一行 | 三方矩形齐全 |
| Q2-5 判废可见化 | done | `动画未播：源=… 层=… 因=…` 全早退路径；diag.ps1 [动画] 节按"因"分组 | 触发率行已出（本机 19/28，含热身） |
| Q2-6 参数 token+热加载 | done | AnimParams + config/anim-params.json 自动生成 + FileSystemWatcher 热加载 | 日志三连：生成/加载/监听 |
| Q2-7 触发矩阵脚本 | done | scripts/verify/anim-trigger-matrix.ps1 + docs/evidence/anim/×5 轮 | 前台锁/Dock UIA 两格脚本不可驱动→OV-01 |
| Q3 AnimLab | partial | 无独立工具；AnimParams 文件即" poorest-man's AnimLab"（热加载可调） | 完整调参台列入下冲刺 |
| Q3 native 渲染器 | blocked | 本机无 MSVC/无 vswhere；src/Beautiful.Native 已有材质/捕获底座，无 BN_Anim_* 动画宿主 | UNVERIFIED-BUILD |
| Q4 还原/最大化/打开/关闭/批量 | partial | 还原/最大化已有（morph 改 easeInOutQuad）；批量策略 AnimPolicy 落 Core+9 测试（风暴/并发/节流/排除/打开批量/缓存龄）；打开 0.94 占位帧未做；关闭默认关未做 | 下冲刺队列首位 |
| Q5 主线续推 | partial | 压缩版审计 docs/audit-v4.0.md | Phase 1 删除项与 v4.x 产品方向冲突→不执行（报告 §5） |
| Q6 收尾交付 | done | 本文件、SPRINT-REPORT.md、README 动画节、版本 4.0.0-alpha.12 | |

## 门禁快照

- build: **0 警告 / 0 错误**（Debug + Release publish 均过）
- test: **1390 通过 / 0 失败**（新增 AnimParams/AnimCurve/AnimReport/AnimPolicy 共 45 测）
- native: 本机不可编译（无 MSVC）→ 按协议不降格、不伪造

## BLOCKED 清单

| 项 | 原因 | 回收 |
|---|---|---|
| native DComp 动画宿主（BN_Anim_*） | 无 MSVC 工具链 | 装机后按 v2.2 §3 实现；现有 Native 材质/捕获底座可复用 |
| AnimLab 独立调参台 | 时间/上下文优先给了触发链修复 | anim-params.json 热加载已是可用的最小调参路径 |

## UNVERIFIED 清单（→ docs/owner-verification/）

OV-01 四路径真机点按 / OV-02 SPI 接管在所有者系统上的可用性 / OV-03 帧性能（本机 P95 80-146ms 超 20ms 目标，疑似 mesh 瓶颈）/ OV-04 混合 DPI / OV-05 调参口味

## 下一个动作（中断续跑从这里开始）

1. OV 包回填回收（所有者 30 分钟）→ 按回填结果修触发链残余。
2. Q4 编排：打开占位帧（捕获重试 8ms×12）+ 关闭 `CloseAnim=true` 开关（默认关）。
3. 装 MSVC BuildTools → native 动画宿主 + WGC 捕获升级 → AnimLab。

---

## alpha.26（2026-11-01）DWM 实况缩略图引擎（CI 首次全绿 + 首个 CI 产出 Release）

26H2 真机日志三条铁证：PrintWindow 抓帧半瘫（纯色判废/抓帧不可用）、mesh 预渲染起播迟到
~1s（P95=78ms/帧）、DwmRegisterThumbnail hr=0 全通过。动画引擎改长在唯一被验证可靠的
DWM 缩略图通道上（详见 commit d33204b）：

1. **DwmLiveGhost**：12 切片实况 genie——rcSource 按源窗口切片，rcDestination 每帧按
   GenieMath 收拢；切片→单片→图像链三级退化，句柄全数配平。零抓帧、零预渲染，事件到手即起播。
2. **MinimizedTileLayer**：最小化条目实况缩略图（等比内切、三路跟随同步、输入全穿透）。
3. **MinimizeAnimator**：lastGoodRect 表根治"事件到手已 iconic、几何=无幽灵"；
   PlayMinimize/PlayRestore/PlayMinimizeFromCapture 首选 TryPlayLive。
4. **CI 里程碑**：release workflow（native /W4 /WX → NativeSmoke → build → test → publish →
   Inno 安装程序 → Release）在 tag v4.0.0-alpha.26 上首次完整跑绿（run 41317924864），
   Release 资产双件套（Setup.exe 52.8MB + win-x64.zip 75.9MB）由 CI 独占产出。
   所有者机器无 MSVC 的 native 编译缺口由 CI 补上——"本机不可编译"从此不再是阻塞项。

## alpha.27（2026-11-01）Q4 关闭动画落成（默认关）+ 自家窗口真动画

所有者裁定"先不测试，继续推进"→ 跳过 OV 回填，执行下一动作队列的 Q4 收尾项（SPRINT-STATE
下一动作 #2 后半）。规格 v3.0-autonomous-sprint.md §4-Q4 原文逐条落实：

1. **第三方关闭退场（默认关）**：`AppSettings.WindowCloseAnimation`（默认 false，macOS 忠实，
   README 写明"这不是没做完"）。开启后 EVENT_OBJECT_DESTROY 触发 `PlayClose`：前台缓存帧
   （龄 ≤ 新参数 CloseCacheMaxAgeMs=1000ms）+ 最后已知非图标矩形，150ms fade+scale→92%
   （easeInQuad，`CloseAnimMath` 纯函数 + 10 单测）。
   【物理上限，诚实声明】DESTROY 到手时 hwnd 已失效——实时抓帧不可能，画面只能来自缓存，
   所以只有"最近 1 秒内曾是前台"的窗口有退场；后台窗口关闭诚实跳过（日志一行，判废可见化）。
2. **窗口身份表**：DESTROY 后类名/进程号/矩形全查不到 → LOCATIONCHANGE/SHOW/启动播种三处
   顺手登记（类名+pid，512 上限整体重来）。菜单(#32768)/提示窗/影子窗/输入法/系统 shell 面
   按类名白名单静默跳过；设置未开时 PlayClose 零日志（DESTROY 对短命窗型极频繁，防刷屏）。
3. **自家窗口真动画（规格原文"一律真动画"）**：新增 `WindowCloseFx`——WPF DoubleAnimation
   对窗口本体做 fade+scale（不经过替身；自家窗口画面随时可得，不受设置控制）。设置/关于本机/
   访达三窗 OnClosing 接线；ConditionalWeakTable 防重入（窗口一生只演一次，无泄漏）；
   Application.Shutdown 期间不演（不给退出加 150ms 的税）；异常 fail-open 直通关闭 +
   120% 时长保险计时器双保险。
4. **设置页**：新增"窗口关闭动画（默认关：macOS 关窗没有动画）"复选框，紧邻新窗口淡入项。

门禁：Release 构建 0W0E；Core 测试 1361 过 / 46 失败（全部为 Linux 平台差异基线，零新增；
CloseAnimMath 10 用例全过）。AnimPolicy 无改动即支持 Close（AnimKind.Close 的 BaseDuration
与节流早已就位，v3.0 预留生效）。

**遗留/口径变更**：
- 下一动作 #2 的"打开占位帧（捕获重试 8ms×12）"**作废**：alpha.26 DWM 实况引擎起播零预渲染，
  打开路径的占位帧竞态已无意义；第三方开窗在 26H2 上受 PrintWindow 半瘫影响退 fail-open
  （窗口直接出现），根治需"开窗实况替身"，但 DWM 拒斗篷（alpha.25 已证）使新窗口无法先藏后演——
  记入 BLOCKED，除非找到不斗篷的藏窗法。
- OV-03（帧性能 P95）随 DWM 实况引擎上线自动重测：实况通道无 mesh 预渲染，P95 语义变化，
  待所有者真机日志回填。

---

## 队列状态（alpha.27 后刷新）

| 任务 | 状态 | 备注 |
|---|---|---|
| Q4 关闭动画 | done | 默认关 + 自家真动画，规格 §4-Q4 全项落实（WH_MOUSE_LL 预捕获属 v2 可选，未做） |
| Q4 打开占位帧 | 作废 | alpha.26 实况引擎取代；开窗实况替身 BLOCKED（DWM 拒斗篷） |
| native DComp 动画宿主 | BLOCKED→缓解 | CI 已能编译 native（/W4 /WX 全绿），无宿主代码；真机调试仍需所有者装 MSVC |
| AnimLab 调参台 | pending | anim-params.json 热加载仍是最小可用路径 |
| OV-01/02/05 | 等回填 | 所有者验收 alpha.27 时一并回填（测试清单已备） |


---

## alpha.25（2026-11-01）遮挡重播 + 图标实拍重画

真机反馈（alpha.24 清单留言 + Beautiful-202611.log）定位到四个架构级根因，全部换思路重做：

1. **最小化动画"两条打架"**：24H2 上 SPI/FORCEDISABLED/CLOAK 三条"关原生动画"的路全被系统拒绝
   （日志：SPI 不响应、0x80070006、0x80070005）。**遮挡重播**：替身第一帧 = 原矩形不透明全尺寸
   画面，Show 即盖住原生缩放动画；还原方向同理。Cloak("minimize")/SW_HIDE 代管全链退役
   （SW_HIDE 丢任务栏按钮 + 2s 对账竞态 = 上轮"最小化区没有窗口"的根因）。
2. **访达开窗动画双重死路**：OBJECT_SHOW 钩子带 SkipOwnProcess（自家事件到不了）+ DWM 拒斗篷。
   改自导链路：FinderWindow Opacity=0 预藏 → ContentRendered → PlayOpenForSelf（PrintWindow
   对全透明窗口照常出图）→ 替身 94% 落位 → reveal；1.5s 失败保险。最小化钩子去掉 SkipOwnProcess，
   自家窗口也播 genie。
3. **启动台翻页无动画**：Refresh 拆 ApplyPage + SlideToPage——RTB 快照（DPI 对齐）+ 双层
   TranslateTransform 280ms easeInOut；快照失败退化直切。
4. **图标不像 macOS**：DockBuiltinIcons 按 MyDockFinder 实拍复刻版逐像素测量重画——访达改
   "白底+蓝色半脸剪影+深色五官"（旧版误画全蓝底白脸）；启动台火箭旋转方向修正（WPF 正角顺时针，
   旧 -45° 指向左上）。渲染验证管线 scripts/draw.py（SVG 同几何预览 + 实拍并排对比）后定稿。

附带：最小化区条目图标 = 窗口帧居中裁方缩略图（LastFrameFor 注入 DockViewModel）；
ScreenBlurBackdrop.Aim 越界改钳制（窄副屏上控制中心不再退化实心卡底）；macOS 主题新增
ThemeDockGlassBackground（Dock 玻璃渐变，独立键不影响顶栏/原生预览取色）。

验证：Release 构建 0W0E；PE machine=0x8664；单测 1351 过 / 46 失败（全部为 Linux 平台差异基线，
零新增）。发布包 Beautiful-4.0.0-alpha.25-win64.zip（exe + 测试清单，已去 pdb）。
