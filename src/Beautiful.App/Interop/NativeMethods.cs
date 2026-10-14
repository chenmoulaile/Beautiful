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
    // ABE_LEFT=0 ABE_TOP=1 ABE_RIGHT=2 ABE_BOTTOM=3：顶栏用 ABE_TOP，与 Dock 的 ABE_BOTTOM 不同边共存
    // （shell 工作区 = 屏幕矩形逐条带裁剪所有已注册 AppBar，两条互不破坏）。
    public const uint AbeTop = 1, AbeBottom = 3;
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

    /// <summary>ABM_GETSTATE：读当前 AppBar（任务栏）状态位（ABS_AUTOHIDE=1 / ABS_ALWAYSONTOP=2）。hWnd 置空取全局值。</summary>
    public static int GetAppBarState()
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return SHAppBarMessage(AbmGetState, ref abd).ToInt32();
    }

    /// <summary>ABM_SETSTATE：写 AppBar 状态位（TaskbarManager 用它开/关自动隐藏以释放/归还底部条带）。返回 TRUE=1。</summary>
    public static int SetAppBarState(int state)
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), lParam = state };
        return SHAppBarMessage(AbmSetState, ref abd).ToInt32();
    }

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

    // 激活式弹层夺焦（评审 Important#3）：托盘右键/定时器显示时本进程未必是前台，系统前台锁会
    // 压制 SetForegroundWindow/Activate → 菜单拿不到键盘焦点。经典解法：临时 AttachThreadInput 到
    // 当前前台线程再 SetForegroundWindow + BringWindowToTop，随后解绑。
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    public const uint SwpNozorder = 0x0004, SwpNoactivate = 0x0010, SwpShowWindow = 0x0040, SwpNoSendChanging = 0x0400;
    public const int SwHide = 0, SwShowna = 8; // SW_SHOWNA：恢复可见但不激活
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public const uint SwpNosize = 0x0001;

    // 窗口区域裁切（Task 12 切换面板）。非分层窗口没有逐像素 alpha，而
    // DWMWA_WINDOW_CORNER_PREFERENCE 只提供系统那一种半径（约 8 物理 px），做不出 macOS 大圆角、
    // 更做不出鸿蒙胶囊；于是改由 SetWindowRgn 在原生层把窗口裁成任意圆角矩形——
    // 形状由区域给出，DWM 的投影/背板会依该区域自动成形。坐标是**窗口客户区物理像素**
    // （WindowStyle=None 时客户区即窗口矩形），与 WPF 的 DIP 无关。
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    // 返回 1=成功、0=失败。**成功时区域所有权转移给系统**（此后不得再 DeleteObject，否则会
    // 销毁仍被窗口引用的区域）；只有失败时才由调用方删除——这是 GDI 句柄泄漏的经典陷阱。
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    // 异步键盘状态（切换面板防卡死看门狗用）：最高位为 1 表示该键此刻物理按下。
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    public static long GetWindowLongPtr(IntPtr hWnd, int nIndex) => GetWindowLongPtr64(hWnd, nIndex).ToInt64();

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    public static long SetWindowLongPtr(IntPtr hWnd, int nIndex, long value) => SetWindowLongPtrW(hWnd, nIndex, new IntPtr(value)).ToInt64();

    public const int GwlpExStyle = -20;
    public const int GwlpStyle = -16;
    public const long WsExToolwindow = 0x80L;
    public const long WsExNoactivate = 0x0800_0000L; // 点击不激活窗口（自绘弹层防抢焦点/防 Deactivated 竞态自关）
    public const long WsCaption = 0x00C0_0000L;     // WS_CAPTION：有标题栏（全屏判定的反向条件）

    // ---------- 低级键盘钩子（Alt+Tab 接管，Task 11） ----------
    // WH_KEYBOARD_LL=13：dwThreadId=0 → 全局系统范围钩子；hMod 对 LL 钩子被系统忽略，传 0 即可。
    // 回调在安装钩子的线程消息循环上被调用，故安装必须发生在 WPF UI 线程（有消息泵）。
    public const int WhKeyboardLl = 13;
    public const int WmKeyDown = 0x0100, WmKeyUp = 0x0101, WmSysKeyDown = 0x0104, WmSysKeyUp = 0x0105;

    public const int VkTab = 0x09, VkEscape = 0x1B;
    public const int VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;
    public const int VkLWin = 0x5B, VkRWin = 0x5C;
    public const int VkLShift = 0xA0, VkRShift = 0xA1;
    public const int VkLControl = 0xA2, VkRControl = 0xA3;
    public const int VkLMenu = 0xA4, VkRMenu = 0xA5; // VK_LMENU/RMENU（左右 Alt 分别跟踪）

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public const uint LlkhfInjected = 0x10, LlkhfAltDown = 0x20, LlkhfUp = 0x80;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    // 合成 Alt 点击（SetForegroundWindow 前台锁压制时的最后一招：让系统自行完成激活）。
    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public const uint KeyeventfExtendedkey = 0x1, KeyeventfKeyup = 0x2;

    // ---------- 显示器（物理像素工作区，弹层边缘翻转定位用） ----------
    public const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // 混合 DPI 多显示器：弹层尺寸/翻转必须用【锚点所在显示器】的有效 DPI，而非刚创建未落位
    // hwnd 的 GetDpiForWindow（PerMonitorV2 下那返回初置/主屏 DPI → 副屏 150% 时测量差 1.5 倍）。
    // shcore.GetDpiForMonitor 返回 HRESULT；MDT_EFFECTIVE_DPI(0) 即 WPF 布局所用的缩放基准。
    public const int MdtEffectiveDpi = 0;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index); // SM_CYSCREEN=1

    // ---------- DWM ----------
    public const int DwmwaCloaked = 14;
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ---------- DWM 系统背板（Win11 Mica / Acrylic）----------
    // DWMWA_SYSTEMBACKDROP_TYPE(38) 需 22H2+：0=AUTO 1=NONE 2=ACRYLIC 3=MICA 4=TABBED。
    // 22H2 起该属性在 21H2/21H1 上返回 E_INVALIDARG → 退未公开 DWMWA_MICA_EFFECT(1029)（BOOL，
    // 传 4 字节 int 1 等价，避免 bool 封送宽度歧义）→ 再退 Win10 旧 SetWindowCompositionAttribute。
    // 关键限制：背板只合成到"非分层"窗口（WS_EX_LAYERED 即 WPF AllowsTransparency=true 的唯一透明
    // 模式）的重定向表面上——分层窗口请求会被接受但什么都不画 → 用背板的窗口必须 AllowsTransparency=false，
    // 圆角改由 DWMWA_WINDOW_CORNER_PREFERENCE 提供，边框由 DwmExtendFrameIntoClientArea(-1) 铺满玻璃层。
    public const int DwmwaSystemBackdropType = 38;
    public const int DwmwaMicaEffect = 1029;
    // DWMWA_WINDOW_CORNER_PREFERENCE(33)：0=DEFAULT(跟随系统) 1=DONOTROUND 2=ROUND(系统圆角) 3=SMALLROUND
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaUseImmersiveDarkMode = 20; // 深色标题栏（本项目窗口均无边框，留作后续顶栏/设置窗用）

    public const int DwmsbtAuto = 0, DwmsbtNone = 1, DwmsbtAcrylic = 2, DwmsbtMica = 3, DwmsbtTabbed = 4;
    public const int DwmwcpDefault = 0, DwmwcpDoNotRound = 1, DwmwcpRound = 2, DwmwcpSmallRound = 3;

    /// <summary>MARGINS：四边 -1 = 把 DWM 玻璃层铺满整个客户区（Aero 时代 "sheet of glass"，背板的载体）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int LeftWidth, RightWidth, TopHeight, BottomHeight;
    }

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

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

    // 修正拼写：user32 的导出函数就叫 UnhookWinEvent（原声明 UnwinEventHook 能编译通过，但运行时
    // EntryPointNotFoundException —— 只在"顶栏优雅关闭"路径上触发，Task 11 的退出实测才把它抓出来：
    // 异常被全局兜底吞掉 → Window.Close 中断 → 进程卡在退出链路里）。
    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

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

    // ---------- 电源（顶栏电量状态项，Task 9） ----------
    /// <summary>SYSTEM_POWER_STATUS（kernel32 GetSystemPowerStatus）：均为字节/双字，Pack=1 对齐 Win32 布局。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0=电池供电 1=接交流电 255=未知
        public byte BatteryFlag;         // 位1高 位2低 位4临界 位8充电中；128=无电池 255=电池不存在
        public byte BatteryLifePercent;  // 0..100（255=未知）
        public byte SystemStatus;        // bit0 节能 bit1 低电警告 bit2 临界动作 bit3 电池耗尽事件
        public uint BatteryLifeTime;     // 剩余分钟（-1=未知）
        public uint BatteryFullLifeTime; // 满电分钟（-1=未知）
    }

    public const byte AcLineUnknown = 255, BatteryFlagNoBattery = 128, BatteryFlagUnknown = 255, BatteryPercentUnknown = 255;
    public const int BatteryChargingBit = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    // ---------- 关闭窗口（顶栏"窗口"菜单：对前台窗口发 WM_CLOSE） ----------
    public const int WmClose = 0x0010;
    public const int SwMinimize = 6; // SW_MINIMIZE（另有 SW_RESTORE=9 已在 WindowTracker 本地定义）

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
