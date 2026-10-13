using System.Runtime.InteropServices;
using System.Text;

namespace Beautiful.App.Interop;

// Win32 P/Invoke 汇总。实现模式参考 TranslucentTB (GPL-3.0) 与 ModernFlyouts (MIT) 的公开用法；
// 本文件为独立编写的声明，仅借鉴 API 序列知识，不包含 GPL 代码。

internal static partial class NativeMethods
{
    // ---------- 亚克力 / DWM ----------
    public const int WcaAccentPolicy = 19;

    public enum AccentState : uint
    {
        Disabled = 0,
        Gradient = 1,
        TransparentGradient = 2,
        BlurBehind = 3,
        AcrylicBlurBehind = 4,
        HostBackdropBlurBehind = 5,
        Invalid = 6
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ACCENT_POLICY
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;   // AABBGGRR
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    public static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    public static uint ToAbgr(System.Windows.Media.Color c)
        => (uint)(c.A << 24 | c.B << 16 | c.G << 8 | c.R);

    // ---------- AppBar ----------
    public const uint AbmNew = 0, AbmRemove = 1, AbmQueryPos = 2, AbmSetPos = 3,
                      AbmGetState = 4, AbmSetState = 10;
    public const uint AbeBottom = 3;
    public const int AbsAutohide = 0x1, AbsAlwaysOnTop = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);

    [DllImport("shell32.dll")]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    // ---------- 窗口与消息 ----------
    public const int WmDisplayChange = 0x007E;
    public const int WmSettingChange = 0x001A;
    public const uint HshellWindowCreated = 1, HshellWindowDestroyed = 2,
                      HshellActivated = 4, HshellGetMinRect = 5, HshellRedraw = 6,
                      HshellAppCommand = 12;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);
    public static IntPtr FindWindowClass(string className) => FindWindowW(className, null);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern ushort RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    public static extern bool RegisterShellHookWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool DeregisterShellHookWindow(IntPtr hwnd);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    public static string GetWindowClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, 256);
        return sb.ToString();
    }

    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); // SW_RESTORE=9 SW_MINIMIZE=6
    [DllImport("user32.dll")] public static extern IntPtr SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);

    public const uint SwpNozorder = 0x0004, SwpNoactivate = 0x0010, SwpShowWindow = 0x0040, SwpNoSendChanging = 0x0400;
    public const int SwHide = 0, SwShow = 5;
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    public static long GetWindowLongPtr(IntPtr hWnd, int nIndex) => GetWindowLongPtr64(hWnd, nIndex).ToInt64();
    public const long WsExToolwindow = 0x80L;

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index); // SM_CYSCREEN=1

    // ---------- DWM ----------
    public const int DwmwaCloaked = 14;
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ---------- 进程路径 ----------
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    public static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------- WinEvent ----------
    public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    public const uint EventSystemForeground = 0x0003, WineventOutOfContext = 0;

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventDelegate proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")] public static extern int GetDpiForWindow(IntPtr hwnd);

    // ---------- Shell 图标 ----------
    public const uint ShgfiIcon = 0x100, ShgfiPidl = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    }

    // 文件路径精确尺寸取图（SHDefExtractIconW，nIconSize 低字=大图/高字=小图）
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHDefExtractIcon(string file, int index, uint flags,
        out IntPtr hIconLarge, out IntPtr hIconSmall, uint nIconSize);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHParseDisplayName(string name, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attr, ref SHFILEINFOW psfi, uint cb, uint flags);

    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr pv);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
}
