# SPRINT-STATE（v3.0 冲刺检查点 · v4.0.0-alpha.15）

- 冲刺开始：2026-10-31 / 最后更新：2026-10-31（P2 冲刺：真机反馈修复——时钟报错 + 动画观感 + 最小化可用性）
- 分支：`agent/alpha.15-p0`（自 `agent/alpha.14-p1` @ `7797439` 切出；所有者授权 agent 自主维护；main 停在 `2b93a28` = alpha.12）
- 开发环境：**非 Windows 机器**（Linux 交叉编译 `-p:EnableWindowsTargeting=true`；沙箱中途回收一次，环境已重建）
- 门禁：本地 build 0 警告 0 错误（Core + App + AnimLab）；test **1427 总 / 1381 过 / 46 红**（46 红 = 上轮完全相同的 Linux 平台差异基线：FinderPathSafety 10、FinderDropPlanner 12、FinderClipboard 6、FinderViewOptions 4、LauncherIndex 5、LauncherTarget 9，**无回归**）；本冲刺新增 8 测全绿（AnimParams 新默认 1 + 兜底回落 1 + GenieLamp split 重载 6）；Windows 端以 `ci.yml`（windows-latest 全套）为准
- 本冲刺动因：所有者真机反馈 ①点顶栏时钟报错打不开 ②打开/关闭动画太快几乎没有 ③最小化动画不可用 ④要求先全面查 bug 再开发

## 队列状态（本冲刺）

| 任务 | 状态 | 证据 | 备注 |
|---|---|---|---|
| P2-1 深度代码审查 | done | 双路审查（人工逐文件 + 子代理全链路）：时钟链 8 个方向、动画链 A1-A7、最小化链 B1-B7 全部过一遍；产出 bug 清单（B1~B17，见提交说明） | 结论：钩子都在、数学正确，断点集中在"参数没接上""画面没留够""真窗口从不隐藏"三类工程问题 |
| P2-2 时钟点击报错修复 | done | `TopBarWindow.OpenClockFlyout/OpenCalendar` 本地 try/catch + 日志 + 回退（异常不再升级为全局弹框，时钟永不白点）；`NotificationCenterWindow`：`CloseCurrent()` + 顶栏 OnClosed 收面板（孤儿面板）、兜底锚点 DIP→物理像素（高 DPI 定位错屏）、`EnsureServiceStartedAsync` 兜底；静态审查未能 100% 锁定真机异常源（无用户日志），**防御已保证任何异常不弹框、不打不开，根因落在 logs** | 待所有者回传 logs/Beautiful-*.log 的"UI 线程未处理异常"行即可定案根因 |
| P2-3 动画观感修复 | done | ① 时长热加载接线：`AnimParamsStore.ParamsChanged` 事件 → `AnimPolicy.UpdateParams`（alpha.14 的 UpdateParams 零调用 = 热加载死路，本冲刺打通）；② 设置页"动画速度"滑条接线（alpha.14 零消费方 → 与参数表倍率相乘进策略）；③ 默认时长 200→360ms / 0.94→0.90 / 关闭 150→260ms + 关闭动画默认开；④ **DWM Cloak 真窗口入场**：打开动画从"叠在已可见窗口上的重影"变成真入场（Cloak 失败自动回退旧模式；Dispose 中途收摊强制 Uncloak 红线） | 观感类修复需真机目视验收（zip 内 测试清单.txt 第 2 节） |
| P2-4 最小化动画可用性 | done | ① `EVENT_SYSTEM_FOREGROUND` 钩子：窗口成为前台立即补采一帧（绕过 2Hz 轮询节流；250ms 绝对节流 + 单飞在途 + 新鲜帧跳过三道闸）——"刚切到窗口就最小化必然不播"的最大断点补上；② `AnimParams.LampSplit` 接线（alpha.14 零读取 → GenieLamp 增加 split 重载，默认值逐位不变）；③ mesh 网格 24×8→16×6（顶点 225→119，对冲 P95 80-146ms 的幻灯片感） | 24H2 双动画打架（SPI 被拒）是系统侧限制，维持如实降级；根治 = native BN_Anim_*（仍 BLOCKED，无 MSVC） |
| P2-5 交付管线 | done | `package-release.sh`：测试清单写入 zip（`测试清单.txt`）；版本 bump `4.0.0-alpha.15`；download 目录只保留 zip | 所有者要求"测试清单打包进 zip"落成 |

## 沿用上冲刺

| 任务 | 状态 | 备注 |
|---|---|---|
| P0-1 ~ P0-9 / P1-1 ~ P1-3 | done | 打开/关闭动画全链路 + AnimLab + 交叉交付（详见 alpha.13/alpha.14 检查点） |

## 当前进行中

- 无（队列清空）

## 下一个动作（中断续跑从这里开始）

1. 所有者真机验收 zip 内 `测试清单.txt`（时钟 × 动画 × 最小化三节，≤15 分钟）；回传 logs 里"UI 线程未处理异常"行定案时钟根因（若仍复现）。
2. native BN_Anim_* DComp 动画宿主：CI 已具备 /W4 /WX 编译验证能力，代码按 v2.2 §3 实现（本机仍无 MSVC，不写假代码、不标完成）。
3. OV-06~OV-09 回填回收（所有者 ≤20 分钟）。

## BLOCKED 清单

| 项 | 原因 | 回收 |
|---|---|---|
| native BN_Anim_*（DComp 宿主） | 本机无 MSVC；代码未写（不预写不可验证的 C++） | 下冲刺按 v2.2 §3 实现，ci.yml 已具备 /W4 /WX 验证能力 |
| Wine 运行期冒烟 | 开发机网络无法拉取 Wine 发行包（三次尝试，100MB 级下载中断） | 运行期目视验证沿用 zip 内测试清单；CI windows-latest 是编译+测试终审 |
| 时钟报错根因终审 | 静态审查全链路无必现抛点；需要真机 logs 的一次异常堆栈 | 防御修复已上线（异常不弹框且必落日志）；所有者回传日志行即可闭环 |
