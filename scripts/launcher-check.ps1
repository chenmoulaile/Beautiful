﻿param([string]$Prefix = "v3-t7c")
# 启动器验收脚本（v3 T7c）：真鼠标点顶栏入口、真键盘敲查询、截图 + UIA 读回结果行。
# 为什么用 UIA 名称而不是坐标：顶栏按钮位置、卡片尺寸、DPI 都会动，而 Name 是我们给控件起的名字
# （AutomationProperties.Name，同时也是无障碍产物）。查询用 SendKeys 而不是 ValuePattern：
# 这条路径要复现的就是"用户手敲 exe 名"那一瞬间的行为。
# 本文件必须以 UTF-8 with BOM 保存（Windows PowerShell 5.1 对无 BOM 的 UTF-8 中文注释会解析失败）。
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes

$root = Split-Path -Parent $PSScriptRoot
if (-not $root) { $root = "E:\Beautiful" }
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

function Find([string]$n) {
  $r = [System.Windows.Automation.AutomationElement]::RootElement
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $n)
  return $r.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function MoveTo([int]$x, [int]$y) { [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x, $y); Start-Sleep -Milliseconds 120 }
function ClickAt([int]$x, [int]$y) {
  MoveTo $x $y
  [W32L]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40
  [W32L]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 400
}
function ClickName([string]$n) {
  $el = Find $n
  if ($null -eq $el) { Write-Host "NOT_FOUND $n"; return $false }
  $r = $el.Current.BoundingRectangle
  ClickAt ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
  Write-Host ("CLICK {0} @({1},{2}) {3}x{4}" -f $n, [int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2), [int]$r.Width, [int]$r.Height)
  return $true
}
function Shot([string]$name) {
  $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen(0, 0, $bounds.Size)
  $path = Join-Path $root ($name + ".png")
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  return $path
}
# 结果行：XAML 给每行的 Border 绑了 AutomationProperties.Name=应用名。注意**不能按 ListItem 找**——
# ItemsControl 的行是 Border（不是 ListBoxItem），UIA 里它的 ControlType 不是 ListItem，
# 所以这里取"名字非空且不等于列表自身名字"的后代，读回来的是界面真的画了什么。
function Rows {
  $list = Find "启动器结果列表"
  if ($null -eq $list) { return @("LIST_NOT_FOUND") }
  $all = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  $out = @()
  foreach ($e in $all) { $n = $e.Current.Name; if ($n -and $n -ne "启动器结果列表") { $out += $n } }
  if ($out.Count -eq 0) { return @("EMPTY") }
  return $out
}
function BoxValue {
  $box = Find "启动器搜索框"
  if ($null -eq $box) { return "BOX_NOT_FOUND" }
  try { $v = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); return ([System.Windows.Automation.ValuePattern]$v).Current.Value }
  catch { return "NO_VALUE_PATTERN" }
}
function BoxSet([string]$text) {
  $box = Find "启动器搜索框"
  if ($null -eq $box) { return $false }
  try { $v = [System.Windows.Automation.ValuePattern]$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $v.SetValue($text); Start-Sleep -Milliseconds 600; return $true }
  catch { return $false }
}
function Send-Text([string]$text) { [System.Windows.Forms.SendKeys]::SendWait($text); Start-Sleep -Milliseconds 600 }
# 查询：先真键盘敲（与用户完全同一条路径），再读回输入框的实际内容核对。
# 本 VM 默认输入法是中文拼音，SendKeys 的 ASCII 会被候选窗吃掉（实测：敲 charmap 弹的是候选条），
# 于是"读回的值不等于查询"时改用 ValuePattern 写入——它走的是同一个 TextChanged → Rebuild → Core.Search
# 路径，只是绕开了输入法；诊断行里如实记 IME_FALLBACK，不假装是手敲成功的。
function Query([string]$text, [string]$shot) {
  if ($text.Length -gt 0) {
    Send-Text $text
    if ((BoxValue) -ne $text) {
      [W32L]::Tap(0x1B) | Out-Null          # 先收起可能挂着的候选窗
      Start-Sleep -Milliseconds 200
      Write-Output ("IME_FALLBACK typed='" + (BoxValue) + "' -> ValuePattern")
      BoxSet $text | Out-Null
    }
  }
  Write-Output ("BOX='" + (BoxValue) + "'")
  $rows = Rows
  Write-Output ("QUERY[" + $text + "] rows=" + ($rows -join ' | '))
  $p = Shot $shot
  Write-Output "SAVED $p"
}

if (-not ('W32L' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class W32L {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
  public static void Tap(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(30); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
'@
}

# ---------- 1. 起进程 ----------
if (-not (Get-Process -Name Beautiful -EA SilentlyContinue)) {
  $exe = Join-Path $root "src\Beautiful.App\bin\Release\net8.0-windows10.0.19041.0\Beautiful.exe"
  Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
  Write-Output "STARTED $exe"
}
Start-Sleep -Seconds 6   # 索引在后台建（桌面扫描 + MSIX 枚举），这里等它落地再验收，避免"正在建立索引"的假阴性

# ---------- 2. charmap：显示名不含查询，只有目标名含 ----------
ClickName "启动器" | Out-Null
Start-Sleep -Milliseconds 700
Query "charmap" "$Prefix-charmap"
# Enter 真启动，再用进程表证明它真的起来了
[W32L]::Tap(0x0D)
Start-Sleep -Seconds 3
$charmap = Get-Process -Name charmap -EA SilentlyContinue
Write-Output ("ENTER_CHARMAP running=" + ($null -ne $charmap) + " pid=" + $(if ($charmap) { $charmap.Id } else { "none" }))
if ($charmap) { $charmap | Stop-Process -Force; Write-Output "CHARMAP_CLOSED" }

# ---------- 3. notepad：Win11 的记事本是 MSIX ----------
Start-Sleep -Milliseconds 600
ClickName "启动器" | Out-Null
Start-Sleep -Milliseconds 700
Write-Output ("REOPEN_BOX_VALUE='" + (BoxValue) + "'")   # 二次打开必须是空框（不残留上次的 charmap）
Query "notepad" "$Prefix-notepad"
[W32L]::Tap(0x0D)
Start-Sleep -Seconds 4
$np = Get-Process -Name Notepad -EA SilentlyContinue
Write-Output ("ENTER_NOTEPAD running=" + ($null -ne $np) + " path=" + $(if ($np) { $np.Path } else { "none" }))
if ($np) { $np | Stop-Process -Force; Write-Output "NOTEPAD_CLOSED" }

# ---------- 4. calc：计算器同样是 MSIX ----------
Start-Sleep -Milliseconds 600
ClickName "启动器" | Out-Null
Start-Sleep -Milliseconds 700
Query "calc" "$Prefix-calc"
[W32L]::Tap(0x0D)
Start-Sleep -Seconds 5
$calc = Get-Process -EA SilentlyContinue | Where-Object { $_.ProcessName -match 'Calc' }
Write-Output ("ENTER_CALC running=" + ($null -ne $calc) + " names=" + (($calc | ForEach-Object { $_.ProcessName }) -join ','))
if ($calc) { $calc | Stop-Process -Force; Write-Output "CALC_CLOSED" }

# ---------- 5. 空白查询与 Esc ----------
Start-Sleep -Milliseconds 600
ClickName "启动器" | Out-Null
Start-Sleep -Milliseconds 700
Query "" "$Prefix-blank"
[W32L]::Tap(0x1B)
Start-Sleep -Milliseconds 600
Write-Output ("AFTER_ESC panel_exists=" + ($null -ne (Find "启动器面板")))
ClickName "启动器" | Out-Null
Start-Sleep -Milliseconds 700
Write-Output ("REOPEN2_BOX_VALUE='" + (BoxValue) + "' rows=" + ((Rows) -join ' | '))
[W32L]::Tap(0x1B)
Start-Sleep -Milliseconds 400
Write-Output "DONE"
