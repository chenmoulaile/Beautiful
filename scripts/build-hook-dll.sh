#!/usr/bin/env bash
# ============================================================
# build-hook-dll.sh — Linux 交叉编译 Beautiful.Hook.dll（CBT 前置钩子层）
# 工具链：Debian clang-19（提供 MS SEH，-fms-extensions）
#         + mingw-w64 头/库/链接器（tools/mingw-prefix，见 setup-mingw.sh）
# 产物：native/hook/Beautiful.Hook.dll（纯 x64 PE DLL，零运行时依赖）
# 用法：bash scripts/build-hook-dll.sh
# ============================================================
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
P="/home/z/my-project/tools/mingw-prefix/usr"
CLANG="/home/z/my-project/tools/clang/usr/lib/llvm-19/bin/clang"
OUT="$ROOT/native/hook/Beautiful.Hook.dll"

export LD_LIBRARY_PATH="/home/z/my-project/tools/clang/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
export PATH="$P/bin:$PATH"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "── clang 编译（MS SEH）..."
"$CLANG" --target=x86_64-w64-mingw32 --sysroot="$P/x86_64-w64-mingw32" \
  -isystem "$P/share/mingw-w64/include" -fms-extensions \
  -c -o "$TMP/beautiful_hook.o" "$ROOT/native/hook/beautiful_hook.c" \
  -O2 -Wall -Wextra -Wno-unused-parameter

echo "── mingw gcc 链接（静态 CRT，零依赖）..."
x86_64-w64-mingw32-gcc -shared -o "$OUT" "$TMP/beautiful_hook.o" \
  -static -lkernel32 -luser32 -lgdi32

echo "── 产物校验..."
python3 - "$OUT" <<'PY'
import struct, sys
d = open(sys.argv[1], "rb").read()
assert d[:2] == b"MZ", "MZ missing"
off = struct.unpack("<I", d[0x3C:0x40])[0]
assert d[off:off+4] == b"PE\0\0", "PE sig missing"
machine = struct.unpack("<H", d[off+4:off+6])[0]
assert machine == 0x8664, f"not x64: {machine:#x}"
chars = struct.unpack("<H", d[off+22:off+24])[0]
assert chars & 0x2000, "not a DLL (no IMAGE_FILE_DLL bit)"
print(f"PE OK (x64 DLL) · {len(d)/1024:.0f} KB")
PY
ls -la "$OUT"
echo "Beautiful.Hook.dll 构建完成"
