# 采样 Beautiful Dock 的动画帧间隔，客观判定放大动画是否丝滑。
# 原理：Beautiful 在 CompositionTarget.Rendering 循环里每帧写一行日志（BEAUTIFUL_FRAME <tick>）。
#      本脚本启动 app、用鼠标横扫 Dock 触发放大动画，再从日志里解析帧间隔分布。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/frametime.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin  = Join-Path $root 'src\Beautiful.App\bin\Debug\net8.0-windows10.0.19041.0'
$exe  = Join-Path $bin 'Beautiful.exe'
if (-not (Test-Path $exe)) { Write-Output "EXE_NOT_FOUND: 先跑 dotnet build"; exit 1 }

# ---------- 0a. 本进程切到 Per-Monitor v2 DPI 感知 ----------
# 必须在任何显示器查询之前：日志里的 AppBar rc 是**物理**像素，而 DPI-unaware 进程的
# SetCursorPos / Screen.Bounds 走**虚拟化**坐标。开发机是 100% 缩放所以两套数字恰好相等；
# 200% 真机上旧版把物理 y 喂给虚拟化的 SetCursorPos，越界被夹到屏幕最底行、落在图标行
# 命中区之外 —— 于是只采到个位数帧，却报"埋点不存在"，把脚本自己的错诊断成应用的缺失。
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FtDpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public static string Apply() {
    try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return "PerMonitorV2"; } catch { }
    try { if (SetProcessDPIAware()) return "SystemAware(回退)"; } catch { }
    return "未改变";
  }
}
'@
Write-Output ("DPI_MODE=" + [FtDpi]::Apply() + "（本脚本全程按物理像素驱动光标）")

# ---------- 0b. 先停掉正在运行的 Beautiful ----------
# 应用有单实例互斥：不先停，下面 Start-Process 起来的测量实例会立刻自己退出
# （日志里只有 "Beautiful 已在运行，第二实例退出"），而旧版把这静默成"没有帧日志"。
$prev = @(Get-Process -Name Beautiful -ErrorAction SilentlyContinue)
$wasRunning = $prev.Count -gt 0
$prevPath = $null
if ($wasRunning) {
  # 记下用户实际跑的是哪一件产物（dist 自包含 / Debug 构建），测完按原样起回去
  $prevPath = $prev[0].Path
  Stop-Process -Name Beautiful -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 1500
  Write-Output "PRE_STOP=1 已停掉正在运行的实例（单实例互斥），测量结束后拉起：$prevPath"
}

# 保证任务栏可见，便于把光标移到 Dock 上（hide 策略下 Dock 位置一致，仅影响托盘可达性）
# 另外强制 macOS 主题：只有 macOS 主题 MaxScale>1，放大动画才会真正跑起来（鸿蒙/Linux 不放大 → 无帧日志）
# 注意：这两项只是本次测量的临时改写，脚本结束时**必须原样还原** settings.json（评审发现 #5：
# 旧版会永久改掉用户的 TaskbarPolicy/Theme）。
$cfgDir = Join-Path $bin 'config'
New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
$cfgPath = Join-Path $cfgDir 'settings.json'
$hadCfg = Test-Path $cfgPath
$cfgOriginal = if ($hadCfg) { Get-Content $cfgPath -Raw -Encoding UTF8 } else { $null }
$cfg = if ($cfgOriginal) { $cfgOriginal } else { '{}' }
$cfg = $cfg -replace '"TaskbarPolicy"\s*:\s*"[^"]*"', '"TaskbarPolicy":"keep"'
if ($cfg -notmatch '"TaskbarPolicy"') { $cfg = $cfg.TrimEnd('}') + ',"TaskbarPolicy":"keep"}' }
$cfg = $cfg -replace '"Theme"\s*:\s*"[^"]*"', '"Theme": "macos"'
if ($cfg -notmatch '"Theme"') { $cfg = $cfg.TrimEnd('}') + ',"Theme":"macos"}' }
Set-Content -Path $cfgPath -Value $cfg -NoNewline -Encoding UTF8

function Restore-Settings {
  if ($cfgOriginal) {
    Set-Content -Path $cfgPath -Value $cfgOriginal -NoNewline -Encoding UTF8
    Write-Output "SETTINGS_RESTORED 已还原 config/settings.json（测量期间的 TaskbarPolicy/Theme 改写不保留）"
  } elseif (Test-Path $cfgPath) {
    Remove-Item $cfgPath -Force
    Write-Output "SETTINGS_RESTORED 测量前不存在 settings.json，已删除临时文件"
  }
}

try {

# 帧埋点开关：DockSurface 仅在 BEAUTIFUL_FRAMETIME=1 时逐帧写 BEAUTIFUL_FRAME 日志
# （Logger 每次写入都要 open/append/close，常态逐帧写盘本身就会成为掉帧源，故默认关）
$env:BEAUTIFUL_FRAMETIME = '1'
# 仓库内置 SDK（scripts/env.sh 的 PowerShell 等价）：apphost 找不到运行时时会直接弹 ".NET not found"
$dotnetRoot = Join-Path $root '.dotnet'
if (Test-Path $dotnetRoot) { $env:DOTNET_ROOT = $dotnetRoot }

$logDir = Join-Path $bin 'logs'
if (Test-Path $logDir) { Remove-Item (Join-Path $logDir '*.log') -Force -ErrorAction SilentlyContinue }

Start-Process -FilePath $exe -WorkingDirectory $bin
Start-Sleep -Seconds 8

# 起来了吗？没起来就直说，别再往下扫（旧版会一路跑到"埋点不存在"的误导结论上）。
$refused = 0
if (Test-Path $logDir) {
  $refused = @(Select-String -Path (Join-Path $logDir '*.log') -Pattern '第二实例退出' -ErrorAction SilentlyContinue).Count
}
$aliveProc = @(Get-Process -Name Beautiful -ErrorAction SilentlyContinue)
if ($aliveProc.Count -eq 0 -or $refused -gt 0) {
  Write-Output "APP_NOT_STARTED alive=$($aliveProc.Count) 互斥挡回=$refused  日志目录=$logDir"
  Write-Output '  → 测量实例没能跑起来：先看上面有没有 PRE_STOP，再确认 DOTNET_ROOT/自包含产物与单实例互斥。'
  exit 1
}
Write-Output ("APP_STARTED pid=" + $aliveProc[0].Id)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Fm {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
  public struct P { public int X; public int Y; }
}
'@

# 找到 Dock 窗口矩形：从日志里取 AppBar SETPOS 的真实条带矩形（任务栏可见时条带在任务栏之上，
# 不能按"屏幕底部 -40"猜——那会落在任务栏里，光标根本进不了 Dock，放大动画不启动）。
# 坐标口径：这里的 rc 是**物理**像素，与本进程（已 Per-Monitor v2）的 SetCursorPos / Screen.Bounds
# 同一套单位，所以可以直接用；旧版没设感知时才需要换算，而那正是真机上扫掠落空的原因。
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$stripLeft = $b.Left; $stripRight = $b.Right
$stripTop = $b.Bottom - 100; $stripBottom = $b.Bottom - 6
$logFile = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($logFile) {
  $m = Select-String -Path $logFile.FullName -Pattern 'AppBar SETPOS rc=\((-?\d+),(-?\d+)\)\-\((-?\d+),(-?\d+)\)' -AllMatches
  $mm = @($m | ForEach-Object { $_.Matches })
  if ($mm.Count -gt 0) {
    $g = $mm[-1].Groups
    $stripLeft = [int]$g[1].Value; $stripTop = [int]$g[2].Value
    $stripRight = [int]$g[3].Value; $stripBottom = [int]$g[4].Value
    Write-Output "DOCK_STRIP rc=($($g[1].Value),$($g[2].Value))-($($g[3].Value),$($g[4].Value))"
  }
}
$y = [int](($stripTop + $stripBottom) / 2)
$steps = 60
for ($i = 0; $i -lt $steps; $i++) {
  $x = [int]($stripLeft + ($stripRight - $stripLeft) * (0.30 + 0.40 * ($i / $steps)))
  [void][Fm]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 40
}
# 扫掠结束后把光标**挪到面板水平范围之外**，再谈"空闲摘钩"。旧版直接停在 0.70·屏宽处，
# 并假定那已经越出条目行——面板居中且明显窄于屏幕时成立，但条目多 / 屏宽小时 0.70 仍落在面板内，
# 于是 IDLE_SPIN_FRAMES 会测成非零的假故障。条带左端内侧 10px 处不可能是居中的面板。
[void][Fm]::SetCursorPos($stripLeft + 10, $y)
Start-Sleep -Milliseconds 600   # 回落/收敛窗口（λ=14 → 约 0.4s 到视觉稳定）

function Count-Frames {
  $total = 0
  if (Test-Path $logDir) {
    Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | ForEach-Object {
      $total += @(Select-String -Path $_.FullName -Pattern 'BEAUTIFUL_FRAME (\d+)' -AllMatches |
        ForEach-Object { $_.Matches }).Count
    }
  }
  $total
}

# 空闲摘钩证据（评审发现 #1）：扫掠结束后光标已被显式挪到面板水平范围之外（见上）。
# 收敛之后连取两次计数：钩子真的摘掉了，第二次必须与第一次一模一样（IDLE_SPIN_FRAMES=0）；
# 旧实现按窗口矩形判活，条带全宽都算"活跃" → 这里会持续写入 60Hz 的空转帧。
$afterSettle = Count-Frames
Start-Sleep -Milliseconds 2000
$idleSpin = (Count-Frames) - $afterSettle

# 再证明"重新进入行范围 → 钩子自动重新唤醒"（不是靠重启进程）：光标移回行中央并抖动一下。
# MouseMove/MouseEnter 是 Wake 信号，必须重新产生帧日志（REARM_FRAMES > 0）。
$centerX = [int](($stripLeft + $stripRight) / 2)
[void][Fm]::SetCursorPos($centerX - 6, $y)
Start-Sleep -Milliseconds 60
[void][Fm]::SetCursorPos($centerX, $y)
Start-Sleep -Milliseconds 500
$reArm = (Count-Frames) - $afterSettle - $idleSpin

Stop-Process -Name Beautiful -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$frames = @()
if (Test-Path $logDir) {
  Get-ChildItem $logDir -Filter '*.log' | ForEach-Object {
    Select-String -Path $_.FullName -Pattern 'BEAUTIFUL_FRAME (\d+)' -AllMatches | ForEach-Object {
      foreach ($m in $_.Matches) { $frames += [double]$m.Groups[1].Value }
    }
  }
}

if ($frames.Count -lt 5) {
  Write-Output "NO_FRAME_LOGS (found $($frames.Count)) — 测量实例**已确认在跑**（上面有 APP_STARTED），但一条 BEAUTIFUL_FRAME 都没写。"
  Write-Output '  这不再是"埋点不存在"的意思。逐个排：① 埋点开关 $env:BEAUTIFUL_FRAMETIME 是否被外层清掉；'
  Write-Output '  ② 扫掠是否真的落在条目行上（对比上面的 DOCK_STRIP 与本次 y，以及日志里 DockSurface 排布 的自然宽）；'
  Write-Output '  ③ 主题是否被改成不放大的那两档（鸿蒙/Linux MaxScale=1 → 循环不起）。'
  exit 2
}

# 帧间隔统计。评审发现 #5：旧版 `if ($d -gt 0 -and $d -lt 500)` 把 ≥500ms 的间隔**静默丢弃**，
# 于是 GC 停顿 / 合成停摆永远进不了 MAX，"丝滑"是自证清白的结论。现在：
#   MAX      = 进入百分位样本（0 < Δ < 500ms）的最大值（口径与历史报告一致，可对比）
#   RAW_MAX  = 全部正间隔的最大值（含被丢弃的尖峰，这才是真正最坏帧）
#   DROPPED  = 被排除出百分位的样本数（Δ<=0 或 Δ>=500ms），尖峰数量单独可见
$deltas = @(); $dropped = 0; $spikes = 0; $rawMax = 0.0
for ($i = 1; $i -lt $frames.Count; $i++) {
  $d = ($frames[$i] - $frames[$i-1]) * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency
  if ($d -le 0) { $dropped++; continue }
  if ($d -gt $rawMax) { $rawMax = $d }
  if ($d -ge 500) { $dropped++; $spikes++; continue }
  $deltas += $d
}
$s = $deltas | Sort-Object
$avg = ($deltas | Measure-Object -Average).Average
Write-Output ("FRAMES={0} AVG_MS={1:N2} P50={2:N2} P95={3:N2} MAX={4:N2} DROPPED={5} SPIKES_GE_500MS={6} RAW_MAX={7:N2} IDLE_SPIN_FRAMES={8} REARM_FRAMES={9}" -f `
  $deltas.Count, $avg, $s[[int]($s.Count*0.50)], $s[[int]($s.Count*0.95)], $s[-1], $dropped, $spikes, $rawMax, $idleSpin, $reArm)
Write-Output "判据：P95 < 20ms 且 MAX < 34ms 视为丝滑（60fps vsync）；MAX > 50ms 视为可感知卡顿"
Write-Output "口径：MAX 只含 0<Δ<500ms 样本；RAW_MAX 是全部正间隔的最大值（真正最坏帧，含被丢弃尖峰）；DROPPED=被排除出百分位的样本数；"
Write-Output "     IDLE_SPIN_FRAMES=光标停在条目行范围外、收敛后再等 2s 期间新增的帧日志数（应为 0 = 钩子已摘，不空转）；"
Write-Output "     REARM_FRAMES=随后把光标移回行中央 0.5s 内新增的帧日志数（应 > 0 = 摘钩后仍能自动重新唤醒）"
Write-Output "提醒：本测量开着 BEAUTIFUL_FRAMETIME，逐帧写日志（Logger 每帧 open/append/close）本身就是掉帧源，"
Write-Output "     所以 MAX / RAW_MAX 在**带埋点**的跑法下偏悲观，只用于同机纵向对比；判定丝滑以 P50/P95 为主。"

} finally {
  Stop-Process -Name Beautiful -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 600
  Restore-Settings
  # 测量把用户正在跑的实例停过就得起回来，否则"跑个测量"= 桌面接管被悄悄关掉。
  if ($wasRunning -and $prevPath -and (Test-Path $prevPath)) {
    Start-Process -FilePath $prevPath -WorkingDirectory (Split-Path -Parent $prevPath)
    Write-Output "RESTARTED 已按测量前的原路径重新拉起：$prevPath"
  } elseif ($wasRunning) {
    Write-Output "RESTART_SKIPPED 测量前的实例路径取不到了（$prevPath），请手动重新启动 Beautiful"
  }
}
