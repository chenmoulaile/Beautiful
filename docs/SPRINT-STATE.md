# SPRINT-STATE（v3.0 冲刺检查点 · v4.0.0-alpha.18）

- 冲刺开始：2026-10-31 / 最后更新：2026-10-31（P5 冲刺：最小化白窗口放行根因修复 + 通知中心缘推滑入 + AnimLab 异常全链落盘 + 访达滑行手感）
- 分支：`agent/alpha.18-minfix`（自 `agent/alpha.17-cbt` @ `7ebcacd`；alpha.17 分支同步远端）
- 开发环境：**非 Windows 机器**（Linux 交叉编译；MinGW-w64 + clang-19 原生构建线沿用 `scripts/setup-mingw.sh` / `scripts/build-hook-dll.sh`）
- 门禁：本地 App/Core/AnimLab build 0W0E；test **1433 总 / 1387 过 / 46 红**（46 红 = 与 alpha.17 HEAD **worktree 对照逐项一致**，零回归）；Windows 端以 `ci.yml`（windows-latest 全套）为准
- 本冲刺动因：所有者真机反馈 ①**最小化动画还是原生、原生 windows 的最小化动画还存在** ②**通知中心滑入像卡住** ③**AnimLab 报 TargetInvocationException 无真因** ④**访达滑动动画太生硬**；要求反馈清单改 md 格式直接勾选

## 队列状态（本冲刺）

| 任务 | 任务号 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| P5-1 最小化根因（★★★） | done | **结构根因锁定**：`FrameHasContent` 把"整帧同色"判废——空记事本纯白整帧同色 → 抓帧永远"失败" → CBT 直接放行 → 原生动画照播。打开/关闭不受影响因为 SHOW 路径不抓像素（这精确解释"为什么只有最小化失效"）。修复：判废只认 BGRA 全零（DIB 未写入特征）+ `MarkEmptySlot` 空帧槽（Width=0）抓帧失败照样拦截 + 宿主 `PlayMinimizeFromHook` 空帧先试前台缓存帧兜底播 genie，连缓存都没有才静默落位（关 DWM 过渡） | 原生动画从此没有出场机会；宿主死亡时 PostMessage 自然失败放行，红线不变 |
| P5-2 通知中心滑入重做（★★） | done | 旧版只平移 20px+140ms 淡入（窗口不透明背景下肉眼不可见 = "卡在那里"）。重做：窗口本体从屏幕右缘外推入（320ms easeOutCubic，SetWindowPos 逐帧、尺寸不变不触发重排）；`PositionAtRightEdge` 先落屏外防 Show→Loaded 间隙露脸；Esc/点面板外/二次点击**对称滑出**（220ms）后关闭 | macOS"从边缘推出来"的忠实语义 |
| P5-3 AnimLab 加固（★★） | done | 真机日志只记了 `TargetInvocationException` 外层一句（InnerException 全丢 = 根因永远查不到的直接原因）。修复：移除 StartupUri 改手动建窗（异常在本处拿全链）；`WriteCrashLog` 落盘 `ex.ToString()`；弹窗显示 `GetBaseException()`；构造分段 `TryStep` 降级（任何一段失败窗口照常出现+状态栏红字）；建窗失败 `Shutdown(1)` 不留僵尸 | 下次再报错，日志必含真因 |
| P5-4 访达滑行手感（★） | done | 新增 `SmoothScrollBehavior`：滚轮改【目标累积 + easeOutCubic 220ms】滑行，连滚连贯、停手减速停稳；列表视图配 `VirtualizingPanel.ScrollUnit="Pixel"`（像素粒度+虚拟化并存，不动 CanContentScroll）；分栏视图横向滑行；图标/侧栏同接；Gallery 保留原滚轮选择逻辑。目录内容就位加淡入+6px 上滑过渡（140ms），搜索过滤不播防闪 | "太生硬"的直接回应；Restore/弹跳等其他动效留待下轮 |
| P5-5 交付管线 | done | 测试清单改 **md 格式**（`[ ]` 直接勾选，所有者点名要求）随 zip 交付；dotnet 探测链加固（DOTNET_ROOT → 沙盒安装点 → PATH，全空报错不静默）；zip 74MB/266 files，三件 PE + coreclr 终验过，`Beautiful.Hook.dll` 与仓库产物 sha256 一致；download 只留 alpha.18 zip + 测试清单.md | alpha.17 旧包已清理 |

## 沿用上冲刺

| 任务 | 状态 | 备注 |
|---|---|---|
| P0/P1/P2/P3/P4 全部 | done | AnimLab/托盘图标/Finder 入口/CBT 真替换/协议 v2/打开关闭真替换/通知中心加固/一键诊断（详见 alpha.13~17 检查点） |

## 已知限制（如实降级，非缺陷）

- 管理员权限窗口受 UAC 隔离：钩子 DLL 注入不进，原生行为照旧（系统安全边界，MyDockFinder 同样如此）
- 32 位应用：64 位钩子 DLL 不注入（极少数老应用走事后路径/无动画）
- SHOW 接管依赖目标窗口接受 Cloak：个别自绘合成窗口若 DwmSetWindowAttribute 拒绝，钩子不拦、原生显示照旧（宁原生不穿帮）
- 关闭动画默认关（macOS 忠实）：开启后走销毁前帧重演，**不阻断销毁**
- `Beautiful.Native.dll`（MSVC 线）仍由 CI 独立构建，zip 不含；缺失自动纯 C# 降级
- BLOCKED（长期）：wine 运行期冒烟（网络 3 次失败）；Windows 运行期行为以 CI 截图+真机验收为准
- AnimLab alpha.17 的 TargetInvocationException 真因未拿到（旧日志只存外层）；alpha.18 起任何异常落盘全链，若复现直接回传 animlab-crash.log 即可定位
- 访达：滚轮滑行/内容过渡本轮落地；文件夹切换 push 过渡、缩略图惯性等深度还原留待下轮

## 下一个动作（中断续跑从这里开始）

- ~~推送 → CI 终审~~ **done**：RUN #20（sha 70fcf5b）windows-gate + linux-cross-build 全绿
- 真机验收：zip 内 `测试清单.md`（B 节空白记事本最小化 = 本轮核心验证；C 节缘推滑入；D 节 AnimLab）
- 所有者回传：md 清单直接勾选；若有异常，【复制诊断信息】/ animlab-crash.log 一并附上
