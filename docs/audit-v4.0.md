# 压缩版 Phase 0 审计（v4.0.0-alpha.11 基线，2026-10-31）

> 只跑可自动化验证项（构建/单测/结构存在性/日志判据）。人工目视项进 owner-verification 包。
> 判定依据是代码与测试实存，不是提交信息声称。

## 1. 门禁

| 项 | 结果 |
|---|---|
| `dotnet build Beautiful.sln -c Debug` | 0 警告 0 错误（基线） |
| `dotnet test` | 1345 通过 0 失败（基线）；冲刺后 1390/0 |
| Release publish win-x64 | 成功（dist\Beautiful） |

## 2. 动画子系统结构审计

| 组件 | 实存 | 测试 |
|---|---|---|
| 灯罩数学 GenieLamp | src/Beautiful.Core/Animation/GenieLamp.cs | GenieLampTests |
| 条带数学 GenieMath | 同上 | GenieMathTests |
| 网格 GenieMesh | 同上 | AnimationMathTests |
| 渲染 GenieMeshGhost / MinimizeAnimator | src/Beautiful.App/Animation/ | （UI 层，单测不覆盖，日志取证） |
| 参数/曲线/门禁/报告（本次新增） | AnimParams/AnimCurve/AnimPolicy/AnimReport | 45 个新测试 |
| native 底座（材质/捕获/注入） | src/Beautiful.Native（.vcxproj） | 本机无 MSVC，不可编译验证 |
| WGC 捕获 | src/Beautiful.Native/WgcCapture.cpp | 同上 |

## 3. "将删模块"判定（v2.0 Phase 1 冲突项）

| 模块 | v2.0 裁定 | 实存状态 | 本次裁定 |
|---|---|---|---|
| 歌词 | 删 | v4.x 活跃开发（Dock 媒体胶囊/顶栏药丸） | **保留**：删除与产品方向直接冲突 |
| 三主题（鸿蒙/GNOME/Win11） | 删（只留 macOS） | ThemeManager 四条线 | 保留：用户可见功能 |
| 玻璃壳 | 删 | TaskbarGlassOverlay + 全屏避让 | 保留 |
| 壁纸取色 | 删 | WallpaperAccentService | 保留 |

理由：v2.0 总纲的 Phase 1 是在"壳层收窄为纯 macOS 壳"前提下写的；基线仓库的 v4.x
主线已经把这些模块做成了用户实际在用的功能。删除动作不可自动化验证"更好"，
只可验证"少了"——按两原则（最接近 macOS 语义 + 最诚实降级），如实保留并在报告声明。

## 4. 残留风险

- 帧性能：mesh 引擎 P95 80-146ms（目标 <20ms）→ native 渲染器是唯一根治路径。
- 24H2 系统不响应 SPI_SETANIMATION → 双动画并存，已如实降级。
- 无头取证环境抢不到前台锁 → 冷启动第一下最小化不播（缓存无帧），
  真实交互场景不受影响（点按=前台=缓存有帧）。
