# SPRINT-STATE（v3.0 冲刺检查点 · v4.0.0-alpha.13）

- 冲刺开始：2026-10-31 / 最后更新：2026-10-31（agent 分支冲刺）
- 分支：`agent/alpha.13-p0`（所有者授权 agent 自主推进；main 停在 `2b93a28` = alpha.12）
- 开发环境：**非 Windows 机器**（Linux 交叉编译 `-p:EnableWindowsTargeting=true`；运行期目视验证交 owner-verification 包，不伪造证据）
- 门禁：本地 build 0 警告 0 错误；test 1412 总（新增 22 全绿）；Linux 本地 48 个平台/区域差异用例红（时钟/盘符/UNC），**失败集合与 alpha.12 基线逐条一致，无回归**；Windows 端以 `ci.yml`（windows-latest 全套）为准
- 规格版本：docs/specs/v3.0-autonomous-sprint.md（§4 浓缩规格 + SPRINT-REPORT v3.0 §6 建议队列）

## 队列状态

| 任务 | 状态 | 证据 | 备注 |
|---|---|---|---|
| P0-1 Core 打开/关闭几何 | done | OpenAnimMath/CloseAnimMath/OpenCloseFrame + 22 测（端点/居中/单调/同步收紧/越界/NaN） | v2.2 §4.3/§4.4 落 Core，纯函数 |
| P0-2 设置面 | done | AppSettings 三项（OpenAnimation 默认开/CloseAnimation 默认关/OpenAnimStyle=inplace）+ 设置页控件 | 值一律小写（Canonical 归一）；SHOW/DESTROY 路径真即时 |
| P0-3 打开动画全链路 | done | SHOW 钩子 + 占位帧（t=0 同帧起播）+ 8ms×12 捕获重试 + SwapImage 换帧；fromIcon 实验形态 | 首帧竞态按规格处理；失败有 `动画未播` 行 |
| P0-4 关闭动画全链路 | done | DESTROY 钩子（默认不播）+ 前台缓存帧 + CloseCacheMaxAgeMs=1000 token + hwnd→pid 记账 | 未知窗口不演（tooltip 不误伤） |
| P0-5 自家窗口真动画 | done | OwnWindowAnimator（Content 收拢 fade+scale→92%）+ 设置/关于本机 OnClosing 防重入 | 不受 CloseAnimation 约束 |
| P0-6 启动弹跳联动 | done | StartSustainedBounce（vsync 循环内反复弹）→ 首窗 SHOW 收口 / 10s 超时；点击+右键菜单同语义 | exe 基名=进程名匹配；UWP 例外由超时兜底 |
| P0-7 旧打开动画让位 | done | WindowTracker：ghost 版开启时不再叠加 AW_BLEND | 两条打开动画不打架 |
| P0-8 CI 门禁 | done | ci.yml：windows-latest 全套（native /W4 /WX + NativeSmoke + build + test）+ ubuntu 交叉编译哨兵 | RUN#4 **全绿**（windows-gate + linux-cross-build） |
| P0-8b 首轮 CI 修复 | done | ① global.json 钉 .NET 8（runner 预装 10 SDK → 新编译器 CS0023）；② build-native.cmd 改候选路径直查（runner 已迁 windows-2025-vs2026 镜像，VS 2026 Enterprise 在 %ProgramFiles%\\18\\Enterprise）；③ 时钟两测去时区依赖（ToLocalTime × UTC 机器） | global.json / scripts/build-native.cmd / DockWidgetTests；三轮实证：RUN1 native 路径+CS0023 → RUN2 native 探测落空 → RUN3 1410/1412 → RUN4 全绿 |
| P0-9 收尾交付 | done | 版本 bump alpha.13、README 动画节、SPRINT-REPORT §alpha.13、OV-06~OV-09 包 | 本文件即检查点 |
| AnimLab 完整台 | todo | 未动（预算给了打开/关闭全链路） | 下冲刺首位候选；anim-params.json 热加载仍可用 |
| native DComp 宿主 | blocked | 非 Windows 机器无 MSVC；CI 已具备编译能力（build-native.cmd 进 ci.yml），代码待写 | 沿用 v3.0 裁定：不写假代码、不标完成 |

## 当前进行中

- 无（队列清空；中断续跑从"下一个动作"开始）

## 下一个动作（中断续跑从这里开始）

1. AnimLab（tools/AnimLab）：窗口列表 + 逐窗 [genie 预览]/[morph]/[open]/[close] 按钮 + 帧时间图 + 热加载（v3.0 §Q3 规格）。
2. OV-06~OV-09 回填回收（所有者 ≤20 分钟）→ 按结果修打开/关闭链路残余（重点：OV-06 重影观感）。
3. native BN_Anim_* 动画宿主：CI 已验证 VS 2026 v145 工具链可编译 native（/W4 /WX 过），代码可先写交 CI 验证。

## BLOCKED 清单

| 项 | 原因 | 回收 |
|---|---|---|
| native DComp 动画宿主（BN_Anim_*） | 本机无 MSVC；CI 可编译但代码未写 | 下冲刺按 v2.2 §3 实现，ci.yml 已具备 /W4 /WX 验证能力 |

## UNVERIFIED 清单（→ docs/owner-verification/）

OV-06 打开观感 / OV-07 关闭观感 / OV-08 弹跳收口 / OV-09 自家窗口退场（OV-01~05 沿用上冲刺）

## 门禁快照

- build: **0 警告 / 0 错误**（本地交叉编译 + CI windows-gate 双口径均过）
- test: **1412 总 / 1412 通过 / 0 失败**（CI RUN#4 windows-latest 实测；新增 22 测全绿）
- native: **CI 实测可编译**（VS 2026 Enterprise v145 工具链，/W4 /WX 过，三工程 + NativeSmoke 绿）——alpha.12 时"native 不可编译"的阻塞在 CI 侧已解除
