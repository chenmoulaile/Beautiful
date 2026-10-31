#!/usr/bin/env bash
# ============================================================
# package-release.sh — Linux 交叉编译产出 Windows 桌面 zip 包
# （.NET 8 SDK + EnableWindowsTargeting；产物 self-contained，
#   用户机器无需安装 .NET。CI windows-latest 上的全套门禁仍是终审。）
# 用法：bash scripts/package-release.sh [输出目录]
# ============================================================
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_DIR="${1:-/home/z/my-project/download}"
WORK="$ROOT/build/release-win-x64"

VER="$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
[ -n "$VER" ] || { echo "ERROR: 版本号读取失败（Directory.Build.props）"; exit 1; }
ZIP="$OUT_DIR/Beautiful-$VER-win64.zip"

echo "── 版本：$VER"
echo "── 输出：$ZIP"

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet)"

# 1) 主程序：self-contained win-x64
rm -rf "$WORK"; mkdir -p "$WORK"
"$DOTNET" publish "$ROOT/src/Beautiful.App/Beautiful.App.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -o "$WORK/app" 2>&1 | tail -2

# 2) AnimLab：framework-dependent（主包目录已含全套运行时，apphost 就地可跑）
"$DOTNET" publish "$ROOT/tools/AnimLab/AnimLab.csproj" \
  -c Release -r win-x64 --self-contained false \
  -p:EnableWindowsTargeting=true -o "$WORK/animlab" 2>&1 | tail -2

# 3) 产物校验：主 exe 必须是合法 x64 GUI PE；WPF/coreclr 关键 dll 必须在列
python3 - "$WORK/app" <<'PY'
import struct, sys, os
app = sys.argv[1]
exe = os.path.join(app, "Beautiful.exe")
d = open(exe, "rb").read()
assert d[:2] == b"MZ", "MZ missing"
off = struct.unpack("<I", d[0x3C:0x40])[0]
assert d[off:off+4] == b"PE\0\0", "PE sig missing"
machine = struct.unpack("<H", d[off+4:off+6])[0]
assert machine == 0x8664, f"not x64: {machine:#x}"
need = ["PresentationFramework.dll", "WindowsBase.dll", "coreclr.dll",
        "hostfxr.dll", "clrjit.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll"]
missing = [n for n in need if not os.path.exists(os.path.join(app, n))]
assert not missing, f"missing runtime files: {missing}"
total = sum(os.path.getsize(os.path.join(app, f)) for f in os.listdir(app))
print(f"PE OK (x64 GUI) · {len(os.listdir(app))} files · {total/1e6:.0f} MB")
PY

# 4) AnimLab 三件套并入主包根目录
for f in AnimLab.exe AnimLab.dll AnimLab.runtimeconfig.json; do
  src="$WORK/animlab/$f"
  [ -f "$src" ] && cp "$src" "$WORK/app/" || { echo "ERROR: AnimLab 产物缺 $f"; exit 1; }
done

# 5) 交付说明
cat > "$WORK/app/README-交付说明.txt" <<EOF
Beautiful $VER — Windows 桌面包
════════════════════════════════════════════

【内容】
  Beautiful.exe   主程序（Dock / 顶栏 / Finder / Launchpad / 控制中心 / 切换器）
  AnimLab.exe     动画调参台（开发工具，见文末）
  其余 .dll       .NET 8 self-contained 运行时 —— 无需安装任何运行库

【运行要求】
  Windows 10 1809 (17763) 或更高 · x64 · 解压即用

【首次运行】
  1. 将本目录解压到任意可写位置（建议非系统盘）
  2. 双击 Beautiful.exe
  3. 屏幕顶部出现"顶栏"、底部出现"Dock"即启动成功
  4. 退出：右键 Dock 上的"关于/设置"图标，或任务管理器结束 Beautiful.exe

【已知限制（alpha 阶段）】
  · 本包不含 Beautiful.Native.dll（C++ 原生层：WGC 窗口捕获 / DComp 材质 / 注入）。
    该层需 MSVC 构建线，由 CI（windows-latest，/W4 /WX 门禁）负责编译与验证；
    缺失时主程序自动降级为纯 C# 形态，界面、Dock、Finder 与全部动画照常工作，
    仅原生捕获/托桥类增强能力不可用。
  · 首次启动若被 SmartScreen 提示：点"仍要运行"（本包未做代码签名）。

【AnimLab 调参台】
  直接双击运行：演示/编辑 AnimLab 目录下 config\anim-params.json；
  指定主程序参数文件：AnimLab.exe <主程序目录>\config\anim-params.json
  —— 在 AnimLab 中保存后，主程序的 watcher 会立即热加载新参数（无需重启）。
EOF

# 5.5 测试清单（所有者验收用，直接进 zip —— alpha.15 起按所有者要求随包交付）
cat > "$WORK/app/测试清单.txt" <<'EOF'
════════════════════════════════════════════════════════
 Beautiful 4.0.0-alpha.15 验收测试清单（预计 ≤15 分钟）
 本轮修复：顶栏时钟报错 / 打开关闭动画观感 / 最小化动画可用性
════════════════════════════════════════════════════════

【第 1 节 · 顶栏时钟（上轮 bug：点击报错、打不开）】
  1.1 左键点顶栏时钟
      期望：右侧滑出通知中心面板（含大时钟 + 月历 + 通知卡），无任何错误弹框
  1.2 再点一次时钟 → 面板收起
  1.3 点面板外其他地方 → 面板自动收起；Esc 键 → 收起
  1.4 右键点时钟 → 直接弹月历浮层
  1.5 若仍报错：把 logs\Beautiful-2026XX.log 里含「UI 线程未处理异常」的
      那几行发给作者——现在异常必落日志（不再弹框），有它就能定案根因

【第 2 节 · 打开/关闭动画（上轮反馈：太快或几乎没有）】
  2.1 从 Dock 点开一个未运行的应用（如 计算器）
      期望：窗口以约 90% 尺寸浮现并在 ~0.36s 内长到位（新默认 360ms/0.90），
            过程肉眼可辨；不再是"瞬间出现 + 一层重影"
  2.2 关闭该窗口
      期望：窗口缩小并淡出约 0.26s（新默认 260ms，本轮起关闭动画默认开）
  2.3 打开 设置 → 动画 → "动画速度"滑条拖到 0.5×，重复 2.1
      期望：动画明显变慢（本项上轮完全不生效，本轮已接线）
  2.4 改 config\anim-params.json 里 "OpenDurationMs" 为 800 保存，再开一个窗口
      期望：不重启主程序，下次打开即用 800ms（热加载本项上轮不生效，本轮已接线）
      测完改回 360（或删掉该文件，下次启动自动重建默认表）

【第 3 节 · 最小化动画（上轮反馈：不可用 / 达不到 MyDockFinder）】
  3.1 Dock 上右键窗口图标 → 最小化（Dock 触发路径，上轮就应可用）
      期望：窗口 genie 吸入 Dock 图标（默认 mesh 引擎，500ms）
  3.2 点窗口标题栏的最小化按钮（事件路径，上轮常不播）
      期望：先切到目标窗口停留约 1 秒再点最小化 → genie 播出
            （本轮新增：窗口成为前台时立即补抓一帧，"刚切过去就最小化"也有画面）
  3.3 点 Dock 图标还原窗口
      期望：从图标"流出来"长回原尺寸（还原 450ms）
  3.4 Win+↓ / 任务栏按钮 最小化 → 同 3.2 期望
  3.5 已知限制（如实验收）：
      · Windows 11 24H2 上系统自带"缩向任务栏"动画可能与 genie 叠加
        （系统拒绝关闭接口，属系统侧限制）
      · 若帧率仍觉幻灯片感：可在设置把引擎切为 strips 对比，
        根治依赖 native 渲染层（下一轮）

【第 4 节 · 回归快查（确认本轮没改坏原有功能）】
  4.1 Dock 图标放大/弹跳、点击启动 → 正常
  4.2 Finder 双开窗口、Launchpad 搜索 → 正常
  4.3 设置窗口开合（自家窗口有独立关闭动画）→ 正常
  4.4 顶栏 音量/网络/控制中心 面板开合 → 正常（时钟链路的兜底不影响它们）
  4.5 托盘右键 → 重载界面 → 通知中心面板不残留在屏上（本轮修复点）

【验收结果回填】
  通过项打 [x]：1.1__ 1.2__ 1.3__ 1.4__ 1.5__
              2.1__ 2.2__ 2.3__ 2.4__
              3.1__ 3.2__ 3.3__ 3.4__
              4.1__ 4.2__ 4.3__ 4.4__ 4.5__
  异常项 + 现象（附 logs 文件更佳）：
  ____________________________________________
EOF

# 6) 打 zip
mkdir -p "$OUT_DIR"
rm -f "$ZIP"
(cd "$WORK/app" && zip -qr "$ZIP" .)
echo "── 完成：$ZIP ($(du -m "$ZIP" | cut -f1) MB)"
