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

# 2) AnimLab：self-contained（alpha.16 修：旧包发 framework-dependent，
#    apphost 在扁平目录里解析不到 shared/ 框架 → 真机双击无窗口）。
#    直接发布到 app 同目录：同名运行时文件版本一致，覆盖无害；异同文件哈希校验兜底。
"$DOTNET" publish "$ROOT/tools/AnimLab/AnimLab.csproj" \
  -c Release -r win-x64 --self-contained true \
  -p:EnableWindowsTargeting=true -o "$WORK/animlab" 2>&1 | tail -2

# 3) 产物校验：主 exe 必须是合法 x64 GUI PE；WPF/coreclr 关键 dll 必须在列
#    alpha.16 追加：Beautiful.Hook.dll 必须是 x64 DLL；AnimLab.exe 必须同目录自带完整运行时
python3 - "$WORK/app" <<'PY'
import struct, sys, os
app = sys.argv[1]
def pe(path, want_dll=False):
    d = open(path, 'rb').read()
    assert d[:2] == b"MZ", f"{path}: MZ missing"
    off = struct.unpack("<I", d[0x3C:0x40])[0]
    assert d[off:off+4] == b"PE\0\0", f"{path}: PE sig missing"
    machine = struct.unpack("<H", d[off+4:off+6])[0]
    assert machine == 0x8664, f"{path}: not x64 ({machine:#x})"
    if want_dll:
        chars = struct.unpack("<H", d[off+22:off+24])[0]
        assert chars & 0x2000, f"{path}: not a DLL"
    return len(d)
pe(os.path.join(app, "Beautiful.exe"))
need = ["PresentationFramework.dll", "WindowsBase.dll", "coreclr.dll",
        "hostfxr.dll", "clrjit.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll"]
missing = [n for n in need if not os.path.exists(os.path.join(app, n))]
assert not missing, f"missing runtime files: {missing}"
print(f"PE OK (Beautiful.exe x64 GUI · 运行时齐全) · {len(os.listdir(app))} files")
PY

# 3.5) 钩子层：Beautiful.Hook.dll（alpha.16 新增，MinGW 交叉编译的 CBT 前置钩子）
HOOK="$ROOT/native/hook/Beautiful.Hook.dll"
[ -f "$HOOK" ] || { echo "ERROR: 缺 Beautiful.Hook.dll（先跑 scripts/build-hook-dll.sh）"; exit 1; }
cp "$HOOK" "$WORK/app/"

# 4) AnimLab 并入主包根目录：同名校验哈希一致才允许覆盖（防双发布版本漂移）
python3 - "$WORK/animlab" "$WORK/app" <<'PY'
import hashlib, os, shutil, sys
src, dst = sys.argv[1], sys.argv[2]
def h(p):
    return hashlib.sha256(open(p, 'rb').read()).hexdigest()
copied, skipped = 0, 0
names = []
for root, dirs, files in os.walk(src):
    rel = os.path.relpath(root, src)
    for name in files:
        s = os.path.join(root, name)
        d = os.path.join(dst, rel, name) if rel != '.' else os.path.join(dst, name)
        os.makedirs(os.path.dirname(d), exist_ok=True)
        if os.path.exists(d):
            if h(s) != h(d):
                raise SystemExit(f"ERROR: {rel}/{name} 在主包与 AnimLab 发布间哈希不一致（运行时版本漂移？）")
            skipped += 1
        else:
            shutil.copy2(s, d)
            names.append(rel if rel == '.' else rel + '/')
            copied += 1
print(f"AnimLab 并入：新增 {copied} 个文件（含 {', '.join(sorted(set(names))[:4])}...），同哈希跳过 {skipped} 个")
PY

# 4.5) 终验：AnimLab.exe / Beautiful.Hook.dll 在并入后的根目录里必须是合法 x64 PE
python3 - "$WORK/app" <<'PY'
import struct, sys, os
app = sys.argv[1]
def pe(path, want_dll=False):
    d = open(path, 'rb').read()
    assert d[:2] == b"MZ", f"{path}: MZ missing"
    off = struct.unpack("<I", d[0x3C:0x40])[0]
    assert d[off:off+4] == b"PE\0\0", f"{path}: PE sig missing"
    machine = struct.unpack("<H", d[off+4:off+6])[0]
    assert machine == 0x8664, f"{path}: not x64 ({machine:#x})"
    if want_dll:
        chars = struct.unpack("<H", d[off+22:off+24])[0]
        assert chars & 0x2000, f"{path}: not a DLL"
    return len(d)
pe(os.path.join(app, "Beautiful.exe"))
pe(os.path.join(app, "AnimLab.exe"))
hook_kb = pe(os.path.join(app, "Beautiful.Hook.dll"), want_dll=True) / 1024
coreclr = os.path.exists(os.path.join(app, "coreclr.dll"))
assert coreclr, "AnimLab 自包含失败：根目录缺 coreclr.dll"
total = sum(os.path.getsize(os.path.join(app, f)) for f in os.listdir(app))
print(f"终验 OK (AnimLab/Hook={hook_kb:.0f}KB 自包含齐) · {len(os.listdir(app))} files · {total/1e6:.0f} MB")
PY

# 5) 交付说明
cat > "$WORK/app/README-交付说明.txt" <<EOF
Beautiful $VER — Windows 桌面包
════════════════════════════════════════════

【内容】
  Beautiful.exe        主程序（Dock / 顶栏 / Finder / Launchpad / 控制中心 / 切换器）
  Beautiful.Hook.dll   CBT 前置钩子层（alpha.16 新增：真替换原生最小化动画；
                       纯静态链接零依赖，缺失时自动回退旧版事后补播路径）
  AnimLab.exe          动画调参台（本轮起自包含运行时，双击即可用）
  其余 .dll            .NET 8 self-contained 运行时 —— 无需安装任何运行库

【运行要求】
  Windows 10 1809 (17763) 或更高 · x64 · 解压即用

【首次运行】
  1. 将本目录解压到任意可写位置（建议非系统盘）
  2. 双击 Beautiful.exe（若 SmartScreen 提示，点"仍要运行"——本包未签名）
  3. 屏幕顶部出现"顶栏"、底部出现"Dock"即启动成功
  4. 退出：托盘图标右键 → 退出（或右键 Dock 上的"关于/设置"图标）

【alpha.16 本轮修复（对照测试清单逐项验收）】
  · 最小化动画真替换：自制 CBT 前置钩子层在原生动画开始前拦下并接管，
    标题栏按钮 / 任务栏 / Win+↓ / Dock 点击四条路径都不再有"原生缩放 + genie 打架"
  · 打开动画 Cloak 真窗口入场；关闭动画默认开
  · AnimLab 双击无窗口已修（自包含发布 + 崩溃日志 animlab-crash.log）
  · 顶栏托盘区图标化（exe 图标提取 + 系统矢量字形，替代文字占位）

【已知限制（alpha 阶段）】
  · Beautiful.Native.dll（MSVC 构建线：WGC 捕获 / DComp 材质）仍由 CI 独立构建，
    本包不含；缺失时自动降级纯 C# 形态，全部界面与动画照常工作
  · 管理员权限窗口受 UAC 隔离，钩子拦不到（原生动画照旧，属系统安全边界）
  · 窗口捕获对个别应用（UWP 部分/受保护内容）可能拿不到画面，genie 会如实跳过并写日志
EOF

# 5.5 测试清单（所有者验收用，直接进 zip —— alpha.15 起按所有者要求随包交付）
cat > "$WORK/app/测试清单.txt" <<'EOF'
════════════════════════════════════════════════════════
 Beautiful 4.0.0-alpha.16 验收测试清单（预计 ≤15 分钟）
 本轮主题：最小化动画真替换（CBT 前置钩子） / AnimLab 修复 / 托盘图标化
════════════════════════════════════════════════════════

【A · 启动】
  A1 双击 Beautiful.exe：顶栏 + Dock 出现（≤5 秒）
  A2 日志确认钩子层：logs\Beautiful-*.log 里搜「CBT 前置钩子已挂载」
     —— 在 = 本轮真替换生效；「不可用」= 回退旧路径（发日志给我）

【B · 最小化真替换（本轮最大改动 ★★★）】
  B1 打开记事本 → 点它标题栏的最小化按钮
      期望：窗口画面被 genie 吸进 Dock 图标，**全程看不到 Windows 原生
            "缩向任务栏"动画，也没有白光/闪烁**（此前是原生+genie 打架）
  B2 任务栏按钮点击最小化 → 同 B1 期望
  B3 Win+↓ 最小化 → 同 B1 期望
  B4 Dock 图标点击最小化（点正在前台应用的图标）→ 同 B1 期望
  B5 点 Dock 图标还原 → 从图标流出来长回原尺寸

【C · AnimLab（上轮双击无窗口 ★）】
  C1 双击 AnimLab.exe → 必须弹出窗口（左侧参数表 + 中部演示 + 右侧曲线）
     若仍无窗口：看本目录 animlab-crash.log，把它发给我
  C2 点"打开动画"演示 → 勾"慢速 0.3x"再看逐帧几何
  C3 把 OpenDurationMs 改 800 → 保存 → 演示明显变慢

【D · 打开 / 关闭动画（回归确认）】
  D1 新开任意窗口：Cloak 入场（替身以不透明帧占位，落位 ~360ms@90%，无白板裸窗）
  D2 关窗口：默认播关闭动画（~260ms 收缩淡出）

【E · 顶栏 / 托盘】
  E1 点时钟 → 通知中心滑入（回归上轮修复，不得再报错）
  E2 顶栏右侧托盘区：**显示图标而非文字**（exe 图标 + 系统矢量字形）
  E3 托盘右键 → 退出，进程干净退出

【F · Finder / Dock 常规】
  F1 Finder 打开、视图切换、QuickLook（空格预览）照常
  F2 Dock 悬停放大、右键菜单、Alt+Tab 切换器照常

【回填】通过打 [x]：A1__ A2__ B1__ B2__ B3__ B4__ B5__ C1__ C2__ C3__
        D1__ D2__ E1__ E2__ E3__ F1__ F2__
  异常项 + 现象（附 logs\ 最新日志更佳）：
  ____________________________________________
EOF

# 6) 打 zip
mkdir -p "$OUT_DIR"
rm -f "$ZIP"
(cd "$WORK/app" && zip -qr "$ZIP" .)
echo "── 完成：$ZIP ($(du -m "$ZIP" | cut -f1) MB)"
