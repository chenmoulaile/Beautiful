# 诊断任务栏条带占用：谁在屏幕底部留了 48px 空隙。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File scripts/strip-probe.ps1
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Probe {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, string n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rr, B; }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
}
'@

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Output ("SCREEN      0,0 - {0},{1}  (h={2})" -f $b.Right, $b.Bottom, $b.Height)
$wa = [System.Windows.Forms.SystemInformation]::WorkingArea
Write-Output ("WORKAREA    {0},{1} - {2},{3}  (bottom gap from screen = {4}px)" -f `
  $wa.Left, $wa.Top, $wa.Right, $wa.Bottom, ($b.Bottom - $wa.Bottom))

foreach ($cls in @('Shell_TrayWnd', 'Shell_SecondaryTrayWnd')) {
  $h = [Probe]::FindWindowW($cls, $null)
  if ($h -eq [IntPtr]::Zero) { Write-Output ("{0,-24} 不存在" -f $cls); continue }
  $r = New-Object Probe+R
  [void][Probe]::GetWindowRect($h, [ref]$r)
  Write-Output ("{0,-24} visible={1} rect=({2},{3})-({4},{5}) h={6}px" -f `
    $cls, [Probe]::IsWindowVisible($h), $r.L, $r.T, $r.Rr, $r.B, ($r.B - $r.T))
}

# 我们的 Dock 窗口（类名 HwndWrapper，标题为空）：按进程找
$proc = Get-Process Beautiful -ErrorAction SilentlyContinue
if ($proc) {
  Write-Output ("Beautiful PID={0} 正在运行" -f $proc.Id)
  # 顺带把 Dock 自己上报的 AppBar 矩形拉出来（日志与 exe 同目录），一次探测即可对账
  $log = "{0}\logs\Beautiful-{1:yyyyMM}.log" -f (Split-Path $proc.Path -Parent), (Get-Date)
  if (Test-Path $log) {
    $line = Select-String -Path $log -Pattern 'AppBar QUERYPOS' | Select-Object -Last 1
    if ($line) { Write-Output ("DOCK      " + ($line.Line -replace '^\[[^\]]+\]\s*\[[^\]]+\]\s*','')) }
  }
} else {
  Write-Output "Beautiful 未运行（Dock 条带未注册）"
}
Write-Output "判读：WORKAREA 底部缺口 = 任务栏高 + Dock 高 → 两者条带并存（48px 间隙来源）；缺口仅等于 Dock 高 → 任务栏条带已释放（Dock 贴真底）。"
Write-Output "对照：DOCK 行里『间隙=0px / 真屏幕底边』即 AppBar 已拿到 screenH；『任务栏仍占 48px』即预留未释放。"
