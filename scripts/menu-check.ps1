Add-Type -AssemblyName System.Windows.Forms,System.Drawing
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes

if (-not ('W32M' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class W32M {
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint f, UIntPtr e);
}
'@
}
function Esc { [W32M]::keybd_event(0x1B,0,0,[UIntPtr]::Zero); [W32M]::keybd_event(0x1B,0,2,[UIntPtr]::Zero) }
function RClick([int]$x,[int]$y){
  [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($x,$y)
  Start-Sleep -Milliseconds 200
  [W32M]::mouse_event(8,0,0,0,[UIntPtr]::Zero); Start-Sleep -Milliseconds 60
  [W32M]::mouse_event(16,0,0,0,[UIntPtr]::Zero)
}
function Find([string]$n){
  $root=[System.Windows.Automation.AutomationElement]::RootElement
  $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$n)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
}

Esc; Start-Sleep -Milliseconds 400

$chev = Find "显示隐藏的通知"; if ($null -eq $chev) { $chev = Find "Show hidden icons" }
if ($null -ne $chev) {
  $cr=$chev.Current.BoundingRectangle
  RClick ([int]($cr.X+$cr.Width/2)) ([int]($cr.Y+$cr.Height/2))
  Start-Sleep -Milliseconds 800
}
$item = Find "Beautiful"
if ($null -eq $item) { Write-Output "TRAY_NOT_FOUND"; exit 1 }
$r=$item.Current.BoundingRectangle
Write-Output ("RECT X={0} Y={1} W={2} H={3}" -f $r.X,$r.Y,$r.Width,$r.Height)
if ($r.Width -le 0) { Write-Output "ZERO_RECT"; exit 2 }
RClick ([int]($r.X+$r.Width/2)) ([int]($r.Y+$r.Height/2))
Start-Sleep -Milliseconds 1200

$b=[System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height
$g=[System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0,0,$b.Size)
$dst=[System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\menu-check.png"))
$bmp.Save($dst,[System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "SAVED $dst"
