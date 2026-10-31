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

# 6) 打 zip
mkdir -p "$OUT_DIR"
rm -f "$ZIP"
(cd "$WORK/app" && zip -qr "$ZIP" .)
echo "── 完成：$ZIP ($(du -m "$ZIP" | cut -f1) MB)"
