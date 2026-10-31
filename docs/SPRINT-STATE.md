# SPRINT-STATE（v3.0 冲刺检查点 · v4.0.0-alpha.16）

- 冲刺开始：2026-10-31 / 最后更新：2026-10-31（P3 冲刺：最小化真替换——自制 CBT 前置钩子层 + AnimLab/托盘/Finder 修复）
- 分支：`agent/alpha.16-cbt`（自 `agent/alpha.15-p0` @ `16614f5` 切出；所有者授权 agent 自主维护；main 停在 `2b93a28` = alpha.12）
- 开发环境：**非 Windows 机器**（Linux 交叉编译；本轮新增 **MinGW-w64 + clang 交叉原生构建线**，见 `scripts/setup-mingw.sh` / `scripts/build-hook-dll.sh`）
- 门禁：本地 build 0 错误；test **1427 总 / 1381 过 / 46 红**（46 红 = 与 alpha.15 完全相同的 Linux 平台差异基线，**无回归**）；Windows 端以 `ci.yml`（windows-latest 全套）为准
- 本冲刺动因：所有者真机反馈 ①打开/关闭/最小化动画"未替换原生动画，要么没有要么不好看" ②最小化开始时一片白光 ③AnimLab 双击无窗口 ④顶栏托盘只有字不是图标 ⑤Finder"没看到你说的东西" ⑥**授权：没有 C++ 原生层就从 0 自制**

## 队列状态（本冲刺）

| 任务 | 状态 | 证据 | 备注 |
|---|---|---|---|
| P3-1 深度侦察 | done | 定位根因：AnimLab=framework-dependent 发布在扁平目录解析不到框架；托盘条"字"=MakeTrayStripTile 的首字母占位；白光/双动画=WinEvent 事后补播架构（原生动画早已开跑） | 全部结构性根因，非偶发 |
| P3-2 原生构建线（从 0 自制） | done | Debian 腾讯镜像拉 mingw-w64 + clang-19 deb 免 root 解包（`scripts/setup-mingw.sh`）；clang `-fms-extensions` 编 MS SEH + mingw gcc 链接（`scripts/build-hook-dll.sh`）；产物 `native/hook/Beautiful.Hook.dll` 95KB 纯静态零依赖，PE/导出已验 | **绕开了"无 MSVC"两年来的原生层死锁**；GH CDN 慢，走国内镜像 |
| P3-3 CBT 前置钩子 DLL | done | 纯 C：全局 WH_CBT，HCBT_MINMAX(最小化方向 show 码过滤)在原生动画开跑前 PrintWindow(PW_RENDERFULLCONTENT) 抓帧入共享内存双槽 → PostMessage → 等 ack≤40ms → 返回 1 阻断原生最小化；自家进程/抑制窗口/任何异常一律放行（SEH 全包）；BH_Install/Uninstall/Suppress/Version 四导出 | 注入组件红线：绝不抛异常给宿主、绝不阻塞>40ms、绝不拦截失败态 |
| P3-4 C# 真替换接入 | done | `MinimizeHookClient`（MMF+EventWaitHandle+消息窗）；`MinimizeAnimator.PlayMinimizeFromHook`（门禁放行→genie 真替换→收尾静默落位；拒绝也必须静默落位兜底）；`SilentMinimize`=DWMWA_TRANSITIONS_FORCEDISABLED(3)+ShowWindow+还原；`_hookMinimizedAt` 2s 防双播（MINIMIZESTART 二次到达不再补播）；缺 DLL/装不上自动回退 alpha.15 事后路径 | **白光根治**：genie 首帧=钩子预抓的真画面，覆盖层与真窗口像素重合 |
| P3-5 主动路径真替换 | done | `App.MinimizeWindow` 统一入口：Dock 点击（WindowTracker.ActivateOrMinimize 加 minimizeOverride）、Dock 右键菜单、顶栏菜单、热角——全部改走 `MinimizeNow`（预抓+关过渡+静默落位+genie） | 不依赖钩子层也成立 |
| P3-6 AnimLab 双击无窗口修复 | done | `package-release.sh` AnimLab 改 **self-contained** 并入主包（461 个运行时文件与主程序哈希一致才放行）；AnimLab 加 DispatcherUnhandledException/UnhandledException 落盘 `animlab-crash.log` + 弹窗 | 旧根因：framework-dependent apphost 在扁平目录里解析不到 shared/ 框架 |
| P3-7 托盘图标化 | done | 新 `TrayIconFallback`：owner 窗口→OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)→QueryFullProcessImageNameW→ExtractIconExW→HIcon→BitmapSource（按映像路径缓存 128）；顶栏托盘条：真实像素 > exe 图标 > 中性圆点，**废除首字母文字占位** | UIA 路无 owner 句柄 → 中性圆点 |
| P3-8 Finder 显性入口 | done | 顶栏"文件"菜单"新建访达窗口"（此前唯一入口是 Dock 右键"在访达中打开"与 `--finder` 参数——功能都在、找不到门） | Finder 四视图/QuickLook/标签页/侧栏"我的"早已实现 |
| P3-9 打包 | done | zip 74MB：主程序+**Beautiful.Hook.dll**+AnimLab（自包含）+测试清单.txt+README；终验三件 PE + coreclr 在位；download 只留 zip；版本 bump 4.0.0-alpha.16 | 清单数字与 AnimParams 实际默认对齐（360/260/500） |

## 沿用上冲刺

| 任务 | 状态 | 备注 |
|---|---|---|
| P0/P1/P2 全部 | done | 打开/关闭动画 + Cloak 入场 + AnimLab + 时钟防御（详见 alpha.13~15 检查点） |

## 已知限制（如实降级，非缺陷）

- 管理员权限窗口受 UAC 隔离：钩子 DLL 注入不进，原生动画照旧（系统安全边界，MyDockFinder 同样如此）
- 32 位应用：64 位钩子 DLL 不注入（极少数老应用走事后路径）
- 双进程同时最小化 <40ms 窗口内的槽竞争：极小概率一帧花屏（双槽设计下最坏影响是替身画面，不影响最小化本身）
- `Beautiful.Native.dll`（MSVC 线：WGC/DComp/TrayBridge）仍由 CI 独立构建，zip 不含；缺失自动纯 C# 降级
- BLOCKED（长期）：wine 运行期冒烟（网络 3 次失败）；Windows 运行期行为以 CI 截图+真机验收为准

## 下一个动作（中断续跑从这里开始）

- 推送分支 → CI windows-latest 终审（0W0E + 1381 基线 + NativeSmoke + 截图取证）
- 真机验收：zip 内 测试清单.txt（B 节四条最小化路径为本轮核心）
- 所有者回传 logs：钩子层状态行「CBT 前置钩子已挂载」= 真替换生效
