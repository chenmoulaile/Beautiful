# raise-window.ps1 - force QuickLook window to absolute top of topmost band (ASCII only)
param([string]$Out = "D:\Beautiful\.tmp-verify\ql-raised.png")
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class Raise {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public static IntPtr Found = IntPtr.Zero;
    static bool Cb(IntPtr h, IntPtr l) {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        if (sb.ToString().StartsWith("\u5feb\u901f\u67e5\u770b")) { Found = h; return false; }
        return true;
    }
    public static string FindAndRaise() {
        Found = IntPtr.Zero; EnumWindows(Cb, IntPtr.Zero);
        if (Found == IntPtr.Zero) return "NOT_FOUND";
        // SWP_NOMOVE(2)|SWP_NOSIZE(1)=3; HWND_TOPMOST=(-1)
        bool ok = SetWindowPos(Found, new IntPtr(-1), 0, 0, 0, 0, 3);
        return "RAISED hwnd=0x" + Found.ToInt64().ToString("X") + " ok=" + ok;
    }
}
"@
$result = [Raise]::FindAndRaise()
Write-Output $result
