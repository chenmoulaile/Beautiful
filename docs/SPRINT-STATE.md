# SPRINT-STATE（v3.0 冲刺检查点 · v4.0.0-alpha.14）

- 冲刺开始：2026-10-31 / 最后更新：2026-10-31（P1 冲刺：AnimLab + 交叉交付管线）
- 分支：`agent/alpha.13-p0` → 本冲刺续推于同分支（所有者授权 agent 自主维护；main 停在 `2b93a28` = alpha.12）
- 开发环境：**非 Windows 机器**（Linux 交叉编译 `-p:EnableWindowsTargeting=true`）
- 门禁：本地 build 0 警告 0 错误（App + AnimLab）；test 1419 总，Linux 46 个平台/区域差异用例红（= 上轮基线 48 − 已修时钟 2 测），**无回归**；Windows 端以 `ci.yml`（windows-latest 全套）为准
- 规格版本：docs/specs/v3.0-autonomous-sprint.md（§Q3 AnimLab + 所有者交付管线要求）

## 队列状态（本冲刺）

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
