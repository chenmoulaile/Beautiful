# print-window.ps1 - PrintWindow capture of the QuickLook window (ASCII only)
param([string]$Out = "D:\Beautiful\.tmp-verify\ql-pw.png")
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Pw {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public struct RECT { public int L, T, R, B; }
    public static IntPtr Found = IntPtr.Zero;
    public static RECT Rect;
    static bool Cb(IntPtr h, IntPtr l) {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        if (sb.ToString().StartsWith("\u5feb\u901f\u67e5\u770b")) { Found = h; GetWindowRect(h, out Rect); return false; }
        return true;
    }
    public static string Find() { Found = IntPtr.Zero; EnumWindows(Cb, IntPtr.Zero);
        return Found == IntPtr.Zero ? "NOT_FOUND" : string.Format("hwnd=0x{0:X} rect=({1},{2})-({3},{4})", Found.ToInt64(), Rect.L, Rect.T, Rect.R, Rect.B); }
}
"@
[Pw]::SetProcessDPIAware() | Out-Null
Write-Output ([Pw]::Find())
if ([Pw]::Found -eq [IntPtr]::Zero) { exit }
$w = [Pw]::Rect.R - [Pw]::Rect.L
$h = [Pw]::Rect.B - [Pw]::Rect.T
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [Pw]::PrintWindow([Pw]::Found, $hdc, 2)  # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$g.Dispose()
$bmp.Save($Out)
$bmp.Dispose()
Write-Output ("PRINTWINDOW_OK=" + $ok + " saved=" + $Out + " size=" + $w + "x" + $h)
