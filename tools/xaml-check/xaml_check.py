#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
xaml_check.py — WPF 工程 XAML ↔ 代码一致性静态扫描（深度检查的第一道网）
检查项：
  1. x:Class 与 .xaml.cs 的 namespace/class 对齐
  2. XAML 里的事件处理器（Click/Checked/...="Name"）在对应 .xaml.cs 中存在
  3. StaticResource{Key} 的键在任一 xaml 中可解析（缺失=运行时 XamlParseException）
  4. 危险模式扫描：.Result / .Wait() / GetAwaiter().GetResult()
  5. Window 子类里直接给 RenderTransform 赋值（WPF Window 不支持，运行时必抛）
用法：python tools/xaml-check/xaml_check.py <repo-root>
退出码：发现 error 级问题 → 1（可进 CI 门禁）
【主线适配（wp-borrow 收口）】扫描前剥离 XML 注释——XAML 注释里常写
`<Path Style="{StaticResource CcIconX}"/>` 这类用法示例（ControlCenterPanel.xaml:111），
不剥会把注释当真实引用，误报断链（alpha.15 原版在主线的 1 个 error 即此根因）。
"""
import re, sys, os, glob

ROOT = sys.argv[1] if len(sys.argv) > 1 else "."
APP = os.path.join(ROOT, "src", "Beautiful.App")
errors, warns = [], []

defined_keys = set()
key_def_re = re.compile(r'x:Key="([^"]+)"')
xml_comment_re = re.compile(r'<!--.*?-->', re.S)

def load_text(path):
    try:
        return open(path, encoding="utf-8-sig").read()
    except Exception:
        return None

def collect_keys(path):
    text = load_text(path)
    if text is None:
        return
    defined_keys.update(key_def_re.findall(xml_comment_re.sub("", text)))

xamls = glob.glob(os.path.join(APP, "**", "*.xaml"), recursive=True)
for p in xamls:
    collect_keys(p)

event_attr_re = re.compile(r'\b(Click|Checked|Unchecked|ValueChanged|SelectionChanged|TextChanged|'
                           r'MouseLeftButtonUp|MouseRightButtonUp|MouseDown|MouseUp|Loaded|KeyDown|KeyUp|'
                           r'Drop|DragEnter|MouseMove|PreviewMouseDown|PreviewKeyDown)="([A-Za-z_]\w*)"')
class_attr_re = re.compile(r'\bx:Class="([^"]+)"')
static_res_re = re.compile(r'\{StaticResource\s+([A-Za-z_]\w*)\}')

# 分部类处理器可能落在任意 partial 文件（FinderWindow.Icons.cs / LaunchpadWindow.Folders.cs…）
all_cs_texts = {}
for c in glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True):
    text = load_text(c)
    if text is not None:
        all_cs_texts[c] = text

for x in sorted(xamls):
    cs = x[:-5] + ".xaml.cs"
    text = load_text(x)
    if text is None:
        continue
    text = xml_comment_re.sub("", text)  # 注释里的示例代码不当真实引用
    m = class_attr_re.search(text)
    if not m or not os.path.exists(cs):
        continue  # 资源字典/无代码侧文件
    fullcls = m.group(1)
    ns, cls = fullcls.rsplit(".", 1)
    ctext = all_cs_texts.get(cs, "")
    if f"namespace {ns};" not in ctext and f"namespace {ns}\n" not in ctext:
        errors.append(f"[xclass] {os.path.relpath(cs, ROOT)}: namespace 与 x:Class '{fullcls}' 不对齐")
    if not re.search(r'partial\s+class\s+' + re.escape(cls) + r'\b', ctext):
        errors.append(f"[xclass] {os.path.relpath(cs, ROOT)}: 找不到 partial class {cls}")

    for attr, handler in event_attr_re.findall(text):
        pat = re.compile(r'\b' + re.escape(handler) + r'\s*\(')
        if not any(pat.search(t) for t in all_cs_texts.values()):
            errors.append(f"[handler] {os.path.relpath(x, ROOT)}: {attr}='{handler}' 在任何 .cs 中都不存在")

    for key in static_res_re.findall(text):
        if key not in defined_keys:
            errors.append(f"[staticres] {os.path.relpath(x, ROOT)}: StaticResource '{key}' 在任何 xaml 中都未定义")

danger_patterns = [
    (r'\.Result\b', "[danger] .Result 可能死锁（UI 线程同步等待异步）"),
    (r'\.Wait\(\)', "[danger] .Wait() 可能死锁"),
    (r'GetAwaiter\(\)\.GetResult\(\)', "[danger] 同步阻塞异步"),
]
# Window 子类禁止 RenderTransform 赋值（WPF Window 不支持，赋值即抛
# "Transform is not valid for Window"——v0.16 真机实锤，控件/元素不受限）
for x in xamls:
    cs = x[:-5] + ".xaml.cs"
    if not os.path.exists(cs):
        continue
    ctext = all_cs_texts.get(cs)
    if ctext is None:
        continue
    if re.search(r'partial\s+class\s+\w+\s*:\s*Window\b', ctext) and re.search(r'(?<![\w.])(this\.)?RenderTransform\s*=[^=]', ctext):
        errors.append(f"[window-transform] {os.path.relpath(cs, ROOT)}: Window 子类中直接给 RenderTransform 赋值（运行时必抛）")
allcs = glob.glob(os.path.join(ROOT, "src", "**", "*.cs"), recursive=True)
for cs in allcs:
    text = load_text(cs)
    if text is None:
        continue
    rel = os.path.relpath(cs, ROOT)
    for pat, msg in danger_patterns:
        for mm in re.finditer(pat, text):
            line = text[:mm.start()].count("\n") + 1
            ctx = text.splitlines()[line - 1].strip()
            warns.append(f"{msg} — {rel}:{line}: {ctx[:100]}")

print(f"扫描 {len(xamls)} 个 xaml / {len(allcs)} 个 cs / 资源键 {len(defined_keys)} 个")
for e in errors: print("ERROR", e)
for w in warns: print("WARN ", w)
print(f"\n== {len(errors)} errors / {len(warns)} warnings ==")
sys.exit(1 if errors else 0)
