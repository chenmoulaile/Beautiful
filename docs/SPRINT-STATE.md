# SPRINT-STATE（v3.0 冲刺检查点 · v4.0.0-alpha.15）

- 冲刺开始：2026-10-31 / 最后更新：2026-10-31（P2 冲刺：最小化链路修复 + 观感调参 + 运行期截图取证）
- 分支：`agent/alpha.15-p2`（续接 alpha.14；main 停在 `2b93a28` = alpha.12）
- 所有者反馈驱动：① 最小化动画完全不可用（真机实锤：MINIMIZESTART 到手已 iconic，判废线 1500ms 导致缓存帧全部被拒）② 打开/关闭动画"太快/几乎没有"（默认参数观感量级错误）③ 时钟点击报错（链路静态审查未找到堆点，交运行期截图取证）④ download 交付形态收紧（zip + 内含测试清单）
- 门禁：build 0W0E（App+AnimLab）；test 1420 总 / Linux 46 基线红无回归；xaml_check 0 error；CI 终审

## 队列状态（本冲刺 P2）

| 任务 | 状态 | 证据 | 备注 |
|---|---|---|---|
| P2-1 最小化 genie 修复 | done | `MinimizeCacheMaxAgeMs`（默认 9s，钳 1s~30s）：MINIMIZESTART 事件路径改用宽松缓存龄；补 clamp + 日志 | 根因：事件到手时 IsIconic=True（代码注释早就预判"真机一次没播过就是这么来的"），1500ms 判废线把静止期窗口的缓存帧全部拒掉 → 放宽后点最小化按钮也能收进 Dock |
| P2-2 动画观感调参 | done | Open 200→**320ms**、起始 0.94→**0.88**；Close 150→**220ms**、终态 0.92→**0.86**（属性默认 + Normalized fallback 同步 + 测试） | 修掉"Normalized fallback 与属性默认脱节"的一致性隐患（历史默认 200/0.94 残留在 clamp fallback） |
| P2-3 运行期截图取证 | done | `screenshot.yml`：windows-latest 启动 app → 全屏截图×3 → 模拟点击时钟 → logs 无"未处理异常"断言 → artifact 回流 | 解决"agent 看不到真机观感"的结构性缺口；`scripts/ci-screenshot.ps1` |
| P2-4 深度静态检查 | done | `tools/xaml-check/xaml_check.py`：x:Class 对齐 / 事件处理器存在性（跨 partial 文件）/ StaticResource 键解析 / .Result 危险模式 —— **25 xaml / 203 cs 全过 0 error**；3 处 .Result 经人工核审均为 ContinueWith 续体安全 | 时钟报错链路逐环审查：CalendarFlyout/NotificationCenterWindow/FluentPopupHost/SettingsService/NotificationService 静态均防御到位 → 疑点转向运行期（截图取证 job 兼任） |
| P2-5 交付形态收紧 | done | `测试清单.md`（A~G 七节，B/C 标★重点项）随包；README-交付说明指向清单 | |
| P2-6 版本与文档 | done | bump alpha.15、SPRINT-STATE | |

## 沿用上冲刺（P0/P1 全 done，详见 alpha.13/14 检查点）

| 任务 | 状态 | 证据 | 备注 |
|---|---|---|---|
| P1-1 交叉交付管线 | done | `scripts/package-release.sh`：交叉编译 → PE 头校验（x64 GUI）→ AnimLab 三件套并入 → 交付说明 → zip | 产物 `Beautiful-4.0.0-alpha.14-win64.zip`（74MB，self-contained 解压即用）；README 新增"免 Windows 机器的交叉交付包"节 |
| P1-2 AnimLab 调参台 | done | `tools/AnimLab`：参数表（JsonObject 双向 + FromJson 校验）+ 打开/关闭/Genie/Morph 四演示（复用 Core 数学逐帧渲染）+ 缓动曲线图 + 帧时间图（FrameTimeStats avg/P95/max/超预算）+ 文件 watcher 热加载（防抖 300ms） | 支持 `AnimLab.exe <主程序 anim-params.json>` 直改主程序配置，保存后主程序 watcher 立即热加载；交叉编译 0W0E |
| P1-2b FrameTimeStats | done | `Core/Animation/FrameTimeStats.cs` 纯函数 + 7 测（空/非法值丢弃/均值/P95 最近秩/预算占比/自定义预算/预算回落） | AnimLab 帧图的数据层，非法样本（0/负/NaN）丢弃规则成文 |
| P1-3 版本与文档 | done | Directory.Build.props bump `4.0.0-alpha.14`；SPRINT-STATE/README 更新 | 版本单一来源不变 |

## 沿用上冲刺

| 任务 | 状态 | 备注 |
|---|---|---|
| P0-1 ~ P0-9 | done | 打开/关闭动画全链路 + CI 门禁（详见 alpha.13 检查点，CI RUN#4 全绿 1412/1412） |
| AnimLab 完整台 | **本冲刺完成** | 上轮"下冲刺首位候选"兑现 |

## 当前进行中

- 无（队列清空）

## 下一个动作（中断续跑从这里开始）

1. OV-06~OV-09 回填回收（所有者 ≤20 分钟）→ 按结果修打开/关闭链路残余（重点：OV-06 重影观感）。
2. native BN_Anim_* DComp 动画宿主：CI 已具备 /W4 /WX 编译验证能力，代码按 v2.2 §3 实现、交 CI 验证（本机仍无 MSVC，不写假代码、不标完成）。
3. 交付包带签名/自解压安装器（owner 若要 Inno Setup 线，需 Windows 侧）。

## BLOCKED 清单

| 项 | 原因 | 回收 |
|---|---|---|
| native BN_Anim_*（DComp 宿主） | 本机无 MSVC；代码未写（不预写不可验证的 C++） | 下冲刺按 v2.2 §3 实现，ci.yml 已具备 /W4 /WX 验证能力 |
| Wine 运行期冒烟 | 开发机网络无法拉取 Wine 发行包（三次尝试，100MB 级下载中断） | 运行期目视验证沿用 owner-verification 包；CI windows-latest 是编译+测试终审 |

## UNVERIFIED 清单（→ docs/owner-verification/）

OV-06 打开观感 / OV-07 关闭观感 / OV-08 弹跳收口 / OV-09 自家窗口退场 / OV-10（新增）AnimLab 调参工作流：改 OpenStartScale → 保存 → 主程序立即生效
（OV-01~05 沿用上冲刺）

## 门禁快照（alpha.14）

- build: **0 警告 / 0 错误**（App + AnimLab 双工程交叉编译）
- test: **1419 总 / 1373 过 / 46 红**（红集合 = Linux 平台差异基线：时钟/盘符/UNC；新增 7 测全绿；Windows 终审以 CI 为准）
- PE: **Beautiful.exe = x64 GUI 合法 PE**（含 createdump.exe 校验；self-contained 260 顶层文件 / 179MB，zip 74MB）
- 交付包: **Beautiful-4.0.0-alpha.14-win64.zip**（Beautiful.exe + AnimLab.exe + 运行时 + README-交付说明.txt）

## P3 冲刺追加记录（alpha.16，同分支续推）

| 任务 | 状态 | 证据 |
|---|---|---|
| P3-1 截图取证四轮迭代 | done | quser 第三列解析 + 显式 exit 0；实证 runner 会话本为 console/Active（tscon 7045 属正常） |
| P3-2 时钟报错根因修复 | done | 第四轮截图抓到 InvalidOperationException: Transform is not valid for Window（PlaySlideIn 给 Window 赋 RenderTransform）→ 改 Left+Opacity 双动画；xaml_check 新增 [window-transform] 防回归规则 |
| P3-3 渲染复盘 | done | 第五轮截图：通知中心（大时钟/月历/今天高亮）+ 顶栏菜单镜像 + Dock 全部真机渲染正常；前三轮系误读 |
| P3-4 交付 | done | Beautiful-4.0.0-alpha.16-win64.zip（74MB，含测试清单 H 节取证结论） |
