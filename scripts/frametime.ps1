# 采样 Beautiful Dock 的动画帧间隔，客观判定放大动画是否丝滑。
# 原理：Beautiful 在 CompositionTarget.Rendering 循环里每帧写一行日志（BEAUTIFUL_FRAME <tick>）。
#      本脚本启动 app、用鼠标横扫 Dock 触发放大动画，再从日志里解析帧间隔分布。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/frametime.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin  = Join-Path $root 'src\Beautiful.App\bin\Debug\net8.0-windows10.0.19041.0'
$exe  = Join-Path $bin 'Beautiful.exe'
if (-not (Test-Path $exe)) { Write-Output "EXE_NOT_FOUND: 先跑 dotnet build"; exit 1 }

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
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$stripTop = $b.Bottom - 100; $stripBottom = $b.Bottom - 6
$logFile = Get-ChildItem $logDir -Filter '*.log' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($logFile) {
  $m = Select-String -Path $logFile.FullName -Pattern 'AppBar SETPOS rc=\((-?\d+),(-?\d+)\)\-\((-?\d+),(-?\d+)\)' -AllMatches
  $mm = @($m | ForEach-Object { $_.Matches })
  if ($mm.Count -gt 0) {
    $g = $mm[-1].Groups
    $stripTop = [int]$g[2].Value; $stripBottom = [int]$g[4].Value
    Write-Output "DOCK_STRIP rc=($($g[1].Value),$($g[2].Value))-($($g[3].Value),$($g[4].Value))"
  }
}
$y = [int](($stripTop + $stripBottom) / 2)
$steps = 60
for ($i = 0; $i -lt $steps; $i++) {
  $x = [int]($b.Width * 0.30 + ($b.Width * 0.40) * ($i / $steps))
  [void][Fm]::SetCursorPos($x, $y)
  Start-Sleep -Milliseconds 40
}
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

# 空闲摘钩证据（评审发现 #1）：横扫结束时光标在 0.70·屏宽处——已越出条目行的水平影响范围。
# 收敛之后连取两次计数：钩子真的摘掉了，第二次必须与第一次一模一样（IDLE_SPIN_FRAMES=0）；
# 旧实现按窗口矩形判活，条带全宽都算"活跃" → 这里会持续写入 60Hz 的空转帧。
$afterSettle = Count-Frames
Start-Sleep -Milliseconds 2000
$idleSpin = (Count-Frames) - $afterSettle

# 再证明"重新进入行范围 → 钩子自动重新唤醒"（不是靠重启进程）：光标移回行中央并抖动一下。
# MouseMove/MouseEnter 是 Wake 信号，必须重新产生帧日志（REARM_FRAMES > 0）。
$centerX = [int]($b.Width / 2)
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
  Write-Output "NO_FRAME_LOGS (found $($frames.Count)) — app 需按 Task 6 约定每帧写 BEAUTIFUL_FRAME <Stopwatch.GetTimestamp()>"
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

} finally {
  Stop-Process -Name Beautiful -Force -ErrorAction SilentlyContinue
  Restore-Settings
}
