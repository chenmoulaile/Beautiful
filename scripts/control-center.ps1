param([string]$Action = "open", [string]$Name = "", [string]$Value = "40", [string]$Prefix = "v3-t7a", [int]$Times = 10)
# 控制中心验收脚本（v3 T7）：用真实鼠标/键盘路径开合面板、逐项点过去，并留截图与 UIA 状态取证。
# 为什么用 UIA 名称而不是固定坐标：面板宽度、顶栏按钮位置、DPI 都会动，而 Name 是我们给控件
# 起的名字（AutomationProperties.Name）——它同时也是无障碍支持的产物，不是为测试专门加的东西。
# 本文件必须以 **UTF-8 with BOM** 保存：Windows PowerShell 5.1 对无 BOM 的 UTF-8 中文注释会
# 误按 ANSI 解码，把多字节序列折进语法结构导致解析失败（仓库已两次踩坑，见 f1069a6 脚本 BOM 修复）。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes

if (-not ('W32C' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class W32C {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  public static void KeyDown(byte vk)  { keybd_event(vk, 0, 0, UIntPtr.Zero); }
  public static void KeyUp(byte vk)    { keybd_event(vk, 0, 2, UIntPtr.Zero); }
  public static void Tap(byte vk) { KeyDown(vk); System.Threading.Thread.Sleep(30); KeyUp(vk); }
}
'@
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = "E:\Beautiful" }
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

function Find([string]$n) {
  $r = [System.Windows.Automation.AutomationElement]::RootElement
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n)
  return $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Children([System.Windows.Automation.AutomationElement]$el) {
  if ($null -eq $el) { return @() }
  return $el.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
}

function MoveTo([int]$x, [int]$y) { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y); Start-Sleep -Milliseconds 120 }

function LDown { [W32C]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40 }
function LUp   { [W32C]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120 }

# 真鼠标点击（不是 InvokePattern）：这样走的是与用户完全相同的事件序列（含按下即失活/看门狗那条竞态）
function ClickAt([int]$x, [int]$y) { MoveTo $x $y; LDown; LUp; Start-Sleep -Milliseconds 350 }

function ClickName([string]$n) {
  $el = Find $n
  if ($null -eq $el) { Write-Output "NOT_FOUND $n"; return $false }
  $r = $el.Current.BoundingRectangle
  Write-Output ("CLICK {0} @({1},{2}) {3}x{4}" -f $n, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2), [int]$r.Width, [int]$r.Height)
  ClickAt ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
  return $true
}

# 注意：这些函数只 return 值、不 Write-Output 状态行——否则返回值会变成"字符串+值"的数组，
# 调用方拿到的就不是路径/布尔而是被拼进管道的那句日志（第一版就踩了这个坑）。
function Shot([string]$name) {
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, $bounds.Size)
  $path = Join-Path $root ($name + ".png")
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  return $path
}

# 面板矩形四周取一圈像素：证明没有黑边圈（分层窗口上"没画到"= 看穿到桌面，那是本项目最怕的故障形态）
function EdgeStats([string]$path, [int]$x, [int]$y, [int]$w, [int]$h) {
  $bmp = [System.Drawing.Bitmap]::new($path)
  $black = 0; $total = 0; $minLuma = 255
  for ($i = 0; $i -lt $w; $i += 2) {
    foreach ($j in @($y, ($y + $h - 1))) {
      if ($j -lt 0 -or $j -ge $bmp.Height -or $i + $x -lt 0 -or $i + $x -ge $bmp.Width) { continue }
      $p = $bmp.GetPixel($x + $i, $j); $l = ($p.R + $p.G + $p.B) / 3
      $total++; if ($l -lt 8) { $black++ }; if ($l -lt $minLuma) { $minLuma = $l }
    }
  }
  for ($j = 0; $j -lt $h; $j += 2) {
    foreach ($i in @($x, ($x + $w - 1))) {
      if ($j + $y -lt 0 -or $j + $y -ge $bmp.Height -or $i -lt 0 -or $i -ge $bmp.Width) { continue }
      $p = $bmp.GetPixel($i, $y + $j); $l = ($p.R + $p.G + $p.B) / 3
      $total++; if ($l -lt 8) { $black++ }; if ($l -lt $minLuma) { $minLuma = $l }
    }
  }
  $bmp.Dispose()
  return ("edge blackish={0}/{1} minLuma={2:F0}" -f $black, $total, $minLuma)
}

function CropPanel([string]$full, [string]$out, [int]$pad) {
  $src = [System.Drawing.Bitmap]::new($full)
  $el = Find "控制中心面板"
  if ($null -eq $el) { Write-Output "PANEL_NOT_FOUND (crop skipped)"; $src.Dispose(); return }
  $r = $el.Current.BoundingRectangle
  $x = [Math]::Max(0, [int]($r.X) - $pad); $y = [Math]::Max(0, [int]($r.Y) - $pad)
  $w = [Math]::Min($src.Width - $x, [int]$r.Width + 2 * $pad); $h = [Math]::Min($src.Height - $y, [int]$r.Height + 2 * $pad)
  $crop = $src.Clone([System.Drawing.Rectangle]::new($x, $y, $w, $h), $src.PixelFormat)
  $crop.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Output ("PANEL RECT X={0} Y={1} W={2} H={3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
  Write-Output (EdgeStats $full ([int]$r.X) ([int]$r.Y) ([int]$r.Width) ([int]$r.Height))
  $crop.Dispose(); $src.Dispose()
  Write-Output "SAVED $out"
}

function DumpTree {
  $el = Find "控制中心面板"
  if ($null -eq $el) { Write-Output "PANEL_NOT_FOUND"; return }
  # 关键状态单独取（Toggle / SelectionItem / RangeValue 是控件的"真值"，不是我们打印出来的字符串）
  foreach ($n in @("控制中心 放大", "控制中心 倒影", "控制中心 媒体胶囊", "macOS", "Linux", "鸿蒙", "隐藏", "自动隐藏", "保留", "系统音量")) {
    $e = Find $n
    if ($null -eq $e) { Write-Output ("STATE {0} = MISSING" -f $n); continue }
    $line = "STATE $n enabled=$($e.Current.IsEnabled)"
    try {
      $tok = $e.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
      if ($tok) { $line += (" toggle={0}" -f ([System.Windows.Automation.TogglePattern]$tok).Current.ToggleState) }
    } catch {}
    try {
      $sel = $e.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
      if ($sel) { $line += (" selected={0}" -f ([System.Windows.Automation.SelectionItemPattern]$sel).Current.IsSelected) }
    } catch {}
    try {
      $rv = $e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
      if ($rv) { $p = [System.Windows.Automation.RangeValuePattern]$rv; $line += (" value={0} min={1} max={2} enabled={3}" -f $p.Current.Value, $p.Current.Minimum, $p.Current.Maximum, $e.Current.IsEnabled) }
    } catch {}
    Write-Output $line
  }
}

function ProcStats {
  $p = Get-Process -Name Beautiful -ErrorAction SilentlyContinue
  if ($null -eq $p) { Write-Output "NO_PROCESS"; return }
  Write-Output ("PID={0} handles={1} threads={2} workingSetMB={3:N1} privateMB={4:N1} windows={5}" -f `
    $p.Id, $p.HandleCount, $p.Threads.Count, ($p.WorkingSet64 / 1MB), ($p.PrivateMemorySize64 / 1MB), `
    (@(Get-Process -Name Beautiful -ErrorAction SilentlyContinue | ForEach-Object { 1 })).Count)
}

function RClick([int]$x, [int]$y) {
  MoveTo $x $y
  [W32C]::mouse_event(8, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 60
  [W32C]::mouse_event(16, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 250
}

# 走"托盘右键菜单"这条与控制中心完全独立的路径改状态，用来证明面板重开时读的是真值而不是缓存。
# 注意：托盘图标只能用"名字叫 Beautiful"来认是**不够**的——资源管理器里任何同名节点都会被
# FindFirst 抢先命中（实测把右键送到了 Explorer 的导航窗格，弹出的是文件菜单）。
# 所以这里枚举所有同名元素，只取落在任务栏那条带里、尺寸像图标的那个。
function FindTrayIcon {
  $r = [System.Windows.Automation.AutomationElement]::RootElement
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Beautiful")
  $all = $r.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
  foreach ($e in $all) {
    $b = $e.Current.BoundingRectangle
    # 只认"屏幕下方那条带里的小方块"：托盘图标本体或溢出浮层里的图标都在那儿，
    # 而资源管理器导航窗格里同名的文件夹节点在屏幕中部，按 Y 就能排除。
    # 本函数内不得 Write-Output（会被调用方的赋值吞成返回值的一部分），诊断一律走调用方的输出。
    if ($b.Width -gt 0 -and $b.Width -lt 60 -and $b.Y -gt ($bounds.Height - 200)) { return $e }
  }
  return $null
}

function TrayTheme([string]$target) {
  [W32C]::Tap(0x1B)   # 先收起任何已打开的菜单，免得第二次右键落在它的上下文里
  Start-Sleep -Milliseconds 300
  $item = FindTrayIcon
  if ($null -eq $item) {
    # 图标在溢出区（本 VM 的托盘就这么窄）：先点开"显示隐藏的通知"，图标才会出现在可点的位置上
    $chev = Find "显示隐藏的通知"; if ($null -eq $chev) { $chev = Find "Show hidden icons" }
    if ($null -ne $chev) {
      $cr = $chev.Current.BoundingRectangle
      RClick ([int]($cr.X + $cr.Width / 2)) ([int]($cr.Y + $cr.Height / 2))
      Start-Sleep -Milliseconds 900
      $item = FindTrayIcon
    }
  }
  if ($null -eq $item) { Write-Output "TRAY_NOT_FOUND"; return }
  $r = $item.Current.BoundingRectangle
  RClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
  Start-Sleep -Milliseconds 1200
  $theme = Find "主题"
  if ($null -eq $theme) { Write-Output "TRAY_THEME_ITEM_NOT_FOUND"; $p = Shot "$Prefix-notheme"; Write-Output "SAVED $p"; return }
  $tr = $theme.Current.BoundingRectangle
  MoveTo ([int]($tr.X + $tr.Width / 2)) ([int]($tr.Y + $tr.Height / 2))   # 悬停触发展开子菜单
  Start-Sleep -Milliseconds 900
  ClickName $target | Out-Null
  Start-Sleep -Milliseconds 900
}

switch ($Action) {
  "open" {
    $ok = ClickName "控制中心"
    Write-Output "OPEN_CLICKED=$ok"
    Start-Sleep -Milliseconds 700
    $full = Shot "$Prefix"
    Write-Output "SAVED $full"
    CropPanel $full (Join-Path $root ($Prefix + "-crop.png")) 10
    DumpTree
    break
  }
  "tree" { DumpTree; break }
  "click" { $ok = ClickName $Name; Write-Output "CLICKED=$ok"; Start-Sleep -Milliseconds 900; DumpTree; break }
  "slider" {
    $e = Find "系统音量"
    if ($null -eq $e) { Write-Output "SLIDER_NOT_FOUND"; break }
    $rv = [System.Windows.Automation.RangeValuePattern]($e.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern))
    $rv.SetValue([double]$Value)
    Start-Sleep -Milliseconds 500
    Write-Output ("SET_RANGE_VALUE {0}" -f $Value)
    DumpTree
    break
  }
  "esc" { [W32C]::Tap(0x1B); Start-Sleep -Milliseconds 500; $e = Find "控制中心面板"; Write-Output ("AFTER_ESC panel_exists={0}" -f ($null -ne $e)); break }
  "outside" { ClickAt 6 ([int]($bounds.Height / 2)); Start-Sleep -Milliseconds 700; $e = Find "控制中心面板"; Write-Output ("AFTER_OUTSIDE_CLICK panel_exists={0}" -f ($null -ne $e)); break }
  "leak" {
    ProcStats
    for ($i = 1; $i -le $Times; $i++) {
      $a = ClickName "控制中心"; $b = ClickName "控制中心"
      Write-Output ("CYCLE {0} open={1} close={2}" -f $i, $a, $b)
    }
    Start-Sleep -Milliseconds 1500
    ProcStats
    $e = Find "控制中心面板"; Write-Output ("AFTER_{0}_CYCLES panel_exists={1}" -f $Times, ($null -ne $e))
    break
  }
  "shot" { $p = Shot "$Prefix"; Write-Output "SAVED $p"; break }
  "crop" { $f = Shot "$Prefix"; Write-Output "SAVED $f"; CropPanel $f (Join-Path $root ($Prefix + "-crop.png")) 10; break }
  "closewin" {
    $e = Find $Name
    if ($null -eq $e) { Write-Output "WINDOW_NOT_FOUND $Name"; break }
    $w = $e.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    if ($null -eq $w) { Write-Output "NO_WINDOW_PATTERN"; break }
    ([System.Windows.Automation.WindowPattern]$w).Close()
    Start-Sleep -Milliseconds 600
    Write-Output ("CLOSED {0} still_there={1}" -f $Name, ($null -ne (Find $Name)))
    break
  }
  "traytheme" { TrayTheme $Name; $p = Shot "$Prefix"; Write-Output "SAVED $p"; break }
  default { Write-Output "UNKNOWN ACTION" }
}
