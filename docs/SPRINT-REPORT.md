# v3.0 自主冲刺 · 终版交付报告

- 冲刺窗口：2026-10-30 23:27 → 2026-10-31 02:00
- 工作目录：`D:\Beautiful-main`（4.0.0-alpha.11 → **4.0.0-alpha.12**）
- 协议：docs/specs/v3.0-autonomous-sprint.md（不停顿协议全程遵守，零等待、零暂停）
- 门禁终态：build 0 警告 0 错误 · test **1390/0** · native 不可编译（如实声明）

## 1. 完成清单

| 任务 | 状态 | 一句话结果 | 证据 |
|---|---|---|---|
| Q1 状态盘点 | done | 基线实存摸清：Stage A 数学齐、Stage B-G 全缺，Phase 1 删除项与产品方向冲突 | SPRINT-STATE.md / audit-v4.0.md |
| Q2-1 挂载自检 | done | 启动期一行日志给出钩子句柄、设置实值、生效引擎、参数摘要；降级原因设置页可读 | 日志 `动画子系统已挂载：…` |
| Q2-2 系统动画接管 | done | SPI_SETANIMATION 会话内接管+退出还原；**本机 24H2 不响应 → 盲试+复核后如实放弃** | tools/spi-probe + WARN 行 |
| Q2-3 四触发路径 | done | 前台帧缓存（2Hz/700ms 龄/1500ms 判废/1全4半）兜底三条无钩子路径；实测 `源=MINIMIZESTART 层=缓存` 播放 | docs/evidence/anim/ |
| Q2-4 几何对账 | done | GetWindowRect × DWM 可见矩形 × Dock 图标三方对账行，DPI 错位/越界/图标缺失可从日志读出 | `几何对账：…` |
| Q2-5 判废可见化 | done | 全部早退路径产出 `动画未播：源= 层= 因=`；diag.ps1 新增 [动画] 节（按因分组+触发率） | diag 输出 |
| Q2-6 参数 token+热加载 | done | AnimParams 全量落 Core（可单测）+ config/anim-params.json 自动生成/热加载（150ms 防抖） | 日志三连行 |
| Q2-7 触发矩阵脚本 | done | scripts/verify/anim-trigger-matrix.ps1：自动起停、四路径驱动、证据落盘；六轮取证迭代 | docs/evidence/anim/*-matrix |
| Q3 AnimLab | partial | 完整调参台未做；等价最小路径=直接改 anim-params.json（热加载） | OV-05 |
| Q3 native 渲染器 | blocked | 无 MSVC 工具链；Native 底座（材质/捕获/WGC）已有，动画宿主未写 | UNVERIFIED-BUILD |
| Q4 编排 | partial | 还原/最大化在播（morph 换 easeInOutQuad）；批量门禁 AnimPolicy 落 Core+9 测（风暴/并发≤3/同进程 300ms/排除/打开批量/缓存龄三档） | AnimPolicyTests |
| Q5 主线续推 | partial | 压缩版审计完成；Phase 1 删除项裁定保留（冲突声明见 §5） | audit-v4.0.md |
| Q6 收尾交付 | done | 版本 bump、README 动画节、本报告、OV 包 | — |

提交链：`0d7eb1b`（Core+App 动画门禁/参数/日志/缓存/接管）→ `本报告提交`（脚本+证据+文档+版本）。

## 2. 动画专项成绩单

**触发矩阵（脚本自动取证，六轮迭代后的终态）**

| 触发路径 | 脚本可驱动 | 结果 |
|---|---|---|
| caption 按钮（WM_SYSCOMMAND） | 是 | 暖机后**已触发**（源=MINIMIZESTART 层=缓存）；冷启动无帧时判废有日志 |
| 任务栏（SW_MINIMIZE） | 是 | **已触发**（层=缓存，500ms，帧数=10） |
| Win+Down 热键 | 否（前台锁） | 脚本不可驱动 → OV-01 人工格 |
| Dock 点击 | 否（UIA 树无条目名） | 脚本不可驱动 → OV-01 人工格 |

对照冲刺前基线："事件路径错过/一次没播过"（MinimizeAnimator 旧注释原话）→
现在**每一条被拒的路径都有 `动画未播：因=…`，每一条成功的路径都有 `动画触发：… 帧数=… P95=…`**。

**帧性能（本机读数，诚实记录）**

- genie 500ms/帧数 10 /P95 106ms；还原 450ms/帧数 16 /P95 80ms。
- **未达 P95<20ms 目标**：mesh 幽灵（WPF 3D+分层窗口）是瓶颈，根治路径=native DComp 渲染器（本机无工具链，未做）。
- 全部读数见 OV-03 表格。

**判废分布（diag [动画] 节）**：本机全部 `因=到手时已最小化且缓存帧过期/缺失`（冷启动/无前台交互），无一处静默吞掉。

## 3. UNVERIFIED 总表

| 编号 | 内容 | 交付物 |
|---|---|---|
| OV-01 | 四路径真机点按矩阵 | docs/owner-verification/OV-01-四路径触发矩阵.md |
| OV-02 | SPI 接管在所有者系统可用性 | OV-02-系统动画接管.md |
| OV-03 | 帧性能真机读数 | OV-03-帧性能.md |
| OV-04 | 混合 DPI/多屏落点 | OV-04-混合DPI与多屏.md |
| OV-05 | 调参口味 | OV-05-调参口味.md |

全部 30 分钟内可跑完（设计目标）。跑完把文件内 UNVERIFIED 改 VERIFIED-日期。

## 4. 调参指南

`dist\Beautiful\config\anim-params.json`（首次运行自动生成）改文件即生效，不用重启。
速查表与口味建议见 OV-05；每个字段的"为什么"在 AnimParams.cs 头注释里。

## 5. BLOCKED 回收结果与方向冲突声明

1. **native 渲染器**：无 MSVC → 不写假代码、不标完成。装机后从 `src/Beautiful.Native`
   既有底座续做（v2.2 §3 的 BN_Anim_* 三件套）。
2. **v2.0 Phase 1（删歌词/三主题/玻璃壳/壁纸取色）与仓库 v4.x 产品方向直接冲突**：
   四个模块都是用户在用的功能（歌词链路 3 个修复提交、主题 4 条线、玻璃壳带全屏避让）。
   按协议两原则裁定**保留**，在此声明，不偷偷执行也不静默跳过。
3. AnimLab 独立台：anim-params.json 热加载已覆盖"改参数立即看"的核心需求，完整台进下冲刺。

## 6. 下一步建议队列

1. OV 包回填 → 修触发链残余（预计集中在 Dock 点击格）。
2. Q4 编排收尾：打开占位帧（8ms×12 重试）、`CloseAnim` 开关（默认关，macOS 忠实）、
   还原 hold+看门狗（防窗口被留在屏幕外）。
3. 装 MSVC BuildTools → native 动画宿主 + WGC 捕获升级 → P95<20ms 主目标。
4. AnimLab（窗口列表+逐窗预览按钮+帧时间图+一键诊断包）。

## 7. 版本与文档

- 版本：4.0.0-alpha.11 → **4.0.0-alpha.12**（Directory.Build.props）
- README：新增「动画」节（四路径/缓存兜底/保真度两档声明/已知限制）
- 已知限制（诚实清单）：
  1. 24H2 不响应 SPI_SETANIMATION → 系统动画与 genie 并存（有 WARN 行）。
  2. mesh 引擎 P95 80-146ms（目标 20ms）。
  3. 无头/后台进程触发最小化时，冷启动第一下不播（缓存无帧，有日志）；
     真实交互（先点窗口再最小化）不受影响。
  4. 风暴抑制从第 4 个窗口起生效，前 3 个已播的收不回（不做 200ms 防抖延迟，避免拖慢每一次最小化）。

---

# v4.0.0-alpha.13 · agent 分支冲刺报告（P0：打开/关闭动画全链路）

- 分支：`agent/alpha.13-p0`（所有者授权 agent 自主推进；CI 门禁 `ci.yml` 每次推送在 windows-latest 全套把关）
- 协议：沿用 docs/specs/v3.0-autonomous-sprint.md 的不停顿协议与门禁口径
- 开发环境：非 Windows 机器（Linux 交叉编译哨兵 `-p:EnableWindowsTargeting=true` 与 CI 同构）；
  **运行期目视验证不在本机做**，交 owner-verification 包（诚实声明，不伪造证据）
- 门禁：build 0 警告 0 错误（本地交叉编译 + CI 双口径）；test 1412 总数，新增 22 测全绿；
  Linux 本地跑有 48 个平台/区域差异用例红（时钟格式、盘符路径、UNC），Windows CI 为准——
  失败集合与 alpha.12 基线逐条一致，无回归

## A1. 完成清单（v3.0 报告 §6 建议队列的回收）

| 任务 | 状态 | 一句话结果 | 证据 |
|---|---|---|---|
| Q4.3 打开动画（队列首位） | done | SHOW 触发 + 占位帧（8ms×12 捕获重试就绪当帧换入）+ 200ms easeOutQuint 落位；fromIcon 实验形态默认关 | OpenAnimMath + 10 测；MinimizeAnimator.PlayOpen |
| Q4.4 关闭动画 | done | DESTROY 触发（默认不播=macOS 忠实）+ 前台缓存帧 + 龄超 CloseCacheMaxAgeMs=1000 判废 | CloseAnimMath + 12 测；PlayClose |
| 自家窗口真动画 | done | 设置/关于本机关闭走 OwnWindowAnimator（Content 收拢 fade+scale→92%）后真 Close | OwnWindowAnimator.cs + 两窗口 OnClosing |
| Dock 启动弹跳联动 | done | 反复弹跳至首窗 SHOW 收口（exe 基名=进程名匹配）或 10s 超时；点击/右键菜单两条启动路径同语义 | DockSurface.StartSustainedBounce / DockWindow.NotifyFirstWindowShown |
| 设置面 | done | OpenAnimation（默认开）/ CloseAnimation（默认关）/ OpenAnimStyle 三项 + 设置页控件，SHOW/DESTROY 路径真即时 | AppSettings + SettingsWindow |
| 旧打开动画让位 | done | ghost 版开启时 AW_BLEND 实验路径不再叠加（两条打开动画不打架） | WindowTracker.AddWindow |
| CI 门禁 | done | ci.yml：windows-latest 全套（native /W4 /WX + NativeSmoke + build + test）+ ubuntu 交叉编译哨兵 | .github/workflows/ci.yml |
| 首轮 CI 修复 | done | ① global.json 钉住 .NET 8（runner 预装 10 SDK，新编译器重载决议不同致 CS0023）；② build-native.cmd 改 vswhere 探测（runner 是 Enterprise，旧脚本只认 BuildTools 硬编码路径） | global.json / scripts/build-native.cmd |
| AnimLab 完整台 | todo | 本冲刺未动（预算给了打开/关闭全链路）；anim-params.json 热加载仍是最小可用路径 | — |

## A2. 打开/关闭专项设计要点

- **占位帧与动画起点是同一帧**：OpenAnimMath.Start(t=0) = 全透明起始帧，占位阶段与起播天然衔接，无第二套数学。
- **首帧竞态**：SHOW 到手时窗口可见但首帧未画完（白板裸窗窗口期）；PrintWindow 在线程池重试，
  就绪前 ghost 全透明不遮挡；捕获彻底失败 → 撤占位 + `动画未播：源=OBJECTSHOW 层=重试 因=占位帧捕获重试耗尽`。
- **关闭的画面来源**：DESTROY 到手时句柄已销毁，唯一画面是 2Hz 前台缓存；hwnd→pid 记账在 SHOW 期完成，
  无记账的窗口（tooltip/临时窗）一律不演。
- **挂载自检行**扩为四钩子（MINIMIZE/LOCATION/SHOW/DESTROY）+ 打开/关闭设置实值。
- **退出链路**：Dispose 卸四钩 + 清 pid/尝试/首窗记账表（红线：新增全局状态全部进清理链）。

## A3. UNVERIFIED（owner-verification 包）

| 编号 | 内容 | 说明 |
|---|---|---|
| OV-06 | 打开动画真机观感（记事本/Chrome/UWP/资源管理器首窗） | 预期：内容浮现 0.94→1.0 落位；日志 `打开动画：占位帧就绪…` / `动画触发：源=OBJECTSHOW…` |
| OV-07 | 关闭动画真机观感（先开 OpenAnimation→CloseAnimation 再关窗） | 预期：缓存帧收拢消失；无缓存时 `动画未播：因=关闭动画无可用缓存帧` |
| OV-08 | 启动弹跳至首窗收口（从 Dock 点一个未运行的应用） | 预期：图标反复弹跳、首窗出现当次弹完落地；日志 `启动弹跳收口：…` |
| OV-09 | 自家窗口收拢退场（设置/关于本机） | 预期：内容收拢淡出后窗口消失 |

## A4. 已知限制（诚实清单）

1. 打开动画的 ghost 与真窗口短暂并存：淡入完成（前 45%）时真窗口可能已画出全尺寸内容，
   0.94 起步的替身在其上有 <100ms 的轻微重影——easeOutQuint 前段极快，实际观感待 OV-06 核。
2. fromIcon 形态无捕获重试（实验项，缺图标矩形或捕获失败即退化/不播，有日志）。
3. 关闭动画依赖 2Hz 前台缓存：窗口从未进过前台（后台直接销毁）时不播，有日志。
4. AnimLab 完整调参台未做（下冲刺首位候选）。
