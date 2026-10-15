param([string]$Action = "all", [string]$Prefix = "v3-probe")
# 抓屏背板验收探针（v3 T2/T3）：对四处瞬态表面（切换器 / 托盘菜单 / 媒体面板）模拟真实按键与
# 鼠标路径触发显示，全屏截图 + 面板矩形边/角像素取证（证明无黑边圈、圆角外是真透明）。
# 本文件必须以 **UTF-8 with BOM** 保存：Windows PowerShell 5.1 对无 BOM 的 UTF-8 中文注释会
# 误按 ANSI 解码，把多字节序列折进语法结构导致解析失败（仓库已两次踩坑，见 f1069a6 脚本 BOM 修复）。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes

if (-not ('W32V' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class W32V {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  public static void KeyDown(byte vk)  { keybd_event(vk, 0, 0, UIntPtr.Zero); }
  public static void KeyUp(byte vk)    { keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
'@
}

# 截图落在仓库根（v3-*.png 已在 .gitignore）——产物只做取证，不入库，也绝不写 C:。
$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = "E:\Beautiful" }
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

function Shot([string]$name) {
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
  $path = Join-Path $root ($name + ".png")
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Output "SAVED $path"
  return $path
}

function Find([string]$n) {
  $r = [System.Windows.Automation.AutomationElement]::RootElement
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n)
  return $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function RClick([int]$x, [int]$y) {
  [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y)
  Start-Sleep -Milliseconds 200
  [W32V]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
  [W32V]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero)
}

# 找目标窗口（WPF 分层窗口的类名前缀 HwndWrapper[Beautiful;;guid]，只能前缀匹配）的物理矩形，用于角点取样
function WinRect([string]$clsPrefix) {
  $r = [System.Windows.Automation.AutomationElement]::RootElement
  foreach ($e in $r.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.ClassName.StartsWith($clsPrefix)) { return $e.Current.BoundingRectangle }
  }
  return $null
}

# 矩形边框取样：返回"最暗的角/边像素"的统计，用来证明没有黑边圈
function EdgeStats([string]$path, [int]$x, [int]$y, [int]$w, [int]$h) {
  $bmp = [System.Drawing.Bitmap]::new($path)
  $black = 0; $total = 0; $minLuma = 255
  for ($i = 0; $i -lt $w; $i += 2) {
    foreach ($j in @($y, ($y + $h - 1))) {
      if ($j -lt 0 -or $j -ge $bmp.Height) { continue }
      $p = $bmp.GetPixel($x + $i, $j); $l = ($p.R + $p.G + $p.B) / 3
      $total++; if ($l -lt 8) { $black++ }; if ($l -lt $minLuma) { $minLuma = $l }
    }
  }
  for ($j = 0; $j -lt $h; $j += 2) {
    foreach ($i in @($x, ($x + $w - 1))) {
      if ($i -lt 0 -or $i -ge $bmp.Width) { continue }
      $p = $bmp.GetPixel($i, $y + $j); $l = ($p.R + $p.G + $p.B) / 3
      $total++; if ($l -lt 8) { $black++ }; if ($l -lt $minLuma) { $minLuma = $l }
    }
  }
  $bmp.Dispose()
  return ("edge blackish={0}/{1} minLuma={2:F0}" -f $black, $total, $minLuma)
}

switch ($Action) {
  "shot" { Shot "$Prefix-full"; break }

  "tray" {
    [W32V]::KeyUp(0x12); [W32V]::KeyUp(0x1B)
    $chev = Find "显示隐藏的通知"; if ($null -eq $chev) { $chev = Find "Show hidden icons" }
    if ($null -ne $chev) {
      $cr = $chev.Current.BoundingRectangle
      RClick ([int]($cr.X + $cr.Width / 2)) ([int]($cr.Y + $cr.Height / 2))
      Start-Sleep -Milliseconds 900
    }
    $item = Find "Beautiful"
    if ($null -eq $item) { Write-Output "TRAY_NOT_FOUND"; break }
    $r = $item.Current.BoundingRectangle
    Write-Output ("TRAY RECT X={0} Y={1} W={2} H={3}" -f $r.X, $r.Y, $r.Width, $r.Height)
    RClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
    Start-Sleep -Milliseconds 1400
    $p = Shot "$Prefix-traymenu"
    $menu = Find "Beautiful Menu"
    Write-Output ("MENU_FOUND=" + ($null -ne $menu))
    [W32V]::KeyUp(0x1B)
    break
  }

  "switcher" {
    [W32V]::KeyUp(0x12); [W32V]::KeyUp(0x09)
    [W32V]::KeyDown(0x12); Start-Sleep -Milliseconds 250
    [W32V]::KeyDown(0x09); Start-Sleep -Milliseconds 60; [W32V]::KeyUp(0x09)
    Start-Sleep -Milliseconds 700
    $p = Shot "$Prefix-switcher"
    $w = WinRect "HwndWrapper[Beautiful"
    if ($null -ne $w) {
      Write-Output ("SWITCHER RECT X={0} Y={1} W={2} H={3}" -f $w.X, $w.Y, $w.Width, $w.Height)
      Write-Output (EdgeStats $p ([int]$w.X) ([int]$w.Y) ([int]$w.Width) ([int]$w.Height))
      # 卡片四角外侧 1px：证明圆角外是真透明（看到的是背后内容而不是黑）
      $bmp = [System.Drawing.Bitmap]::new($p)
      foreach ($c in @(@( ([int]$w.X), ([int]$w.Y) ), @( ([int]($w.X + $w.Width - 1)), ([int]$w.Y) ),
                        @( ([int]$w.X), ([int]($w.Y + $w.Height - 1)) ), @( ([int]($w.X + $w.Width - 1)), ([int]($w.Y + $w.Height - 1)) ))) {
        $px = $bmp.GetPixel($c[0], $c[1])
        Write-Output ("CORNER {0},{1} = RGB({2},{3},{4})" -f $c[0], $c[1], $px.R, $px.G, $px.B)
      }
      $bmp.Dispose()
    } else { Write-Output "SWITCHER_WINDOW_NOT_FOUND" }
    [W32V]::KeyUp(0x12)
    Start-Sleep -Milliseconds 300
    break
  }

  "media" {
    $caps = Find "Beautiful Media Capsule"
    Write-Output ("CAPSULE_FOUND=" + ($null -ne $caps))
    if ($null -ne $caps) {
      $r = $caps.Current.BoundingRectangle
      [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
      Start-Sleep -Milliseconds 200
      [W32V]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 50
      [W32V]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
      Start-Sleep -Milliseconds 1500
      Shot "$Prefix-media" | Out-Null
    }
    break
  }

  "all" {
    Shot "$Prefix-full" | Out-Null
    break
  }
}
