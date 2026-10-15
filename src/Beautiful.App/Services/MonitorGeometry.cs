using System.Globalization;
using System.Runtime.InteropServices;
using Beautiful.App.Interop;
using Beautiful.Core.Layout;

namespace Beautiful.App.Services;

/// <summary>
/// 一条 surface（顶栏 / Dock）在它**实际所在显示器**上的目标几何，全部物理像素。
/// </summary>
/// <param name="Monitor">显示器句柄（日志里按十六进制打印，多屏排障时用来确认选中的是哪一块）。</param>
/// <param name="MonitorRect">该显示器的 <c>rcMonitor</c>（全屏矩形，不是 rcWork——贴边要用真边缘）。</param>
/// <param name="DpiScale">该显示器的有效 DPI 缩放（= MDT_EFFECTIVE_DPI / 96）。</param>
/// <param name="Reserve">期望的 AppBar 预留带矩形（上报 <c>ABM_QUERYPOS</c> 的输入；宽度 = 该屏全宽）。</param>
/// <param name="Window">期望的真实窗口矩形 = 预留带 + 越界生长余量（<c>SetWindowPos</c> 的输入）。</param>
public readonly record struct StripTarget(
    IntPtr Monitor,
    PhysicalRect MonitorRect,
    double DpiScale,
    PhysicalRect Reserve,
    PhysicalRect Window);

/// <summary>设置面板下拉用的一个显示器选项（Value 即 AppSettings 的 DockMonitor/TopBarMonitor 串）。</summary>
/// <param name="Value">选择器串：<c>auto</c> / <c>primary</c> / 十进制序号（1 起）。</param>
/// <param name="Label">人读标签（含该屏真实分辨率与原点，序号含义一眼可核）。</param>
public readonly record struct MonitorOption(string Value, string Label);

/// <summary>
/// 一块显示器的两块矩形与该屏缩放（物理像素），见 <see cref="MonitorGeometry.AreaForPoint"/>。
/// </summary>
/// <param name="Monitor">全屏矩形 <c>rcMonitor</c>。</param>
/// <param name="Work">工作区矩形 <c>rcWork</c>（扣掉任务栏与已注册 AppBar）。</param>
/// <param name="DpiScale">该显示器有效 DPI 缩放（= MDT_EFFECTIVE_DPI / 96）。</param>
public readonly record struct MonitorArea(PhysicalRect Monitor, PhysicalRect Work, double DpiScale);

/// <summary>
/// 每显示器几何适配层：Win32 取值（显示器句柄 / <c>rcMonitor</c> / 有效 DPI）全部集中在这里，
/// 算术一律走 <see cref="StripMath"/>（Core 纯函数，可单测）。
/// <para>
/// 【为什么存在（v3 T1）】v2 的顶栏与 Dock 用 <c>GetSystemMetrics(SM_CXSCREEN/SM_CYSCREEN)</c>
/// 算条带——那**只有主屏**。后果在两处：① 副屏上的条带宽度与贴边按主屏尺寸算，必然错位；
/// ② 高 DPI 副屏用窗口初置 DPI 缩放，DIP→物理换算差一个倍数，于是"顶栏上方露一截壁纸"。
/// 本类之后，几何的唯一来源是窗口所在显示器的 <c>rcMonitor</c> 与该屏的
/// <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>；<c>GetSystemMetrics</c> 在这里一次都不出现。
/// </para>
/// <para>
/// 【显示器选择器（设置项 <c>DockMonitor</c>/<c>TopBarMonitor</c>）】
/// <c>auto</c> = 启动时光标所在显示器（解析结果按进程粘住，见 <c>_autoMonitor</c> 注释）、
/// <c>primary</c> = 带主屏标志的那一块、其余按 <see cref="Options"/> 列出的序号（1 起）。
/// 任何解析失败都往 <c>auto</c> 收敛，最终兜到"窗口所在屏 → 首块枚举屏"，宁可让条带待在一块
/// 合理的屏上，也不返回一个猜出来的屏幕尺寸。
/// </para>
/// </summary>
public static class MonitorGeometry
{
    private const string AutoSelector = "auto";
    private const string PrimarySelector = "primary";

    // 枚举回调委托放静态字段：跨 P/Invoke 期间临时委托可能被回收（与 WinEventDelegate /
    // LowLevelKeyboardHook 同一套纪律，回调发生在非托管栈上，回收即崩）。
    private static readonly NativeMethods.MonitorEnumProc CollectProc = Collect;

    // "auto" 的解析结果按进程留存：语义是"**启动时**光标所在显示器"，不是"此刻光标在哪屏"。
    // 每次重排都重读光标的话，拖图标尺寸滑杆（→ ScheduleReposition → 本方法）期间只要鼠标
    // 划过副屏，Dock 就整条跳到副屏上。显示器被拔掉/拓扑变化后句柄失效，
    // TryInfo 返回 false → 这里自然重新解析（等效于 WM_DISPLAYCHANGE 后重新跟随光标），
    // 因此不需要额外的失效通知接口。
    private static IntPtr _autoMonitor;

    /// <summary>
    /// 求一条边向条带的目标几何。<b>返回 null 表示当前取不到任何显示器</b>（拔屏瞬间/远程会话），
    /// 调用方须跳过本次定位、保持上次几何——这里没有任何"主屏尺寸"可兜底，也不该有。
    /// </summary>
    /// <param name="hwnd">surface 自身窗口句柄（决定 auto 回退时的所在屏与 DPI 兜底）。</param>
    /// <param name="edge">停靠边（<see cref="StripEdge.Top"/> / <see cref="StripEdge.Bottom"/>）。</param>
    /// <param name="panelDip">可见面板高（DIP）——这是上报给 shell 的预留高度。</param>
    /// <param name="headroomDip">越界生长余量（DIP）：Dock 传放大 headroom，顶栏传 0。</param>
    /// <param name="selector">显示器选择器串（null/空 = auto）。</param>
    public static StripTarget? ForEdge(
        IntPtr hwnd, StripEdge edge, double panelDip, double headroomDip, string? selector)
    {
        var monitor = ResolveMonitorHandle(hwnd, selector);
        if (!TryInfo(monitor, out var info))
        {
            Logger.Warn($"条带几何不可用 hwnd=0x{hwnd.ToInt64():X} monitor=0x{monitor.ToInt64():X} " +
                        "取不到 rcMonitor（显示器刚被拔出？），本次定位跳过并保持上次几何");
            return null;
        }
        var monitorRect = ToCore(in info.rcMonitor);
        var scale = ScaleOf(monitor, hwnd);
        var (reserve, window) = StripMath.Band(monitorRect, panelDip, headroomDip, scale, edge);
        return new StripTarget(monitor, monitorRect, scale, reserve, window);
    }

    /// <summary>
    /// 某窗口所在显示器的下沿（物理 px）。供 <see cref="TaskbarManager"/> 判"任务栏矩形在不在自己
    /// 那块屏内"以及把副屏任务栏推出**它自己的**屏幕——v2 用 SM_CYSCREEN（主屏底边），
    /// 于是副屏 <c>Shell_SecondaryTrayWnd</c> 被摆到主屏那一行上（README 已知限制 5(d)/7）。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="bottom">输出：该屏 <c>rcMonitor.Bottom</c>；失败时为 0，调用方不得采用。</param>
    /// <returns>取到返回 true。</returns>
    public static bool TryMonitorBottom(IntPtr hwnd, out int bottom)
    {
        bottom = 0;
        var monitor = hwnd != IntPtr.Zero
            ? NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest)
            : IntPtr.Zero;
        if (!TryInfo(monitor, out var info)) return false;
        bottom = info.rcMonitor.Bottom;
        return true;
    }

    /// <summary>
    /// 某个物理像素点**所在**显示器的两块矩形与该屏缩放（v3 T2 抓屏背板用）。
    /// <para>
    /// 为什么暴露两个矩形而不是一个：抓屏区要钳到 <see cref="MonitorArea.Work"/>（不把系统任务栏/
    /// 其他 AppBar 的像素糊进背板），但面板本身可能合法地压在工作区之外（顶栏下方 8px 留白、
    /// 弹层贴边翻转的边界情形）——那时钳工作区会**抓不到面板自己的区域**，画刷就会露出未绘制空洞
    /// （分层窗口 = 透明 → 看穿到桌面，等于回到 v2 的故障形态）。调用方据此回退到整屏
    /// <see cref="MonitorArea.Monitor"/>：宁可背板里混一条任务栏，也不让卡片缺一块。
    /// </para>
    /// </summary>
    /// <param name="x">锚点物理 px X（屏幕坐标）。</param>
    /// <param name="y">锚点物理 px Y。</param>
    /// <returns>取不到任何显示器时返回 null（拔屏瞬间/远程会话），调用方必须放弃本次抓屏。</returns>
    public static MonitorArea? AreaForPoint(int x, int y)
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y },
                                                     NativeMethods.MonitorDefaultToNearest);
        if (!TryInfo(monitor, out var info)) return null;
        return new MonitorArea(ToCore(in info.rcMonitor), ToCore(in info.rcWork), ScaleOf(monitor, IntPtr.Zero));
    }

    /// <summary>
    /// 列出可选显示器（设置面板下拉）：auto、primary，之后是枚举到的每一块屏及其真实分辨率与原点。
    /// 序号按<strong>本次枚举</strong>的次序（EnumDisplayMonitors 的顺序由显示驱动决定），
    /// 所以标签里必须带上分辨率/原点，用户才能确认"显示器 2"是哪一块。
    /// </summary>
    public static IReadOnlyList<MonitorOption> Options()
    {
        var list = new List<MonitorOption>
        {
            new(AutoSelector, "自动（启动时光标所在显示器）"),
            new(PrimarySelector, "主显示器")
        };
        var monitors = EnumerateMonitors();
        var primary = PrimaryMonitorHandle();
        for (var i = 0; i < monitors.Count; i++)
        {
            if (!TryInfo(monitors[i], out var info)) continue;
            var r = info.rcMonitor;
            list.Add(new MonitorOption(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                $"显示器 {i + 1}｜{r.Right - r.Left}×{r.Bottom - r.Top} @ ({r.Left},{r.Top})" +
                (monitors[i] == primary ? "｜主屏" : "")));
        }
        return list;
    }

    // 两个 RECT 转换器是 internal：NativeMethods 本身是 internal P/Invoke 汇总，公开类型不能
    // 在签名里暴露它（CS0050/CS0051）。跨层传递的几何一律用 Core 的 PhysicalRect。

    /// <summary>Win32 <c>RECT</c> → Core 物理矩形（几何算术只在 Core 的类型上做，保证可单测）。</summary>
    internal static PhysicalRect ToCore(in NativeMethods.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>Core 物理矩形 → Win32 <c>RECT</c>（<c>APPBARDATA.rc</c> / <c>SetWindowPos</c> 用）。</summary>
    internal static NativeMethods.RECT ToNative(PhysicalRect r)
        => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };

    // ---------- 内部 ----------

    /// <summary>按选择器串解析显示器句柄；任何失败都往 auto 收敛（见类型注释）。</summary>
    private static IntPtr ResolveMonitorHandle(IntPtr hwnd, string? selector)
    {
        var s = (selector ?? AutoSelector).Trim().ToLowerInvariant();
        if (s.Length == 0) s = AutoSelector;

        if (s != AutoSelector && s != PrimarySelector)
        {
            // 序号形（"1"/"2"…）。AppSettings.Normalized 已保证它是 1..MaxMonitorIndex 的十进制串，
            // 但"在屏几块"只有运行时知道：写了个不存在的台数就按 auto 解析并留日志（手改 JSON 的路径）。
            if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                var monitors = EnumerateMonitors();
                if (index >= 1 && index <= monitors.Count) return monitors[index - 1];
                Logger.Warn($"显示器序号 {index} 超出在屏显示器数 {monitors.Count}，按 auto 解析");
            }
            else
            {
                Logger.Warn($"显示器选择器 \"{selector}\" 无法识别，按 auto 解析");
            }
            s = AutoSelector;
        }

        if (s == PrimarySelector)
        {
            var primary = PrimaryMonitorHandle();
            if (primary != IntPtr.Zero) return primary;
            Logger.Warn("未找到带主屏标志的显示器，按 auto 解析");   // 远程会话/极端拓扑：落到下面的兜底链
        }

        if (_autoMonitor != IntPtr.Zero && TryInfo(_autoMonitor, out _)) return _autoMonitor;

        var resolved = MonitorFromCursor();
        if (resolved == IntPtr.Zero && hwnd != IntPtr.Zero)
            resolved = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        if (resolved == IntPtr.Zero)
        {
            var monitors = EnumerateMonitors();
            if (monitors.Count > 0) resolved = monitors[0];
        }
        if (resolved != IntPtr.Zero) _autoMonitor = resolved;
        return resolved;
    }

    private static IntPtr MonitorFromCursor()
        => NativeMethods.GetCursorPos(out var pt)
            ? NativeMethods.MonitorFromPoint(pt, NativeMethods.MonitorDefaultToNearest)
            : IntPtr.Zero;

    /// <summary>主显示器句柄（dwFlags 含 MONITORINFOF_PRIMARY）；没有则 0。</summary>
    private static IntPtr PrimaryMonitorHandle()
    {
        foreach (var h in EnumerateMonitors())
        {
            if (TryInfo(h, out var info) && (info.dwFlags & NativeMethods.MonitorinfofPrimary) != 0)
                return h;
        }
        return IntPtr.Zero;
    }

    private static List<IntPtr> EnumerateMonitors()
    {
        var list = new List<IntPtr>();
        var gc = GCHandle.Alloc(list);
        try
        {
            // 第一个参数是 HDC：传 NULL 表示整个虚拟桌面；回调里同时拿到每屏矩形，但只留句柄
            // （矩形随时由 TryInfo 重读，避免缓存过期数据）。
            NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, CollectProc, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }
        return list;
    }

    private static bool Collect(IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rcMonitor, IntPtr dwData)
    {
        if (GCHandle.FromIntPtr(dwData).Target is List<IntPtr> list) list.Add(hMonitor);
        return true; // true = 继续枚举
    }

    private static bool TryInfo(IntPtr monitor, out NativeMethods.MONITORINFO info)
    {
        info = default;
        if (monitor == IntPtr.Zero) return false;
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi)) return false;
        info = mi;
        return true;
    }

    /// <summary>
    /// 该显示器的有效 DPI 缩放，降级链与 FluentPopupHost.ScaleOfAnchor 逐字一致：
    /// <c>GetDpiForMonitor(MDT_EFFECTIVE_DPI)</c>（返回 0 = S_OK 且 dpiX&gt;0）
    /// → <c>GetDpiForWindow</c>（shcore 不可用时，单屏主 DPI 场景等价）→ 1.0。
    /// <para>
    /// 为什么不用 <c>GetDpiForWindow</c> 为准：混合 DPI 多屏时，刚 ABM_NEW 还没落位的窗口返回的是
    /// **初置屏**的 DPI，副屏 150% 下按它换算就把条带算成 2/3 高（FluentPopupHost 评审 Important#1
    /// 踩过的同一坑）。
    /// </para>
    /// </summary>
    private static double ScaleOf(IntPtr monitor, IntPtr fallbackHwnd)
    {
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MdtEffectiveDpi, out uint dpiX, out _) == 0 &&
            dpiX > 0)
            return dpiX / 96.0;
        try
        {
            if (fallbackHwnd != IntPtr.Zero)
            {
                var dpi = NativeMethods.GetDpiForWindow(fallbackHwnd);
                if (dpi > 0) return dpi / 96.0;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"显示器 DPI 取值降级异常: {ex.Message}");
        }
        return 1.0;
    }
}
