# scripts/diag.ps1 —— 一次性诊断块（把输出整段复制回来即可排障）
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\diag.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\diag.ps1 -Path E:\Beautiful\dist\Beautiful\logs\Beautiful-202610.log
#
# 做的事：自动找**最新**的应用日志（dist\Beautiful\logs\、publish\logs\、src\Beautiful.App\bin\**\logs\ 都算），
# 从里面挑出 v3 各项修复对应的行（顶栏几何 / Dock 几何 / AppBar / 背板 / 图标提取 / 启动器 / 掉帧 / 策略），
# 再附上"看懂这些数字需要的机器事实"：每一块显示器的 rcMonitor + 有效 DPI、主屏标志、光标所在屏，
# 以及当前 config\settings.json 的主题/图标尺寸/任务栏策略/显示器选择器。
#
# 兼容 Windows PowerShell 5.1：不用 &&、不用三元、不用 PS7 专有 cmdlet；文件必须带 UTF-8 BOM。
# 本脚本只读，不改任何设置、不写 C 盘。

param(
    [string]$Path = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# ---------- 0. 让本进程变成 Per-Monitor v2 DPI 感知 ----------
# 必须在任何显示器查询之前调用：DPI 感知是进程级且不可逆的属性。不开这一句，
# EnumDisplayMonitors/GetMonitorInfo 与 GetDpiForMonitor 返回的是**系统虚拟化**后的
# 数字（200% 屏上给 1440x960 @96 DPI），而应用日志里的 rcMonitor 是**物理**像素
# （同一块屏给 2880x1920 / dpi=2）—— 本节的全部用途就是"两边一对就能定位"，
# 口径不一致时它反而会把人带偏（源开发机是 100% 缩放，两套数字恰好相等，所以从没暴露）。
try {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DiagDpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4；已设置过再调用只会返回 false，不是错误
  public static string Apply() {
    try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return "PerMonitorV2"; } catch { }
    try { if (SetProcessDPIAware()) return "SystemAware(回退)"; } catch { }
    return "未改变（可能已由宿主进程设置）";
  }
}
'@
    $dpiMode = [DiagDpi]::Apply()
} catch {
    $dpiMode = "设置失败（将继续用进程原有感知，数字可能与日志不同口径）"
}

# ---------- 1. 定位日志 ----------
# Logger 写在 AppContext.BaseDirectory\logs\Beautiful-yyyyMM.log，而 BaseDirectory 随运行入口而变：
# 自包含产物在 dist\Beautiful\（或 publish\），开发构建在 src\Beautiful.App\bin\<cfg>\...。
$candidates = @()
$fixedDirs = @(
    (Join-Path $root 'dist\Beautiful\logs'),
    (Join-Path $root 'publish\logs')
)
foreach ($d in $fixedDirs) {
    if (Test-Path $d) { $candidates += @(Get-ChildItem -Path $d -Filter 'Beautiful-*.log' -ErrorAction SilentlyContinue) }
}
$binRoot = Join-Path $root 'src\Beautiful.App\bin'
if (Test-Path $binRoot) {
    $candidates += @(Get-ChildItem -Path $binRoot -Recurse -Filter 'Beautiful-*.log' -ErrorAction SilentlyContinue)
}

$log = $null
if ($Path -ne '') {
    if (-not (Test-Path $Path)) { Write-Output "LOG_NOT_FOUND $Path"; exit 1 }
    $log = Get-Item -Path $Path
} else {
    if ($candidates.Count -eq 0) { Write-Output 'NO_LOG_FOUND：应用还没运行过（或日志目录不在上述位置），请用 -Path 指定日志文件'; exit 1 }
    $log = @($candidates | Sort-Object -Property LastWriteTime -Descending)[0]
}

$lines = @(Get-Content -Path $log.FullName -Encoding UTF8)
$appRoot = Split-Path -Parent $log.Directory.FullName   # logs\ 的上一级 = 运行入口目录
$exe = Join-Path $appRoot 'Beautiful.exe'
$exeInfo = '（未找到）'
if (Test-Path $exe) { $exeInfo = ((Get-Item $exe).Length.ToString() + ' 字节') }

Write-Output '================ Beautiful v3 诊断块 ================'
Write-Output ("日志文件 : " + $log.FullName)
Write-Output ("最后写入 : " + $log.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss') + "    总行数: " + $lines.Count)
Write-Output ("运行入口 : " + $appRoot + "    Beautiful.exe: " + $exeInfo)
Write-Output ("系统     : " + [Environment]::OSVersion.VersionString + "    CLR " + [Environment]::Version + "    64位进程 " + [Environment]::Is64BitProcess)

# ---------- 2. 机器事实：显示器 rcMonitor + 有效 DPI ----------
if (-not ('W32Diag' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class W32Diag
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    // MONITORINFOEXW：必须用 struct + ref（class 传参时 cbSize 的封送在 PS5.1 的 CodeDom 编译下拿不到矩形）
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    // shcore：与 src/Beautiful.App/Services/MonitorGeometry.cs 用的是同一个入口、同一个 MDT_EFFECTIVE_DPI(0)
    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, int flags);   // MDM_NEAREST = 2

    private static readonly MonitorEnumProc Proc = Collect;
    private static readonly List<IntPtr> Handles = new List<IntPtr>();
    private static readonly List<RECT> ClipRects = new List<RECT>();

    private static bool Collect(IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data)
    {
        Handles.Add(hMonitor);
        ClipRects.Add(clip);
        return true;
    }

    public static string[] Report()
    {
        List<IntPtr> snapshot;
        List<RECT> enumRects;
        Handles.Clear();
        ClipRects.Clear();
        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Proc, IntPtr.Zero); }
        catch (Exception ex) { return new string[] { "  枚举显示器失败: " + ex.GetType().Name + " " + ex.Message }; }
        snapshot = new List<IntPtr>(Handles);
        enumRects = new List<RECT>(ClipRects);
        GC.KeepAlive(Proc);

        POINT cursor;
        IntPtr cursorMonitor = IntPtr.Zero;
        try { if (GetCursorPos(out cursor)) cursorMonitor = MonitorFromPoint(cursor, 2); }
        catch { }

        var outLines = new List<string>();
        outLines.Add("  在屏显示器数: " + snapshot.Count + "    光标所在显示器句柄: 0x" + cursorMonitor.ToInt64().ToString("X"));
        for (int i = 0; i < snapshot.Count; i++)
        {
            var h = snapshot[i];
            var info = new MONITORINFOEX();
            info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (!GetMonitorInfo(h, ref info))
            {
                string fallback = (i < enumRects.Count)
                    ? "（GetMonitorInfo 失败，退到 EnumDisplayMonitors 回调矩形）"
                    : "（GetMonitorInfo 失败且无回调矩形）";
                outLines.Add("  显示器 " + (i + 1) + "  句柄 0x" + h.ToInt64().ToString("X") + " 取不到 rcMonitor " + fallback);
                continue;
            }
            var r = info.rcMonitor;
            var w = info.rcWork;
            string dpiText;
            try
            {
                uint x, y;
                if (GetDpiForMonitor(h, 0, out x, out y) == 0 && x > 0)
                    dpiText = x.ToString() + " DPI（" + Math.Round(x / 96.0 * 100.0) + "%）";
                else
                    dpiText = "GetDpiForMonitor 返回失败";
            }
            catch (Exception ex) { dpiText = "shcore 不可用: " + ex.GetType().Name; }

            bool primary = (info.dwFlags & 1) != 0;
            bool hasCursor = (h == cursorMonitor);
            outLines.Add("  显示器 " + (i + 1) + "｜0x" + h.ToInt64().ToString("X") + "｜" + info.szDevice +
                "｜rcMonitor=(" + r.Left + "," + r.Top + ")-(" + r.Right + "," + r.Bottom + ")" +
                " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
                "｜rcWork=(" + w.Left + "," + w.Top + ")-(" + w.Right + "," + w.Bottom + ")" +
                "｜" + dpiText +
                (primary ? "｜主屏" : "") + (hasCursor ? "｜光标在此" : ""));
        }
        return outLines.ToArray();
    }
}
'@
}

Write-Output ''
Write-Output '[显示器与 DPI（几何数字的解释基准）]'
Write-Output "  本进程 DPI 感知: $dpiMode    口径: 下列 rcMonitor 与日志里的 rc 同为物理像素"
foreach ($l in [W32Diag]::Report()) { Write-Output $l }

# ---------- 3. 当前设置 ----------
Write-Output ''
Write-Output '[当前 config\settings.json]'
$cfgPath = Join-Path $appRoot 'config\settings.json'
if (Test-Path $cfgPath) {
    $cfg = Get-Content -Path $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $keys = @('Theme', 'DockIconSize', 'TaskbarPolicy', 'DockMonitor', 'TopBarMonitor', 'DockPosition',
              'MagnificationStrength', 'UseWallpaperAccent', 'TopBarEnabled', 'LauncherEnabled', 'SwitcherEnabled')
    foreach ($k in $keys) {
        $v = $cfg.PSObject.Properties[$k]
        if ($v -ne $null) { Write-Output ("  " + $k + ' = ' + $v.Value) }
        else { Write-Output ("  " + $k + ' = (未设置，用代码默认)') }
    }
} else {
    Write-Output ('  (没有 ' + $cfgPath + '，全部走代码默认：Theme=harmonyos / DockIconSize=48 / TaskbarPolicy=hide / DockMonitor=auto / TopBarMonitor=auto)')
}

# ---------- 4. 日志里挑出 v3 的判据行 ----------
function Show-Section {
    param([string]$Label, [string]$Pattern, [int]$Take, [string]$Empty)
    Write-Output ''
    Write-Output ("[$Label]")
    $hits = @($lines | Where-Object { $_ -match $Pattern })
    if ($hits.Count -eq 0) {
        Write-Output ('  ' + $Empty)
        return
    }
    $tail = @($hits | Select-Object -Last $Take)
    foreach ($h in $tail) { Write-Output ('  ' + $h) }
    if ($hits.Count -gt $Take) {
        Write-Output ('  (本节共 ' + $hits.Count + ' 行，只显示最后 ' + $Take + ' 行)')
    }
}

Show-Section -Label '顶栏几何（贴屏顶？）' `
    -Pattern '顶栏几何|顶栏 条带落位' -Take 6 `
    -Empty '(无顶栏几何行——顶栏未启用或未落位)'

Show-Section -Label 'Dock 几何（贴真底？）' `
    -Pattern 'Dock 几何|Dock 工作区|Dock 就绪' -Take 6 `
    -Empty '(无 Dock 几何行——Dock 未落位)'

Show-Section -Label 'AppBar（两条带的注册与协商）' `
    -Pattern 'AppBar|ABM_|ABN_' -Take 8 `
    -Empty '(无 AppBar 行)'

Show-Section -Label '背板（抓屏模糊 / 实心降级）' `
    -Pattern '背板' -Take 8 `
    -Empty '(无背板行——弹层未被打开过，或 BEAUTIFUL_NOBLUR=1)'

Show-Section -Label '图标提取与烘焙' `
    -Pattern '图标提取|图标烘焙' -Take 8 `
    -Empty '(无图标提取行——固定应用列表可能为空)'

Show-Section -Label '启动器索引条数' `
    -Pattern '启动器：索引就绪|启动器：开始菜单扫描完成|启动器：MSIX 枚举|启动器已打开|启动器：索引构建失败|启动器：热键' -Take 8 `
    -Empty '(无启动器行——启动器未装配或索引线程未跑)'

Show-Section -Label '策略（原生任务栏）' `
    -Pattern '任务栏策略|ReclaimBottomStrip|EnableAutoHide|DisableAutoHide|HideAll|Restore\(' -Take 8 `
    -Empty '(无策略行)'

# Native 模块（v4 Phase 2）：DLL 存在性 + native.log 最近 20 行（含探针/降级原因）
Write-Output ''
Write-Output '[Native 模块]'
Show-Section -Label 'Native 层（主日志）' `
    -Pattern 'Native 层|native 材质|WGC|TrayBridge' -Take 12 `
    -Empty '(无 native 主日志行——native 未加载或本版未启用)'
$appDir = Split-Path -Parent $log.Directory
foreach ($name in @('Beautiful.Native.dll', 'Beautiful.TrayBridge.dll')) {
    $p = Join-Path $appDir $name
    if (Test-Path $p) {
        $f = Get-Item $p
        Write-Output ('  ' + $name + ' 存在 ' + $f.Length + ' B  ' + $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
    } else {
        Write-Output ('  ' + $name + ' 缺失（纯 C# 形态）')
    }
}
$nativeLog = Join-Path $log.Directory 'native.log'
if (Test-Path $nativeLog) {
    Write-Output '  native.log 最近 20 行：'
    foreach ($h in @(Get-Content -Path $nativeLog -Encoding UTF8 -Tail 20)) { Write-Output ('    ' + $h) }
} else {
    Write-Output '  (无 native.log——native 层未加载或未产生日志)'
}

# 掉帧：帧间隔分布（口径与 scripts/frametime.ps1 一致：MAX 只含 0<Δ<500ms，RAW_MAX 含全部正间隔）
Write-Output ''
Write-Output '[掉帧 / 帧间隔统计（BEAUTIFUL_FRAME）]'
$ticks = @()
foreach ($l in $lines) {
    if ($l -match 'BEAUTIFUL_FRAME (\d+)') { $ticks += [double]$Matches[1] }
}
if ($ticks.Count -lt 5) {
    Write-Output ('  帧日志 ' + $ticks.Count + ' 条，不足 5 条，无法统计。要测帧间隔请设环境变量 BEAUTIFUL_FRAMETIME=1 再启动 Beautiful，并把鼠标扫过 Dock。')
} else {
    $freq = [System.Diagnostics.Stopwatch]::Frequency
    $deltas = @(); $rawMax = 0.0; $dropped = 0
    for ($i = 1; $i -lt $ticks.Count; $i++) {
        $d = ($ticks[$i] - $ticks[$i - 1]) * 1000.0 / $freq
        if ($d -le 0) { $dropped++; continue }
        if ($d -gt $rawMax) { $rawMax = $d }
        if ($d -ge 500) { $dropped++; continue }
        $deltas += $d
    }
    if ($deltas.Count -lt 2) {
        Write-Output '  有效帧间隔样本不足（全部尖峰被排除），请重新采样。'
    } else {
        $s = @($deltas | Sort-Object)
        $avg = ($deltas | Measure-Object -Average).Average
        $p50 = $s[[int]($s.Count * 0.50)]
        $p95 = $s[[int]($s.Count * 0.95)]
        Write-Output ('  FRAMES=' + $s.Count + '  AVG_MS=' + ('{0:N2}' -f $avg) + '  P50=' + ('{0:N2}' -f $p50) +
                      '  P95=' + ('{0:N2}' -f $p95) + '  MAX=' + ('{0:N2}' -f $s[-1]) +
                      '  RAW_MAX=' + ('{0:N2}' -f $rawMax) + 'ms  DROPPED=' + $dropped)
        Write-Output '  判据：P95 < 20ms 且 MAX < 34ms 视为丝滑（60fps vsync）；MAX > 50ms 视为可感知卡顿。'
    }
}

# 异常尾部：WARN/ERROR 是最能说明"为什么长这样"的一行
Write-Output ''
Write-Output '[最近 WARN / ERROR]'
$bad = @($lines | Where-Object { $_ -match '\[(WARN|ERROR)\]' })
if ($bad.Count -eq 0) {
    Write-Output '  (无告警与错误)'
} else {
    foreach ($h in @($bad | Select-Object -Last 10)) { Write-Output ('  ' + $h) }
    if ($bad.Count -gt 10) { Write-Output ('  (共 ' + $bad.Count + ' 行，只显示最后 10 行)') }
}

Write-Output ''
Write-Output '================ 诊断块结束 ================'
Write-Output '把上面这一整段原样复制粘贴回来即可，我按这些数字定位是哪一块屏、哪一步几何出了问题。'
