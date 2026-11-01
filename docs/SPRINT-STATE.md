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
