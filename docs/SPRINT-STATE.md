# SPRINT-STATE（v3.0 冲刺检查点 · v4.0.0-alpha.17）

- 冲刺开始：2026-10-31 / 最后更新：2026-10-31（P4 冲刺：钩子层 v2——打开/关闭动画真替换 + 通知中心加固 + 一键诊断）
- 分支：`agent/alpha.17-cbt`（自 `agent/alpha.16-cbt` @ `27e85a6` 快进；远端 alpha.16 未及推送，本轮一并推送）
- 开发环境：**非 Windows 机器**（Linux 交叉编译；MinGW-w64 + clang-19 原生构建线沿用 `scripts/setup-mingw.sh` / `scripts/build-hook-dll.sh`）
- 门禁：本地 App/Core/AnimLab build 0W0E；test **1433 总 / 1387 过 / 46 红**（46 红 = 与 alpha.15/16 完全相同的 Linux 平台差异基线，**无回归**；本轮 +6 = HookProtocol 钉子测试）；Windows 端以 `ci.yml`（windows-latest 全套）为准
- 本冲刺动因：所有者真机反馈 ①**通知中心又炸了** ②**开新窗口仍不能替换原生动画** ③**最小化动画还是用不了**；指令"向前推进剩余功能，做完所有能做的工作"

## 队列状态（本冲刺）

| 任务 | 任务号 | 状态 | 证据 | 备注 |
|---|---|---|---|---|
| P4-1 根因深查（三连反馈） | done | 开窗未替换的结构性根因：alpha.15 是"事后 Cloak"——OBJECTSHOW 到手→建替身→抓帧（几十 ms），期间真窗口早已原生上屏，用户永远先看到原生出现再叠替身。关窗更是 DESTROY 后播缓存帧。通知中心"炸"= Loaded 之后全链路在点击 try/catch 之外，任何环境性异常直接升级全局弹框 | 结构性，非偶发 |
| P4-2 协议 v2（Core 单一图纸） | done | `Beautiful.Core/Animation/HookProtocol.cs`：共享头 48B/槽头 48B/Kind(MIN=1,DESTROY=2,SHOW=3)/Pid/矩形/Flags bit1=关闭动画开关；名字 .v1→.v2 防残留节误认；6 钉子测试防两侧漂移 | v1 偏移散在宿主端无测试，本轮收口 |
| P4-3 钩子层 v2（C） | done | `native/hook/beautiful_hook.c`：①HCBT_MINMAX 拦截不变（阻断+40ms ack）②**HCBT_DESTROYWND**：Flags bit1 开启时销毁前抓最后一帧，投递即走不阻断（阻断+重发 WM_CLOSE 会让"未保存"弹两次——明确不选）③**EVENT_OBJECT_SHOW in-context**（SetWinEventHook WINEVENT_INCONTEXT）：回调在目标进程内、ShowWindow 返回前同步执行→就地 DwmSetWindowAttribute(CLOAK)→首帧永不出现→等 ack 40ms：ack=宿主接管演完 Uncloak，超时=就地 Uncloak 放行。过滤：顶层/无 owner/非工具窗/类黑名单/未 Cloak/尺寸闸门。链接加 -ldwmapi；产物 99KB PE 已验 | 全回调 SEH 包裹，红线继承 |
| P4-4 宿主端 v2（C#） | done | `MinimizeHookClient`：按 Kind 分派；MIN=拷帧后 ack（v1 语义）；SHOW=簿记后立即 ack→后台抓帧（`WindowCapture.CapturePrintWindowOnly`：**绝不退屏幕路**——Cloak 时屏幕上是旧背景）→门禁播替身→演完 Uncloak；接管台账+**600ms 看门狗**（PrintWindow 挂死也不许窗口隐形）+退出收摊 Uncloak；DESTROY=拷帧→`PlayCloseFromHook`。Flags bit1 由 2s 定时器与设置项同步（读-改-写保 bit0） | 双保险：钩子 40ms 就地还原 + 宿主 600ms 看门狗 |
| P4-5 动画器接线 | done | `MinimizeAnimator`：新增 `PlayOpenFromHook`（不透明替身 90%→100%，演完先 Uncloak 再收替身，终帧重合无跳变）与 `PlayCloseFromHook`（销毁前新帧，零龄）；`SetCloak` internal 化；**事后路径整体短路分流**（HookBridge.Available 时 PlayOpen/PlayClose 不播——杜绝双动画）；首窗报告与 pid 记账保留（Dock 弹跳/close 台账不丢） | 两条路径互斥由 Available 分流 |
| P4-6 通知中心加固 | done | `NotificationCenterWindow`：Loaded 链路（定位/重画/滑入）逐段 try/catch；20s 时钟定时器 SafeApplyClock；事件重画 SafeRebuild；**单卡片构建异常跳过并计数显示**（不再一张坏卡拖垮面板）；`NotificationBannerHost.InvalidateLayout` 单条隔离 | "又炸了"的所有已知敞口关闭 |
| P4-7 一键诊断 | done | 设置→动画：诊断状态行（动画器 MountStatus+钩子层 Status+通知服务状态+设置实值）；【复制诊断信息】（App.BuildDiagnosticsText→剪贴板，剪贴板被锁则引导日志目录）；【打开日志文件夹】。报障不再需要翻文件系统 | 下轮反馈成本大幅下降 |
| P4-8 打包与交付 | done | zip 74MB：主程序+Hook.dll(v2)+AnimLab(自包含)+测试清单(alpha.17 版)+README；终验三件 PE+coreclr 在位；download 只留 alpha.17 zip；版本 bump 4.0.0-alpha.17 | 测试清单按三路真替换重写 |

## 沿用上冲刺

| 任务 | 状态 | 备注 |
|---|---|---|
| P0/P1/P2/P3 全部 | done | AnimLab/托盘图标/Finder 入口/最小化 CBT 真替换（详见 alpha.13~16 检查点） |

## 已知限制（如实降级，非缺陷）

- 管理员权限窗口受 UAC 隔离：钩子 DLL 注入不进，原生行为照旧（系统安全边界，MyDockFinder 同样如此）
- 32 位应用：64 位钩子 DLL 不注入（极少数老应用走事后路径/无动画）
- SHOW 接管依赖目标窗口接受 Cloak：个别自绘合成窗口若 DwmSetWindowAttribute 拒绝，钩子不拦、原生显示照旧（宁原生不穿帮）
- 关闭动画默认关（macOS 忠实）：开启后走销毁前帧重演，**不阻断销毁**（避免"未保存提示"弹两次），窗口消失与替身收缩之间无接缝魔法，属"忠实重演"而非"阻止消失"
- `Beautiful.Native.dll`（MSVC 线）仍由 CI 独立构建，zip 不含；缺失自动纯 C# 降级
- BLOCKED（长期）：wine 运行期冒烟（网络 3 次失败）；Windows 运行期行为以 CI 截图+真机验收为准
- 通知中心"又炸了"的原始异常未拿到日志（用户未回传），本轮按"所有已知敞口逐段兜底+一键取证"处置；若仍复现，请用【复制诊断信息】回传

## 下一个动作（中断续跑从这里开始）

- ~~推送 → CI 终审~~ **done**：RUN 41212736169 windows-gate + linux-cross-build 全绿（native /W4 /WX、NativeSmoke、HookDll 探针 x64 PE+四导出、0W0E、测试门禁过）
- 真机验收：zip 内 测试清单.txt（B 节开窗真替换 / C 节关闭真替换为本轮核心）
- 所有者回传：设置→动画→【复制诊断信息】的结果（本轮新增取证方式）
