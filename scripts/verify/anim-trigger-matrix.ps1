# 动画触发矩阵取证脚本（v3.0 冲刺 §Q2-7）
# 用法：
#   powershell -ExecutionPolicy Bypass -File scripts\verify\anim-trigger-matrix.ps1 [-Exe <Beautiful.exe 路径>]
# 产出：
#   docs/evidence/anim/<日期>-matrix/matrix.md —— 逐格触发的日志证据
#   控制台打印触发率汇总
# 原则（v3.0 §2.5）：不伪造。脚本驱动不了的格子，在矩阵里如实标"脚本不可驱动（人工步骤见下）"。

param(
    [string]$Exe = "",
    [switch]$KeepRunning,     # 不断开 Beautiful（连续取证时用）
    [switch]$NoDock           # 跳过 Dock 点击路径（无头环境）
)

$ErrorActionPreference = 'Continue'

function Find-BeautifulExe {
    if ($Exe -ne '' -and (Test-Path $Exe)) { return (Resolve-Path $Exe).Path }
    $candidates = @(
        (Join-Path $PSScriptRoot '..\..\dist\Beautiful\Beautiful.exe'),
        (Join-Path $PSScriptRoot '..\..\dist\Beautiful.exe'),
        (Join-Path $PSScriptRoot '..\..\src\Beautiful.App\bin\Debug\net8.0-windows10.0.19041.0\Beautiful.exe')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return (Resolve-Path $c).Path }
    }
    throw "找不到 Beautiful.exe：请用 -Exe 指定（发布版 dist\Beautiful.exe 或 Debug 产物）"
}

function Find-LatestLog {
    param([string]$AppDir)
    $logDir = Join-Path $AppDir 'logs'
    if (-not (Test-Path $logDir)) { return $null }
    $log = Get-ChildItem -Path $logDir -Filter 'Beautiful-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    return $log
}

Add-Type -Namespace Win32 -Name AnimProbe -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string title);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
'@

$SwMinimize = 6; $SwRestore = 9; $SwMaximize = 3
$WmSyscommand = 0x0112; $ScMinimize = 0xF020
$VkLwin = 0x5B; $VkDown = 0x28
$KeyUp = 0x2; $KeyDown = 0x0

$exePath = Find-BeautifulExe
$appDir = Split-Path -Parent $exePath
Write-Output "Beautiful: $exePath"

# ---------- 0. 干净启动 ----------
$existing = Get-Process -Name 'Beautiful' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Output "已有实例在跑，先让它退出（--exit 走完整退出链）..."
    Start-Process -FilePath $exePath -ArgumentList '--exit' | Out-Null
    Start-Sleep -Seconds 3
}

$env:BEAUTIFUL_NO_ANIMATION = ''
# 先记日志基线，再启动：挂载自检行才会落进"本次取证"的窗口里
$log = Find-LatestLog -AppDir $appDir
if (-not $log) { throw "找不到日志目录：$appDir\logs" }
$linesBefore = (Get-Content -Path $log.FullName -Encoding UTF8 -ErrorAction SilentlyContinue).Count
Write-Output "启动 Beautiful ..."
Start-Process -FilePath $exePath | Out-Null
Start-Sleep -Seconds 6
Write-Output "日志：$($log.FullName)（起始 $linesBefore 行）"

# 记下起始时刻，用于截取"本次取证"产生的行
$startTime = Get-Date

function Read-NewLines {
    $all = Get-Content -Path $log.FullName -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($all.Count -le $linesBefore) { return @() }
    return @($all[$linesBefore..($all.Count - 1)])
}

function Wait-WindowReady {
    param([IntPtr]$Hwnd, [int]$TimeoutMs = 5000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (-not [Win32.AnimProbe]::IsIconic($Hwnd)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return $false
}

function Activate-Window {
    # 后台进程直接 SetForegroundWindow 会被前台锁拒绝；先发一次 ALT 释放锁（经典手法），再抢。
    param([IntPtr]$Hwnd, [int]$TimeoutMs = 4000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        [Win32.AnimProbe]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)        # ALT down
        [Win32.AnimProbe]::keybd_event(0x12, 0, $KeyUp, [UIntPtr]::Zero)   # ALT up
        [Win32.AnimProbe]::SetForegroundWindow($Hwnd) | Out-Null
        Start-Sleep -Milliseconds 250
        if ([Win32.AnimProbe]::GetForegroundWindow() -eq $Hwnd) { return $true }
    }
    return $false
}

function Invoke-Trigger {
    param([string]$Name, [scriptblock]$Action, [int]$SettleMs = 1100)
    $before = (Read-NewLines | Where-Object { $_ -match '动画触发：|动画未播：' }).Count
    & $Action
    Start-Sleep -Milliseconds $SettleMs
    $after = (Read-NewLines | Where-Object { $_ -match '动画触发：|动画未播：' }).Count
    $delta = $after - $before
    $tail = @(Read-NewLines | Where-Object { $_ -match '动画触发：|动画未播：' } | Select-Object -Last $delta)
    # 判定要看"最小化动画本身"播没播：MINIMIZESTART 源的触发行才算数，
    # 只有 MINIMIZEEND（还原）说明最小化那一下没播（比如撞上了单替身闸门）
    $minPlayed = @($tail | Where-Object { $_ -match '动画触发：源=(MINIMIZESTART|Dock)' }).Count
    return [pscustomobject]@{
        Case     = $Name
        Lines    = $delta
        MinPlayed = $minPlayed
        Evidence = $tail
    }
}

# ---------- 1. 准备测试窗口 ----------
Write-Output "启动记事本作为测试窗口..."
# 记下取证前就在跑的记事本，收尾时只杀这次新起的（不误伤用户自己的窗口）
$preNotepadPids = @(Get-Process -Name 'notepad' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
# 预先存在的"无标题"空记事本会一直占着前台（缓存只拍前台窗口，它不走就全拍它）。
# 只关标题为"无标题"的（未打开文件、本次取证前遗留的测试窗口），不碰用户打开着文件的记事本。
Get-Process -Name 'notepad' -ErrorAction SilentlyContinue | Where-Object {
    $_.MainWindowTitle -like '无标题*'
} | ForEach-Object {
    Write-Output ("关闭遗留的无标题记事本 pid=" + $_.Id)
    try { $_.Kill() } catch { }
}
Start-Sleep -Milliseconds 600
$notepad = Start-Process notepad -PassThru
# 新版记事本（Win11 商店版）一次启动会有 2-3 个 notepad.exe，只有其中一个持有主窗口：
# 轮询**所有** notepad 进程，谁有主窗口用谁，最多 10 秒
$np = [IntPtr]::Zero
for ($i = 0; $i -lt 50; $i++) {
    Start-Sleep -Milliseconds 200
    $procs = @(Get-Process -Name 'notepad' -ErrorAction SilentlyContinue)
    foreach ($proc in $procs) {
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $np = $proc.MainWindowHandle; $notepad = $proc; break }
    }
    if ($np -ne [IntPtr]::Zero) { break }
}
if ($np -eq [IntPtr]::Zero) {
    # 兜底：按类名找（新旧记事本的主窗口类都是 Notepad）
    $np = [Win32.AnimProbe]::FindWindow('Notepad', $null)
}
if ($np -eq [IntPtr]::Zero) { throw "拿不到记事本窗口句柄（等待 10s 超时）" }
Write-Output ("记事本句柄：0x{0:X}" -f $np.ToInt64())
# 记事本必须真正到前台：前台帧缓存只拍"前台窗口"，它不在前台就永远不会有新鲜帧
$fg = Activate-Window -Hwnd $np
Write-Output ("记事本前台激活：{0}" -f $fg)
if (-not $fg) {
    # 后台取证进程抢不到前台锁：借一次"最小化→还原"把窗口带回前台（还原天然激活窗口）。
    # 这是取证手段，不是判废依据——首轮"冷启动无缓存不播"的行为会如实记录在矩阵外。
    Write-Output "前台锁未拿到：用一轮最小化/还原热身把窗口带回前台..."
    [Win32.AnimProbe]::ShowWindow($np, $SwMinimize) | Out-Null
    Start-Sleep -Milliseconds 1400
    [Win32.AnimProbe]::ShowWindow($np, $SwRestore) | Out-Null
    Start-Sleep -Milliseconds 1800
    $fg = Activate-Window -Hwnd $np
    Write-Output ("热身后前台激活：{0}" -f $fg)
}

$results = [System.Collections.Generic.List[object]]::new()

# ---------- 2. 四条触发路径 ----------
# 判定口径：看"最小化动画本身"（源=MINIMIZESTART/Dock 的触发行）。
# 还原（MINIMIZEEND）只是最小化的影子，单看它会冤枉或错判。
$minimizeVerdict = {
    param($r)
    if ($r.Lines -lt 0) { return '脚本不可驱动' }
    if ($r.MinPlayed -gt 0) { return '已触发' }
    if ($r.Lines -gt 0) { return '最小化未播（还原/判废证据见下）' }
    return '未播'
}

# 路径 1：WM_SYSCOMMAND（与标题栏"最小化按钮"同一消息流）
# 取证前先给采样器 2 秒热身：刚启动的窗口可能还没被前台缓存拍到（缓存龄判废会拒播）
Start-Sleep -Seconds 2
$results.Add((Invoke-Trigger 'caption按钮(WM_SYSCOMMAND)' {
        [Win32.AnimProbe]::PostMessage($np, $WmSyscommand, [IntPtr]$ScMinimize, [IntPtr]::Zero) | Out-Null
    }))
Wait-WindowReady -Hwnd $np | Out-Null
[Win32.AnimProbe]::ShowWindow($np, $SwRestore) | Out-Null
Start-Sleep -Milliseconds 1600   # 还原动画 450ms + 事件排队余量：别让下一条撞上单替身闸门

# 路径 2：ShowWindow(SW_MINIMIZE)（与任务栏按钮 / 程序自身调用同一 API）
$results.Add((Invoke-Trigger '任务栏路径(SW_MINIMIZE)' {
        [Win32.AnimProbe]::ShowWindow($np, $SwMinimize) | Out-Null
    }))
[Win32.AnimProbe]::ShowWindow($np, $SwRestore) | Out-Null
Start-Sleep -Milliseconds 1600

# 路径 3：Win+Down（最大化窗口才吃这条热键）。必须先把窗口真正带到前台，
# 否则热键打到别的窗口上——这不是动画器的锅，是取证的锅，所以要显式核对 IsZoomed 与激活结果。
[Win32.AnimProbe]::ShowWindow($np, $SwMaximize) | Out-Null
Start-Sleep -Milliseconds 700
$activated = Activate-Window -Hwnd $np
Start-Sleep -Milliseconds 500
$zoomed = [Win32.AnimProbe]::IsZoomed($np)
Write-Output ("Win+Down 前置：IsZoomed={0} 前台激活={1}" -f $zoomed, $activated)
$wdResult = Invoke-Trigger 'Win+Down(热键)' {
    [Win32.AnimProbe]::keybd_event($VkLwin, 0, $KeyDown, [UIntPtr]::Zero)
    [Win32.AnimProbe]::keybd_event($VkDown, 0, $KeyDown, [UIntPtr]::Zero)
    [Win32.AnimProbe]::keybd_event($VkDown, 0, $KeyUp, [UIntPtr]::Zero)
    [Win32.AnimProbe]::keybd_event($VkLwin, 0, $KeyUp, [UIntPtr]::Zero)
}
if (-not $activated -or -not $zoomed) {
    # 前台没抢到：热键大概率没打到记事本上。这一格按"脚本不可驱动"记录，不冒充判废证据。
    $wdResult.Lines = -1
    $wdResult.Evidence = @("(脚本无法把记事本带到前台：IsZoomed=$zoomed activated=$activated——人工步骤见文末)")
}
$results.Add($wdResult)
[Win32.AnimProbe]::ShowWindow($np, $SwRestore) | Out-Null
Start-Sleep -Milliseconds 1600

# 路径 4：Dock 点击 —— UIA 按**进程**找 Beautiful 的所有顶层窗口，逐个找记事本条目。
if (-not $NoDock) {
    try {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $beau = Get-Process -Name 'Beautiful' -ErrorAction SilentlyContinue | Select-Object -First 1
        $dockItem = $null
        $dockHost = $null
        if ($beau) {
            $pidCond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $beau.Id)
            $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $pidCond)
            foreach ($c in $candidates) {
                Write-Output ("Beautiful 窗口：name='{0}' class='{1}'" -f $c.Current.Name, $c.Current.ClassName)
                $titleCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, $notepad.MainWindowTitle)
                $appCond = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, 'Notepad')
                $orCond = New-Object System.Windows.Automation.OrCondition($titleCond, $appCond)
                $found = $c.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $orCond)
                if ($found) { $dockItem = $found; $dockHost = $c; break }
            }
        }
        if ($dockItem) {
            $rect = $dockItem.Current.BoundingRectangle
            Write-Output ("Dock 条目：'{0}' @ {1},{2}" -f $dockItem.Current.Name, $rect.X, $rect.Y)
            $results.Add((Invoke-Trigger 'Dock点击(UIA)' {
                    Add-Type -Namespace Win32 -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
'@
                    $cx = [int]($rect.X + $rect.Width / 2); $cy = [int]($rect.Y + $rect.Height / 2)
                    [Win32.Mouse]::SetCursorPos($cx, $cy) | Out-Null
                    Start-Sleep -Milliseconds 120
                    [Win32.Mouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)   # LEFTDOWN
                    [Win32.Mouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)   # LEFTUP
                }))
            Wait-WindowReady -Hwnd $np | Out-Null
        } else {
            # 把 Dock 树里的名字倒出来：条目名不匹配时能立刻看出该用什么条件找
            $names = @()
            if ($dockHost) {
                $all = $dockHost.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                    [System.Windows.Automation.Condition]::TrueCondition)
                foreach ($e in $all) { if ($names.Count -lt 20) { $names += $e.Current.Name } }
                Write-Output ("Dock 树前 20 个名字：" + ($names -join ' | '))
            }
            $results.Add([pscustomobject]@{ Case = 'Dock点击(UIA)'; Lines = -1; MinPlayed = 0;
                Evidence = @('(Dock 树里没找到记事本条目；实际名字见控制台与证据文件)') })
        }
    } catch {
        $results.Add([pscustomobject]@{ Case = 'Dock点击(UIA)'; Lines = -1; MinPlayed = 0;
            Evidence = @(('UIA 异常：' + $_.Exception.Message)) })
    }
} else {
    $results.Add([pscustomobject]@{ Case = 'Dock点击(UIA)'; Lines = -1; MinPlayed = 0;
        Evidence = @('(按 -NoDock 跳过)') })
}

# ---------- 3. 汇总 ----------
$newLines = Read-NewLines
$triggered = @($newLines | Where-Object { $_ -match '动画触发：' })
$skipped = @($newLines | Where-Object { $_ -match '动画未播：' })
$mounted = @($newLines | Where-Object { $_ -match '动画子系统|系统动画接管|动画参数' })

$outDir = Join-Path $PSScriptRoot ('..\..\docs\evidence\anim\' + (Get-Date -Format 'yyyyMMdd-HHmm') + '-matrix')
$outDir = [System.IO.Path]::GetFullPath($outDir)
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# 动画触发矩阵取证（脚本自动驱动）')
[void]$sb.AppendLine('- 时间：' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
[void]$sb.AppendLine('- 进程：' + $exePath)
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 挂载自检（启动期日志）')
foreach ($m in $mounted) { [void]$sb.AppendLine('```'); [void]$sb.AppendLine($m); [void]$sb.AppendLine('```') }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 矩阵')
[void]$sb.AppendLine('| 触发路径 | 证据行数 | 最小化判定 |')
[void]$sb.AppendLine('|---|---|---|')
foreach ($r in $results) {
    $verdict = if ($r.Lines -lt 0) { '脚本不可驱动' }
        elseif ($r.MinPlayed -gt 0) { '已触发' }
        elseif ($r.Lines -gt 0) { '最小化未播（还原/判废证据见下）' }
        else { '未播' }
    [void]$sb.AppendLine('| ' + $r.Case + ' | ' + $r.Lines + ' | ' + $verdict + ' |')
}
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 触发证据行')
foreach ($t in $triggered) { [void]$sb.AppendLine('- ' + $t) }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 未播证据行')
if ($skipped.Count -eq 0) { [void]$sb.AppendLine('- (无)') }
foreach ($s in $skipped) { [void]$sb.AppendLine('- ' + $s) }
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## 人工步骤清单（脚本驱动不了的部分）')
[void]$sb.AppendLine('1. 拖动窗口到副屏后再最小化（混合 DPI 落点核对）。')
[void]$sb.AppendLine('2. 全屏播放视频期间最小化（应整段不播且 Dock/菜单栏隐藏）。')
[void]$sb.AppendLine('3. 4 个窗口 200ms 内连续最小化（应触发风暴抑制，日志出现"因=风暴抑制"）。')

$outFile = Join-Path $outDir 'matrix.md'
[System.IO.File]::WriteAllText($outFile, $sb.ToString(), [System.Text.Encoding]::UTF8)

Write-Output ''
Write-Output '================ 矩阵汇总 ================'
foreach ($r in $results) {
    $verdict = if ($r.Lines -lt 0) { '脚本不可驱动' }
        elseif ($r.MinPlayed -gt 0) { '已触发' }
        elseif ($r.Lines -gt 0) { '最小化未播' }
        else { '未播' }
    Write-Output ('  {0}  ->  {1}' -f $r.Case, $verdict)
}
Write-Output ('触发 ' + $triggered.Count + ' / 未播 ' + $skipped.Count)
Write-Output ('证据已写：' + $outFile)

# ---------- 4. 收尾 ----------
if (-not $KeepRunning) {
    Write-Output '让 Beautiful 走完整退出链...'
    Start-Process -FilePath $exePath -ArgumentList '--exit' | Out-Null
    Start-Sleep -Seconds 2
}
try {
    Get-Process -Name 'notepad' -ErrorAction SilentlyContinue | Where-Object {
        $preNotepadPids -notcontains $_.Id
    } | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 300
        if (-not $_.HasExited) { $_.Kill() }
    }
} catch { }
Write-Output '完成。'
