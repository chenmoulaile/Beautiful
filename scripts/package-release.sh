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
# dotnet 探测链：DOTNET_ROOT → 本机沙盒安装点 → PATH（CI）。全空即报错，绝不静默。
DOTNET="${DOTNET_ROOT:-}/dotnet"
[ -x "$DOTNET" ] || DOTNET="/home/z/my-project/tools/dotnet/dotnet"
[ -x "$DOTNET" ] || DOTNET="$(command -v dotnet || true)"
if [ -z "$DOTNET" ] || [ ! -x "$DOTNET" ]; then
  echo "ERROR: 找不到 dotnet SDK（请设 DOTNET_ROOT 或将 dotnet 加入 PATH）"
  exit 1
fi
echo "── dotnet：$DOTNET"

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
  Beautiful.Hook.dll   前置钩子层 v2（alpha.18：最小化拦截不再被"纯白窗口"瓦解；
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

【alpha.18 本轮修复（对照 测试清单.md 逐项验收）】
  · 最小化真替换根因修复：空记事本这类纯白窗口整帧同色，被旧版"纯色判废"
    当成抓帧失败直接放行——这就是"原生 Windows 最小化动画还在"的根因。
    现在：纯白/纯色照常抓帧照常 genie；万一仍抓帧失败，钩子照样拦截原生动画
    （空帧信令），主程序用最近缓存帧播 genie 或静默落位，原生动画不再有出场机会
  · 通知中心滑入重做：旧版只平移 20px（肉眼不可见，像卡住）；现在整个面板
    从屏幕右缘外推入（320ms），Esc/点面板外/二次点击对称滑出（220ms）
  · AnimLab 加固：启动异常完整记录内部异常链（animlab-crash.log），
    弹窗直接显示真实原因；初始化分段降级，任何一段失败窗口照常出现
  · 访达还原度：列表/图标/侧栏/分栏滚动全部换成 macOS 式滑行（目标累积 +
    easeOutCubic 220ms）；目录内容就位改为淡入+上滑过渡，不再硬切

【已知限制（alpha 阶段）】
  · Beautiful.Native.dll（MSVC 构建线：WGC 捕获 / DComp 材质）仍由 CI 独立构建，
    本包不含；缺失时自动降级纯 C# 形态，全部界面与动画照常工作
  · 管理员权限窗口受 UAC 隔离，钩子拦不到（原生动画照旧，属系统安全边界）
  · 窗口捕获对个别应用（UWP 部分/受保护内容）可能拿不到画面，genie 会用缓存帧
    或静默落位，原生动画不会出现
EOF

# 5.5 测试清单（所有者验收用，直接进 zip；alpha.18 起改 md 格式——所有者要求直接勾选）
cat > "$WORK/app/测试清单.md" <<EOF
# Beautiful $VER 验收测试清单

> 勾选方式：把 \`[ ]\` 改成 \`[x]\`，有问题直接写在该项下面。
> 预计 ≤12 分钟，改完把这份文件发回来即可完成验收。

## A · 启动（30 秒）

- [ ] A1 双击 Beautiful.exe：顶栏 + Dock 出现（≤5 秒）
- [ ] A2 设置 → 动画 →【动画诊断】行 = 「CBT 前置钩子已挂载（v2…）」

## B · 最小化真替换（本轮修复根因 ★★★）

上轮根因已查明：空记事本这类**纯白窗口**整帧同色，被旧版误判为"抓帧失败"
直接放行——所以你看到原生 Windows 最小化动画照旧。本轮已修。

- [ ] B1 开一个**空白记事本**（不打字）→ 点标题栏最小化：
      期望 genie 吸进 Dock 图标，**原生缩小动画不再出现**
- [ ] B2 打几个字让窗口有内容再最小化：同样 genie
- [ ] B3 任务栏按钮 / Win+↓ 最小化：同上
- [ ] B4 点 Dock 图标还原：从图标流出来长回原尺寸

## C · 通知中心（本轮重做滑入 ★★）

- [ ] C1 点顶栏时钟：面板**从屏幕右缘外整体推入**（约 0.3 秒），不再"卡在那里"
- [ ] C2 Esc / 点面板外 / 再点时钟：面板对称**滑出**（不是硬切消失）
- [ ] C3 无报错弹框；卡片正常渲染（无通知时显示占位文案）

## D · AnimLab（本轮加固 ★★）

- [ ] D1 双击 AnimLab.exe：有窗口。若仍报错，弹窗会显示**真实原因**，
      animlab-crash.log 里是完整异常链——直接把文件发回来即可定位
- [ ] D2 点"打开动画 / 关闭动画 / Genie 条带"按钮：舞台正常逐帧播放

## E · 打开 / 关闭动画（回归确认）

- [ ] E1 打开新窗口：替身从 90% 平滑落位（上轮已 OK，确认没退化）
- [ ] E2 设置勾选【关闭窗口动画】→ 关闭窗口：替身收缩淡出

## F · 访达还原度（本轮增强 ★）

- [ ] F1 列表/图标视图滚滚轮：**平滑滑行、停手减速**，不再一格一格跳
- [ ] F2 分栏视图滚滚轮：列横向平滑滑动
- [ ] F3 切换目录：内容淡入轻浮起，不再硬切

## G · 常规回归

- [ ] G1 Finder 开合、Dock 悬停放大、Alt+Tab 切换器照常
- [ ] G2 顶栏托盘区显示图标而非文字（上轮遗留项，观察即可）

---

**异常记录**（现象 + 复现步骤，能贴【复制诊断信息】更佳）：

＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿＿
EOF

# 6) 打 zip
mkdir -p "$OUT_DIR"
rm -f "$ZIP"
(cd "$WORK/app" && zip -qr "$ZIP" .)
echo "── 完成：$ZIP ($(du -m "$ZIP" | cut -f1) MB)"
