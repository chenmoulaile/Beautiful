using System.Runtime.InteropServices;
using System.Text;

namespace Beautiful.App.Interop;

// Win32 P/Invoke 汇总。实现模式参考 TranslucentTB (GPL-3.0) 与 ModernFlyouts (MIT) 的公开用法；
// 本文件为独立编写的声明，仅借鉴 API 序列知识，不包含 GPL 代码。

internal static partial class NativeMethods
{
    // ---------- 旧亚克力（SetWindowCompositionAttribute）/ DWM ----------
    // v3 起本 API **只**用于常驻表面（顶栏 / Dock）的 best-effort 增强：系统认账就有真模糊，
    // 不认账也只是看不到它（我们自绘的半透明 tint 底始终铺在那里，绝不落黑）。
    // 瞬态表面（切换器/菜单/媒体面板）不再调用它——那条路改走自绘抓屏模糊，见 Services/ScreenBlurBackdrop.cs。
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
    // ABM_NEW/ABM_SETPOS 之后 shell 通过 uCallbackMessage 发来的通知（wParam）：
    // POSCHANGED=1 表示"你的条带位置需要重新协商"（其它 AppBar 落位、任务栏状态位变化都会触发）。
    public const int AbnNew = 0, AbnPosChanged = 1, AbnFullscreenApp = 2, AbnWindowArrange = 3;

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
    public const int WmWindowPosChanged = 0x0047;   // 窗口位置/尺寸/层序已变（条带漂移的检测点）
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
    // IsHungAppWindow：查询窗口所属线程是否停止处理消息（"未响应"）。判据本身不发消息、不阻塞，
    // 专供 EnumWindows 遍历在 GetWindowText 这类 SEND 消息之前早退挂死窗口（终审 I-7，见 WindowEligibility）。
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hWnd);
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
    public const long WsExNoactivate = 0x0800_0000L; // 点击不激活窗口（DWM 预览浮层、切换器覆盖层使用）。
    // 终审 M-2：本常量是全库唯一定义（原 :386 的 uint 同名重复已并入）。旧注释的"自绘弹层防抢焦点/
    // 防 Deactivated 竞态自关"理据已随 Task 2-3 裁定**废止**——FluentPopupHost 现按设计自行激活，
    // 不再施加本样式，勿以"防抢焦点"为由给弹层恢复它。
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

    // KBDLLHOOKSTRUCT 不在此声明：回调按偏移手工读字段（布局见 LowLevelKeyboardHook.Invoke 注释），
    // 结构体封送从未启用（评审附带：删除死声明）。LlkhfUp 同理删除——抬起判定用消息号不用标志位。
    public const uint LlkhfInjected = 0x10, LlkhfAltDown = 0x20;

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

    // ---------- 显示器（物理像素工作区，弹层边缘翻转定位与条带几何用） ----------
    // MONITORINFO.dwFlags 位 0x1 = 主显示器（其 rcMonitor 左上角即物理原点 (0,0)）。
    public const uint MonitorDefaultNull = 0, MonitorDefaultToPrimary = 1, MonitorDefaultToNearest = 2;
    public const uint MonitorinfofPrimary = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    // 条带（顶栏/Dock）几何的锚点：窗口**实际所在**的显示器。v2 用 GetSystemMetrics(SM_CXSCREEN/
    // SM_CYSCREEN) 取"屏幕尺寸"，那是**只有主屏**的全局值，副屏（含 Left<0 的左置副屏）上的条带
    // 宽度与贴边必然错位（v3 T1 起 SM_CX/SM_CYSCREEN 从几何路径退役）。
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    // 显示器枚举（设置项 DockMonitor/TopBarMonitor 的"第 N 台"与主屏判定用）。
    // 回调委托必须长期持有（这里放静态 readonly 字段），且枚举顺序由显示驱动决定、不保证稳定，
    // 所以序号只在**本次枚举**内有意义：设置面板每次打开都重新列出，日志同时打印该屏矩形供核对。
    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // 混合 DPI 多显示器：弹层尺寸/翻转必须用【锚点所在显示器】的有效 DPI，而非刚创建未落位
    // hwnd 的 GetDpiForWindow（PerMonitorV2 下那返回初置/主屏 DPI → 副屏 150% 时测量差 1.5 倍）。
    // shcore.GetDpiForMonitor 返回 HRESULT；MDT_EFFECTIVE_DPI(0) 即 WPF 布局所用的缩放基准。
    public const int MdtEffectiveDpi = 0;

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index); // SM_CYSCREEN=1；
    // v3 T1 起**不得**再用于条带/贴边几何（那只有主屏），仅保留给弹层最后兜底（FluentPopupHost）。

    // ---------- DWM ----------
    public const int DwmwaCloaked = 14;
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // ---------- DWM 系统背板（v3 已从 UI 表面路径退役）----------
    // DWMWA_SYSTEMBACKDROP_TYPE(38) / DWMWA_MICA_EFFECT(1029) / DWMWA_USE_IMMERSIVE_DARK_MODE(20)
    // 与 DwmExtendFrameIntoClientArea 一起删除：真机取证证明"非分层窗口 + 玻璃层 + 半透明内容"并不会
    // 把客户端 alpha 与系统材质合成（Alt+Tab 面板画成黑矩形、顶栏矩形内露桌面），v3 的模糊改由
    // Services/ScreenBlurBackdrop.cs 自己抓屏合成。保留的唯一 DWM 属性是下面 33=圆角：它服务的是
    // Preview/ThumbnailPreview.cs 那个**纯 Win32 宿主窗口**（客户区只承载 DWM 实时缩略图，无 WPF airspace），
    // 那里没有逐像素 alpha 需求，圆角是窗口自身的形状，属可靠路径（v2 截图已验证）。
    // DWMWA_WINDOW_CORNER_PREFERENCE(33)：0=DEFAULT(跟随系统) 1=DONOTROUND 2=ROUND(系统圆角) 3=SMALLROUND。
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    // ---------- DWM 缩略图（Task 13 悬停实时预览）----------
    // 与上面三个"属性类"DWM API 不同，缩略图是一组**句柄型** API：DwmRegisterThumbnail 在目标窗口
    // （dst）上注册一张源窗口（src）的实时画面，返回一个缩略图句柄。该句柄**必须** DwmUnregisterThumbnail
    // 释放——它不是进程退出自动回收的普通 HWND 引用，驻留进程里每次悬停泄漏一个就是"越开越慢/句柄数
    // 单调上涨"的经典故障（本任务的首要风险，见 Preview/ThumbnailPreview.cs 的释放矩阵）。
    // 合成位置：DWM 把缩略图画进 **dst 窗口的客户区坐标系**（rcDestination）。dst 若是 WPF 窗口，
    // WPF 的合成表面覆盖在缩略图之上（airspace）→ 预览必须注册到一个客户区只用来承载缩略图的
    // 纯 Win32 窗口，卡片的边框/标题由 GDI 画在缩略图矩形**之外**的留白环里。
    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    /// <summary>
    /// DWM_THUMBNAIL_PROPERTIES（dwmapi.h）。字段顺序必须与原生结构逐字节一致：
    /// DWORD / 两个 RECT / BYTE / 两个 BOOL——BYTE 之后原生对齐会填 3 字节，LayoutKind.Sequential
    /// 的默认打包给出同样的空洞，故无需 Pack=1（填了反而错位）。dwFlags 是**逐字段生效开关**：
    /// 未在 flags 中置位的字段会被 DWM 忽略（不是"取默认值"），所以每次更新都要把要写的位的并集传进去。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;          // 0..255（需 DwmtOpacity）
        public bool fVisible;         // BOOL（4 字节），需 DwmtVisible
        public bool fSourceClientAreaOnly; // BOOL，需 DwmtSourceClientAreaOnly
    }

    public const int DwmtRectSource = 0x1, DwmtRectDestination = 0x2, DwmtOpacity = 0x4,
                     DwmtVisible = 0x8, DwmtSourceClientAreaOnly = 0x10;

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr dst, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("dwmapi.dll")]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    // ---------- 原生窗口类（预览宿主：不用 WPF，规避 airspace）----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;   // 无菜单须传 NULL（string? → 封送 0 指针）；空串是非法菜单名
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 回调委托必须由调用方长期持有（本类的 WndProc 存在静态字段里）：委托被 GC 后消息打到已回收的桩上
    // 就是随机的原生崩溃——与 WinEventDelegate 同一套纪律。
    // SetLastError=true 是调用方判定 ERROR_CLASS_ALREADY_EXISTS(1414) 的前提：没有它 Marshal.GetLastWin32Error()
    // 读到的是陈旧值（Task 13 评审 Important）。CreateWindowExW 调用点不读错误码，故不加。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName,
        uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // GWL_STYLE(-16) 的 32 位写入：WS_EX_* 用 SetWindowLongPtr，样式位仍走 32 位入口
    [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr hWnd, IntPtr rect, bool erase);

    // RegisterClassEx 的 hInstance 须为模块基址；GetModuleHandle(null) 返回主 exe 句柄。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    // 类样式：CS_DROPSHADOW(0x20000) 让顶层弹出窗口自带系统投影（预览卡片的阴影来源）。
    public const uint CsDropShadow = 0x0002_0000;
    // ShowWindow nCmdShow：SW_SHOWNOACTIVATE=4（显示但不夺焦；SW_HIDE=0 已在上方"窗口与消息"段定义）。
    public const int SwShowNoActivate = 4;
    // SetWindowPos 的 hwndInsertAfter：HWND_TOPMOST=-1（预览浮于其他窗口之上，配合 SWP_NOACTIVATE 不抢焦点）。
    public static readonly IntPtr HwndTopmost = new(-1);

    public const uint WsPopup = 0x8000_0000, WsVisible = 0x1000_0000;
    // WsExToolwindow(0x80) 已在上方"窗口与消息"段以 long 形式声明（供 WindowEligibility 与 long
    // 的 ex 位与）——此处不再重复定义；CreateWindowExW 的 exStyle 是 uint，调用点显式转型即可。
    public const uint WsExTransparent = 0x20; // WS_EX_NOACTIVATE 唯一定义见上方 long 版 WsExNoactivate（终审 M-2 合并，调用点显式转型）

    // ---------- GDI 绘制（预览卡片边框/标题：画在缩略图矩形之外的留白环里）----------
    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading;
        public int tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [DllImport("gdi32.dll")] public static extern IntPtr CreateSolidBrush(uint color);  // 0x00BBGGRR
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int fn);
    // FillRect 在 user32.dll（不在 gdi32！）——误标 gdi32 会 EntryPointNotFoundException，
    // 且它发生在 WndProc 的 WM_PAINT 栈上 → 直接崩掉驻留进程（Task 13 实测踩过）。
    [DllImport("user32.dll")] public static extern bool FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);
    [DllImport("gdi32.dll")] public static extern bool SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] public static extern bool SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW tm);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, bool italic, bool underline, bool strikeOut,
        int charSet, int outPrecision, int clipPrecision, int quality, int pitchAndFamily, string faceName);

    [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr hwnd, IntPtr lpps);
    [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr hwnd, IntPtr lpps);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(IntPtr hdc, string text, int count, ref RECT rect, uint flags);

    public const int ObsoleteNull = 5;  // NULL_BRUSH（GDI 保留对象，非分层窗口用它 = 客户区不填充）
    public const int BkOpaque = 2, BkTransparent = 1;   // SetBkMode：Win32 定义 TRANSPARENT=1、OPAQUE=2（原声明写反）
    public const uint DtRight = 0x2, DtSingleLine = 0x20, DtEndEllipsis = 0x8000, DtPathEllipsis = 0x20000;
    public const int WsExGwl = (-20);

    public const uint WmPaint = 0x000F, WmDestroy = 0x0002, WmNcHitTest = 0x0084;

    // ---------- 屏幕取像（v3 T2 自绘背板：玻璃感由我们合成，不再外包给 DWM 材质）----------
    // 取像序列是 Win32 截屏的标准做法（MSDN "Screen Capture" 示例，ShareX 等开源截屏工具同用法，
    // 本项目独立编写声明，不含第三方代码）：
    //   GetDC(NULL) → CreateCompatibleDC → CreateDIBSection(32bpp, biHeight<0 = top-down) → BitBlt。
    // 两个必须注意的点：
    //  ① CAPTUREBLT(0x40000000)：不加它就抓不到分层窗口与 DWM 合成的内容（壁纸、其他软件浮层），
    //     背板会糊成"只剩桌面底色"的纯色，玻璃感直接消失；
    //  ② 目标必须是**自己选的** top-down DIB section：屏幕 DC 的位图是 bottom-up 且不能被 WPF 直接引用，
    //     拿到 bits 指针后按行主序拷进托管缓冲，才能喂给 Core 的 BoxBlur（纯字节数组）。
    // GDI 句柄全部由调用方成对释放（ReleaseDC/DeleteDC/DeleteObject），驻留进程漏一个就是句柄单调上涨。
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);   // hWnd=NULL → 整个屏幕 DC
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy,
                                     IntPtr hdcSrc, int xSrc, int ySrc, uint rop);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi,
                                                 uint usage, out IntPtr bits, IntPtr section, uint offset);

    /// <summary>
    /// BITMAPINFOHEADER（wingdi.h）。字段顺序与原生逐字节一致；CreateDIBSection 的入参在原生侧是
    /// BITMAPINFO*，而 32bpp BI_RGB 没有颜色表，BITMAPINFOHEADER* 与它前缀等价（标准互操作技巧，
    /// 因此不必再复制一个把本结构包起来的 BITMAPINFO）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;   // biHeight &lt; 0 = top-down（第一行就是屏幕那侧的第一行，与 WPF 一致）
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    public const uint Srccopy = 0x00CC0020;        // 直接拷像素（不做光栅三元运算合成）
    public const uint CaptureBlt = 0x4000_0000;    // CAPTUREBLT：把分层窗口/DWM 合成层一并抓进来
    public const uint DibRgbColor = 0;             // CreateDIBSection 的 iUsage：不索引调色板
    public const uint BiRgb = 0;                   // 未压缩 BGRA 位图

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
    public const uint ShgfiIcon = 0x100, ShgfiPidl = 0x8, ShgfiLargeIcon = 0x0, ShgfiSmallIcon = 0x1;
    /// <summary>SHGetFileInfo(SHGFI_ICON) 的"大图标"= SM_CXICON（本机 32px）。T6 里它只做末位兜底。</summary>
    public const uint ShgfiIconLarge = ShgfiIcon | ShgfiLargeIcon;

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

    // v3 T6 一层：请求 w×h 时返回资源里**最接近**的帧（含 256px 的 PNG 帧）。
    // 【绝不猜序号】本机实测 shell32 的这个名字解析不到（EntryPointNotFoundException），
    // 而按序号调用陌生函数 = 用错签名调用别的东西 = 进程级风险（探针里 #152 就返回了指针样的垃圾）。
    // 所以这里只按名声明，由 IconExtractor 捕获 EntryPointNotFoundException 后**永久停用本层**并降级。
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int PrivateExtractIconsW(string file, int index, int width, int height,
        IntPtr[]? hIcons, int[]? iconIds, int count, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHParseDisplayName(string name, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attr, ref SHFILEINFOW psfi, uint cb, uint flags);

    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr pv);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

    // HBITMAP → 保 alpha 的 BitmapSource 需要读位图的宽/高/位深与像素指针
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObjectW(IntPtr h, int size, ref BITMAP bm);

    // ---------- IShellItemImageFactory（**默认关闭**的可选层，v3 T6 第 4 层） ----------
    // 仓库历史与 THIRD-PARTY-NOTICES 记录：本机（Win11 VM）对任意路径/尺寸组合调用 GetImage 都触发
    // AccessViolationException。AV 在 .NET 8 属于"损坏状态异常"，try/catch **抓不到**，因此这一层
    // 无法在进程内做到绝对安全 —— 唯一可行口径是"默认关闭 + 显式环境变量开启"（BEAUTIFUL_ICON_SHELLIMAGE=1），
    // 详见 IconExtractor.TryShellItemImage。
    [StructLayout(LayoutKind.Sequential)]
    public struct PSIZE
    {
        public int cx, cy;
    }

    /// <summary>GetImage 的标志：允许返回比请求更大的图（拿到真实 logo 而不是被缩小过的缩略图）。</summary>
    public const uint SiigbfBiggerSizeOk = 0x10;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        /// <summary>成功时输出一个 DIB 段 HBITMAP（**调用方负责 DeleteObject**）。</summary>
        void GetImage(PSIZE size, uint flags, out IntPtr phbm);
    }

    private static readonly Guid IidShellItem = new("43826d1e-eeca-4d1c-a512-efe8d4d8f783");

    /// <summary>
    /// 按 shell 命名空间路径（<c>shell:AppsFolder\AUMID</c>、<c>::{clsid}</c>、普通文件路径）绑定 IShellItem，
    /// 再 QI 成 <see cref="IShellItemImageFactory"/>。失败返回 null（抛出的 COM 异常由调用方兜住）。
    /// </summary>
    public static IShellItemImageFactory? BindShellItemImageFactory(string parsingName)
    {
        var iid = IidShellItem;
        IntPtr punk;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out punk);
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
        if (punk == IntPtr.Zero) return null;
        try
        {
            // IShellItem → IShellItemImageFactory 走标准 COM QI（Marshal 负责引用计数，
            // 不自己 P/Invoke ole32 的导出函数：那条路径在本仓库不可验证，错了就是难查的悬挂指针）。
            var target = typeof(IShellItemImageFactory).GUID;
            var hr = Marshal.QueryInterface(punk, ref target, out var factory);
            Marshal.Release(punk);
            if (hr != 0 || factory == IntPtr.Zero) return null;
            var obj = Marshal.GetObjectForIUnknown(factory);
            Marshal.Release(factory);
            return obj as IShellItemImageFactory;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Marshal.Release(punk);
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid, out IntPtr ppv);

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
