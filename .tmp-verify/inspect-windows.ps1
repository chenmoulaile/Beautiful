# inspect-windows.ps1 - dump all top-level windows of Beautiful.exe (all logic inside C#)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class WinDump2 {
    public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);

    static HashSet<uint> _pids;
    static StringBuilder _sb;
    static bool Cb(IntPtr h, IntPtr l) {
        uint p;
        GetWindowThreadProcessId(h, out p);
        if (_pids.Contains(p)) {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            RECT r;
            GetWindowRect(h, out r);
            int cloak;
            DwmGetWindowAttribute(h, 14, out cloak, 4);
            int ex = GetWindowLong(h, -20);
            _sb.AppendFormat("hwnd=0x{0:X} vis={1} iconic={2} cloak={3} rect=({4},{5})-({6},{7}) exstyle=0x{8:X} title={9}\n",
                h.ToInt64(), IsWindowVisible(h), IsIconic(h), cloak, r.L, r.T, r.R, r.B, ex, sb);
        }
        return true;
    }
    public static string Dump(IEnumerable<uint> pids) {
        _pids = new HashSet<uint>(pids);
        _sb = new StringBuilder();
        EnumWindows(Cb, IntPtr.Zero);
        return _sb.ToString();
    }
}
"@
$ids = New-Object 'System.Collections.Generic.List[uint32]'
(Get-Process Beautiful -ErrorAction SilentlyContinue) | ForEach-Object { $ids.Add([uint32]$_.Id) }
if ($ids.Count -eq 0) { Write-Output "NO_BEAUTIFUL_PROCESS"; exit }
Write-Output ("PIDS: " + ($ids -join ","))
Write-Output ([WinDump2]::Dump($ids))
Write-Output "DUMP_DONE"
