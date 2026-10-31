# ci-screenshot.ps1 — Windows CI 上的"运行期目视取证"：
# 启动 Beautiful.exe → 等待界面就绪 → 全屏截图（Dock/顶栏初始态）
# → 模拟点击顶栏时钟（验证通知中心是否打开/是否报错）→ 再截图
# → 收集 logs/ 里的异常行 → 有未处理异常则以非零码退出
param(
    [string]$AppDir = "$PSScriptRoot\..\build\shot-app",
    [string]$OutDir = "$PSScriptRoot\..\build\shots"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# 【关键】runner 会话默认 disconnected → DWM 停止合成 → CopyFromScreen 拿到旧帧。
# 把会话重定向到物理控制台，保住实时合成（alpha.15 实证：不加这句，截图全是启动前的旧桌面）。
$currentUser = $env:USERNAME
query user 2>$null | Out-String | Write-Host
$sessionId = (query user 2>$null | Select-String $currentUser) -replace '.*\s(\d+)\s*$', '$1'
if ($sessionId -match '^\d+$') {
    Write-Host "redirect session $sessionId -> console"
    tscon $sessionId /dest:console 2>&1 | Write-Host
    Start-Sleep -Seconds 2
} else {
    Write-Host "WARN: no interactive session found for $currentUser"
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class User32 {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
"@
[User32]::SetProcessDPIAware() | Out-Null

function Save-Shot([string]$name) {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $g.Dispose()
    $path = Join-Path $OutDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "shot: $path ($($b.Width)x$($b.Height))"
}

function Click-At([int]$x, [int]$y) {
    [User32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 150
    [User32]::mouse_event([User32]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [User32]::mouse_event([User32]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
}

# 1) 启动 app
$exe = Join-Path $AppDir 'Beautiful.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe（先跑 dotnet publish）" }
$proc = Start-Process -FilePath $exe -WorkingDirectory $AppDir -PassThru
Write-Host "app started pid=$($proc.Id)"

try {
    Start-Sleep -Seconds 12          # Dock/顶栏动画/托盘桥就绪
    Save-Shot '01-initial.png'

    # 2) 点顶栏时钟（顶栏横跨屏幕顶部，时钟在右端）
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    Click-At -x ($b.Width - 70) -y 10
    Start-Sleep -Seconds 3           # 通知中心滑入 + 可能的异常弹窗出现
    Save-Shot '02-clock-flyout.png'

    # 3) Esc 关面板，再点一次验证"再点收起"语义
    Start-Sleep -Seconds 1
    Click-At -x ($b.Width - 70) -y 10
    Start-Sleep -Seconds 2
    Save-Shot '03-after-toggle.png'
}
finally {
    # 4) 日志取证：未处理异常 → CI 红；Dock/顶栏就绪行缺失 → CI 红
    $logDir = Join-Path $AppDir 'logs'
    if (Test-Path $logDir) {
        Copy-Item -Path $logDir -Destination $OutDir -Recurse -Force
        $all = Get-ChildItem $logDir -Filter *.log | Get-Content | Out-String
        $hits = Get-ChildItem $logDir -Filter *.log | ForEach-Object {
            Select-String -Path $_.FullName -Pattern '未处理异常|XamlParseException|Unhandled'
        }
        if ($hits) {
            Write-Host "FOUND UI EXCEPTIONS:"
            $hits | ForEach-Object { Write-Host $_.Line }
            exit 7
        }
        if ($all -notmatch 'Dock 就绪')      { Write-Host 'MISSING: Dock 就绪';    exit 9 }
        if ($all -notmatch 'TopBar|顶栏')     { Write-Host 'MISSING: TopBar 就绪';  exit 10 }
        Write-Host "logs clean: no unhandled UI exceptions; Dock/TopBar ready lines present"
    } else {
        Write-Host "no logs dir (app may have failed to start)"
        exit 8
    }
    if (-not $proc.HasExited) {
        Get-Process | Where-Object { $_.Id -eq $proc.Id } | Stop-Process -Force
        Write-Host "app stopped"
    }
}
Write-Host "SCREENSHOT-OK"
