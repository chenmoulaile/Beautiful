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
