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
  Beautiful.Hook.dll   前置钩子层 v2（alpha.17：最小化/打开/关闭 三路真替换原生动画；
                       纯静态链接零依赖，缺失时自动回退旧版事后补播路径）
  AnimLab.exe          动画调参台（自包含运行时，双击即可用）
  其余 .dll            .NET 8 self-contained 运行时 —— 无需安装任何运行库

【运行要求】
  Windows 10 1809 (17763) 或更高 · x64 · 解压即用

【首次运行】
  1. 将本目录解压到任意可写位置（建议非系统盘）
  2. 双击 Beautiful.exe（若 SmartScreen 提示，点"仍要运行"——本包未签名）
  3. 屏幕顶部出现"顶栏"、底部出现"Dock"即启动成功
  4. 退出：托盘图标右键 → 退出（或右键 Dock 上的"关于/设置"图标）

【alpha.17 本轮修复（对照测试清单逐项验收）】
  · 打开动画真替换：钩子在目标窗口【首帧上屏之前】就地 Cloak，替身从 90% 落位，
    原生"突然出现"被整个吃掉（此前是先看到原生出现再叠一层替身）
  · 关闭动画真替换：钩子在窗口销毁前抓下最后一帧，替身收拢消失（设置里开启后生效）
  · 通知中心加固：初始化/重画/卡片逐段兑底，单张卡片异常不再拖垮面板
  · 设置 → 动画：新增【诊断状态行 + 复制诊断信息 + 打开日志文件夹】，
    报障时点一下"复制诊断信息"把结果发回来即可

【已知限制（alpha 阶段）】
  · Beautiful.Native.dll（MSVC 构建线：WGC 捕获 / DComp 材质）仍由 CI 独立构建，
    本包不含；缺失时自动降级纯 C# 形态，全部界面与动画照常工作
  · 管理员权限窗口受 UAC 隔离，钩子拦不到（原生动画照旧，属系统安全边界）
  · 窗口捕获对个别应用（UWP 部分/受保护内容）可能拿不到画面，genie 会如实跳过并写日志
EOF

# 5.5 测试清单（所有者验收用，直接进 zip —— alpha.15 起按所有者要求随包交付）
cat > "$WORK/app/测试清单.txt" <<EOF
════════════════════════════════════════════════════════
 Beautiful $VER 验收测试清单（预计 ≤15 分钟）
 本轮主题：打开/关闭动画真替换（钩子层 v2） / 通知中心加固 / 一键诊断
════════════════════════════════════════════════════════

【A · 启动与自检】
  A1 双击 Beautiful.exe：顶栏 + Dock 出现（≤5 秒）
  A2 设置 → 动画 → 看【动画诊断】行：钩子层应为「CBT 前置钩子已挂载（v2…三路真替换）」
     点【复制诊断信息】粘到验收回执里（这是本轮新增的取证方式，比翻日志快）

【B · 打开动画真替换（本轮最大改动 ★★★）】
  B1 从开始菜单/桌面双击打开记事本（或任意新窗口）
      期望：**看不到窗口"突然蹦出"**——替身以不透明画面从 90% 平滑落位到 100%，
      全程无白板、无先出现后重叠的两段动画
  B2 连续快速开 3 个窗口：都应有动画（1 秒内同进程第 4 个起会"风暴抑制"不播，属正常）
  B3 关闭再重开同一应用：动画一致

【C · 关闭动画真替换（本轮新增 ★★）】
  C1 设置 → 动画 → 勾选【关闭窗口动画】（macOS 默认不播，先手动开）
  C2 关闭记事本窗口 → 期望：窗口消失瞬间，替身画面原地收缩淡出（~260ms）
      （原生关闭本身没有动画，本动画是"销毁前最后一帧"的忠实重演）
  C3 测完可把开关关回默认

【D · 最小化真替换（上轮回归确认 ★）】
  D1 记事本点标题栏最小化 → genie 吸进 Dock 图标，无原生缩放/无白光
  D2 任务栏按钮 / Win+↓ / Dock 点击最小化 → 同上
  D3 点 Dock 图标还原 → 从图标流出来长回原尺寸

【E · 通知中心（上轮报"又炸了" ★★）】
  E1 点顶栏时钟 → 面板滑入，**不得出现任何报错弹框**
  E2 面板内：有通知时卡片正常渲染；无通知时显示"没有新通知/无法读取通知+原因"
  E3 若仍有报错：点【复制诊断信息】+【打开日志文件夹】把最新日志发回

【F · 常规回归】
  F1 AnimLab 双击有窗口、调参生效
  F2 顶栏托盘区显示图标而非文字
  F3 Finder 开合、Dock 悬停放大、Alt+Tab 切换器照常

【回填】通过打 [x]：A1__ A2__ B1__ B2__ B3__ C1__ C2__ C3__
        D1__ D2__ D3__ E1__ E2__ E3__ F1__ F2__ F3__
  异常项 + 现象（用【复制诊断信息】贴进来更佳）：
  ____________________________________________
EOF

# 6) 打 zip
mkdir -p "$OUT_DIR"
rm -f "$ZIP"
(cd "$WORK/app" && zip -qr "$ZIP" .)
echo "── 完成：$ZIP ($(du -m "$ZIP" | cut -f1) MB)"
