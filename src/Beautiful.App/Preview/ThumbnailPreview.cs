using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using static Beautiful.App.Interop.NativeMethods;
// UseWindowsForms 把 System.Drawing 引入全局命名空间，与 WPF 的 Rect/Point/Size 同名 → 显式取 WPF 版本
// （与 DockSurface / DockWindow 同一手法）。原生结构一律以 NativeMethods.XXX 限定，不与这些别名冲突。
using Rect = System.Windows.Rect;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;   // 与 DockWindow 同法：System.Drawing.Color 同名，显式取 WPF 版
using Application = System.Windows.Application;   // 与 System.Windows.Forms.Application 同名歧义（UseWindowsForms）

namespace Beautiful.App.Preview;

/// <summary>
/// DWM 实时窗口缩略图预览（Task 13）。
/// <para>
/// <b>为什么目标窗口是纯 Win32 宿主而非 WPF 窗口（airspace 规则）</b>：DWM 把源窗口的实时画面合成进
/// <b>目标窗口客户区坐标系的 <c>rcDestination</c> 矩形</b>，且是在窗口自身内容之上覆盖绘制。WPF 窗口的
/// 呈现面（HwndSource 的重定向表面）铺满整个客户区 → 缩略图会被 WPF 内容盖住（airspace 冲突）。故目标
/// 必须是一个客户区专门用来承载缩略图的、非分层的 Win32 弹层窗口（<see cref="EnsureHost"/> 建的类
/// "BeautifulThumbHost"，WS_POPUP + WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE + CS_DROPSHADOW）。卡片的外观
/// （底色 / 标题带 / 细边框）由 GDI 在 <c>WM_PAINT</c> 里画在缩略图矩形<b>之外</b>的留白环，圆角交给
/// <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>——都不会压在实时画面之上。
/// </para>
/// <para>
/// <b>生命周期与释放矩阵（首要风险：驻留进程的缩略图句柄泄漏）</b>：<c>DwmRegisterThumbnail</c> 返回的
/// 句柄不是随进程自动回收的普通引用，必须逐一 <c>DwmUnregisterThumbnail</c>。全类只保留<b>至多一个</b>
/// <see cref="_thumb"/>（复用同一宿主窗口，一次只预览一个源），不变量：<c>_thumb != Zero</c> ⇔ 已注册。
/// 释放路径全覆盖——① <see cref="Show"/> 重入先注销旧的；② 注册后任何一步失败（查尺寸 / 更新属性）就地
/// 注销并隐藏；③ <see cref="Hide"/> 必注销；④ <see cref="Dispose"/> 经 <see cref="Hide"/> 注销后销毁宿主
/// 窗口。因此"注册次数 == 注销次数"，句柄数不随悬停次数单调上涨（见 task-13 报告的实测计数）。
/// </para>
/// <para>
/// <b>"注册成功"不等于"屏上看得见"（v3.1 缺陷 2 的教训）</b>：DWM 缩略图只在宿主的<b>玻璃像素</b>上显形。
/// v3 整体退役 <c>DwmExtendFrameIntoClientArea</c> 时把这个宿主一起带走了，于是注册、查尺寸、写属性
/// 三步 HRESULT 全 0，屏幕上却什么都不画；叠加类刷是 <c>BLACK_BRUSH</c>，用户看到的就是一只实心黑框
/// （"鼠标放在 dock 图标上方无法预览窗口，整个黑框"）。v3.1 起 <see cref="EnsureHost"/> 显式把整块客户区
/// 开成玻璃（margins = -1，理由与豁免范围见 <see cref="NativeMethods.DwmExtendFrameIntoClientArea"/>），
/// 并把类刷换成主题卡底色。<b>不变量</b>：宿主玻璃就绪 ⇔ 才允许弹预览；玻璃开不成（无桌面合成 / 驱动不支持）
/// 就整张卡片不出现并只记一条日志——宁可没有预览，也不给屏幕留黑洞。
/// </para>
/// <para>
/// <b>源窗口消失 / 最小化</b>：安装时不新增全局钩子（brief 约束）。仅在可见期间跑一个 ~220ms 的
/// <see cref="DispatcherTimer"/> 做有界轮询：<c>!IsWindow(src)</c> 或 <c>IsIconic(src)</c> → 立即隐藏；
/// 否则重查源尺寸，变化则 <see cref="UpdateSourceSize"/>（源窗口被拖动改变大小即时跟新）。空闲零开销。
/// </para>
/// </summary>
public sealed class ThumbnailPreview : IDisposable
{
    // ---------- 视觉常量（DIP，随 DpiScale 换算成物理像素） ----------
    private const int PadDip = 10;      // 缩略图四周留白环（边框/圆角可视区）
    private const int TitleDip = 26;    // 顶部标题带高度（画在缩略图之上，不被实时画面覆盖）
    private const int GapDip = 8;       // 预览卡片与锚点之间的竖直间隙

    // 复用单宿主：本进程同一时刻只存在一个 ThumbnailPreview（DockWindow 持有；重载 Dock 时先关旧再建新），
    // 且其一次只显示一个缩略图 → 生命周期简单、泄漏面可控。窗口类只需注册一次（进程级）。
    private const string ClassName = "BeautifulThumbHost";
    private const uint WmEraseBkgnd = 0x0014;

    private static readonly Dictionary<IntPtr, ThumbnailPreview> Instances = new();
    private static ushort _classAtom;
    // WndProc 委托必须长期持有（静态字段）：否则被 GC 后消息打到已回收的桩 → 随机原生崩溃。
    private static WndProcDelegate? _classProc;

    private readonly Dispatcher _dispatcher;
    private IntPtr _hwnd;                 // 宿主窗口（首次 Show 惰性创建并复用）
    private IntPtr _thumb;                // 当前缩略图句柄（Zero = 未注册）
    private IntPtr _source;               // 当前源窗口句柄（Zero = 无）
    private DispatcherTimer? _watchd;     // 仅可见期间运行
    // 宿主玻璃层是否真的开起来了（见类型注释的不变量）。false ⇒ 一次都不弹卡片，绝不显示空黑框。
    private bool _hostGlassReady;
    // 不可用成因（玻璃开不成 / 注册失败）只把详情写进日志一次（悬停会反复重试，逐次 Warn 只会刷满日志）。
    private bool _unavailableLogged;

    // 供 WndProc 的 GDI 绘制读取（每次 Show/主题变更后刷新）
    private string _title = string.Empty;
    private int _padPx = 10, _titlePx = 26;
    private uint _bgRef, _accentRef, _textRef, _bandRef;

    /// <summary>由宿主（DockWindow）在 <see cref="Show"/> 前设置的 DIP→物理 缩放比（预览恒落在 Dock 所在显示器）。</summary>
    public double DpiScale { get; set; } = 1.0;

    /// <summary>是否处于已注册缩略图的状态（供外部判定 / 诊断）。</summary>
    public bool IsShowing => _thumb != IntPtr.Zero;

    /// <summary>当前已注册句柄数（0 或 1）；泄漏断言用——同一时刻至多一个。</summary>
    public int RegisteredCount => _thumb == IntPtr.Zero ? 0 : 1;

    public ThumbnailPreview()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        // 初始配色（无主题时兜底），Show 时再按主题资源刷新。
        _bgRef = 0x00202020; _bandRef = 0x002B2B2B; _accentRef = 0x00F7590A; _textRef = 0x00FFFFFF;
    }

    // ---------- 对外 API ----------

    /// <summary>
    /// 在锚点（屏幕 DIP 矩形，通常是 Dock 条目当前放大后的绘制矩形）上方居中弹出 <paramref name="sourceHwnd"/>
    /// 的实时缩略图；尺寸按 <see cref="DwmQueryThumbnailSourceSize"/> 的源宽高做等比适配并钳到工作区。
    /// 失败（句柄无效 / 注册失败 / 源已消失）一律回到隐藏态且不遗留任何注册句柄。
    /// </summary>
    public void Show(IntPtr sourceHwnd, Rect anchorScreenDip, double maxW, double maxH)
    {
        _dispatcher.VerifyAccess();

        // 释放矩阵 ①：重入（上一张还在显示）先彻底注销，保证"注册/注销"配平。
        HideInternal();

        if (sourceHwnd == IntPtr.Zero || !IsWindow(sourceHwnd) || IsIconic(sourceHwnd))
            return;

        EnsureHost();
        if (_hwnd == IntPtr.Zero) return;
        // 玻璃没开成的宿主 = 缩略图永远不可能显形（HRESULT 照样全 0，屏幕上一个像素都不画）。
        // 这种情况直接把预览判定为"本次运行不可用"：不弹卡片，宁可没有预览也不给屏幕留一只黑框。
        if (!_hostGlassReady)
        {
            LogUnavailableOnce($"预览宿主玻璃层未就绪（DwmExtendFrameIntoClientArea 未成功），已跳过弹卡片 src=0x{sourceHwnd.ToInt64():X}");
            return;
        }

        RefreshThemeColors();   // 每次弹出重读主题色（换主题后下次悬停即生效）

        var hr = DwmRegisterThumbnail(_hwnd, sourceHwnd, out var thumb);
        if (hr != 0 || thumb == IntPtr.Zero)
        {
            // 失败即"绝不留下卡片"：宿主保持隐藏（下面这句是防御性重复，确保任何调用序下屏上都没有半张卡）。
            LogUnavailableOnce($"DwmRegisterThumbnail 失败 hr=0x{hr:X8} src=0x{sourceHwnd.ToInt64():X}");
            ShowWindow(_hwnd, SwHide);
            return;
        }
        _thumb = thumb;      // 自此 _thumb 非空——下面任何失败路径都必须经 HideInternal 注销
        _source = sourceHwnd;
        Logger.Info($"DWM 缩略图注册 src=0x{sourceHwnd.ToInt64():X} thumb=0x{thumb.ToInt64():X} dst=0x{_hwnd.ToInt64():X}");

        if (!LayoutAndApply(maxW, maxH, anchorScreenDip))
            return;         // LayoutAndApply 内部失败已 HideInternal（注销 + 隐藏）

        ShowWindow(_hwnd, SwShowNoActivate);
        StartWatchdog();
    }

    /// <summary>隐藏预览并注销缩略图句柄（可安全重复调用）。</summary>
    public void Hide()
    {
        _dispatcher.VerifyAccess();
        HideInternal();
    }

    /// <summary>重新查询源窗口尺寸并即时刷新缩略图布局（源被拖动改变大小时由看门狗调用）。</summary>
    public void UpdateSourceSize()
    {
        if (_thumb == IntPtr.Zero || _source == IntPtr.Zero) return;
        if (!IsWindow(_source) || IsIconic(_source)) { HideInternal(); return; }
        // 复用 Show 时的锚点/上限重算：这里没有锚点参数，只按当前窗口客户区大小重贴缩略图矩形，
        // 保持宿主窗口尺寸不变（拖动力图避免抖动）——源比例变化时缩略图在既有画框内等比重算。
        ReapplyWithinCurrentFrame();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        HideInternal();               // 释放矩阵 ④：先注销缩略图
        if (_hwnd != IntPtr.Zero)
        {
            Instances.Remove(_hwnd);
            DestroyWindow(_hwnd);     // 销毁宿主窗口（缩略图已注销，随窗口销毁无残留）
            _hwnd = IntPtr.Zero;
        }
        _source = IntPtr.Zero;
    }

    /// <summary>按当前主题资源刷新卡片配色 / 字体度量（GDI 绘制读这几个值）。Show 时调用。</summary>
    private void RefreshThemeColors()
    {
        _bgRef = CardColorRef();   // 与窗口类刷同一取色函数：换主题后卡片底与"空卡底"始终是同一个色
        // 标题带：取底色与纯白之间一点亮度做分隔；文字用主题主文字色；强调条用 ThemeAccent。
        _textRef = ToGdi(ReadColor("ThemeTextPrimary", Colors.White));
        _accentRef = ToGdi(ReadColor("ThemeAccent", Color.FromRgb(0x0A, 0x59, 0xF7)));
        var bg = ReadColor("ThemeDockBackground", Color.FromRgb(0x20, 0x20, 0x20));
        _bandRef = ToGdi(Color.FromRgb(
            (byte)System.Math.Min(255, bg.R + 16),
            (byte)System.Math.Min(255, bg.G + 16),
            (byte)System.Math.Min(255, bg.B + 16)));
    }

    // ---------- 布局 / 更新 ----------

    /// <summary>查源尺寸 → 等比适配 → 计算并摆放宿主窗口 → 写 DWM 缩略图属性。任一步失败即 <see cref="HideInternal"/> 并返回 false。</summary>
    private bool LayoutAndApply(double maxW, double maxH, Rect anchorDip)
    {
        var hr = DwmQueryThumbnailSourceSize(_thumb, out var size);
        if (hr != 0 || size.cx <= 0 || size.cy <= 0)
        {
            Logger.Warn($"DwmQueryThumbnailSourceSize 失败 hr=0x{hr:X8}");
            HideInternal();
            return false;
        }
        return ApplySize(size.cx, size.cy, maxW, maxH, anchorDip);
    }

    private bool ApplySize(int srcW, int srcH, double maxW, double maxH, Rect anchorDip)
    {
        var dpi = DpiScale <= 0 ? 1.0 : DpiScale;
        _padPx = (int)System.Math.Round(PadDip * dpi);
        _titlePx = (int)System.Math.Round(TitleDip * dpi);

        // 可用画框（物理像素）= 上限 - 留白环 - 标题带。
        var availW = (int)System.Math.Round(maxW * dpi) - 2 * _padPx;
        var availH = (int)System.Math.Round(maxH * dpi) - _titlePx - 2 * _padPx;
        if (availW <= 0 || availH <= 0 || srcW <= 0 || srcH <= 0)
        {
            HideInternal();
            return false;
        }

        // 等比适配（fit-inside，允许放大铺满画框）。
        var scale = System.Math.Min((double)availW / srcW, (double)availH / srcH);
        var thumbW = System.Math.Max(1, (int)System.Math.Round(srcW * scale));
        var thumbH = System.Math.Max(1, (int)System.Math.Round(srcH * scale));

        var clientW = thumbW + 2 * _padPx;
        var clientH = _titlePx + thumbH + _padPx;

        // 物理锚点 + 目标屏幕物理像素定位。
        var ax = (int)System.Math.Round(anchorDip.X * dpi);
        var ay = (int)System.Math.Round(anchorDip.Y * dpi);
        var aw = (int)System.Math.Round(anchorDip.Width * dpi);
        var ah = (int)System.Math.Round(anchorDip.Height * dpi);
        var gap = (int)System.Math.Round(GapDip * dpi);

        // 工作区（锚点中心所在显示器）用于钳制。
        var center = new POINT { X = ax + aw / 2, Y = ay + ah / 2 };
        var mon = MonitorFromPoint(center, MonitorDefaultToNearest);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        RECT work = mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi) ? mi.rcWork
                    : new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };

        var x = ax + aw / 2 - clientW / 2;                       // 水平居中对齐锚点
        x = System.Math.Clamp(x, work.Left, System.Math.Max(work.Left, work.Right - clientW));
        var y = ay - gap - clientH;                              // 默认置于锚点上方
        if (y < work.Top) y = ay + ah + gap;                     // 上方放不下 → 翻到锚点下方
        y = System.Math.Clamp(y, work.Top, System.Math.Max(work.Top, work.Bottom - clientH));

        SetWindowPos(_hwnd, HwndTopmost, x, y, clientW, clientH,
            SwpNoactivate);          // 置顶但不夺焦点（预览浮在应用之上、点 Dock 时不激活自己）

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmtRectDestination | DwmtVisible | DwmtOpacity | DwmtSourceClientAreaOnly,
            rcDestination = new RECT { Left = _padPx, Top = _titlePx, Right = _padPx + thumbW, Bottom = _titlePx + thumbH },
            rcSource = default,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true
        };
        var hr = DwmUpdateThumbnailProperties(_thumb, ref props);
        if (hr != 0)
        {
            Logger.Warn($"DwmUpdateThumbnailProperties 失败 hr=0x{hr:X8}");
            HideInternal();
            return false;
        }
        InvalidateRect(_hwnd, IntPtr.Zero, false);   // 触发 WM_PAINT 重画卡片底色/标题（新尺寸下）
        return true;
    }

    /// <summary>源尺寸变化但保持宿主窗口客户区不变，仅在既有画框内按新源比例重贴缩略图矩形。</summary>
    private void ReapplyWithinCurrentFrame()
    {
        var hr = DwmQueryThumbnailSourceSize(_thumb, out var size);
        if (hr != 0 || size.cx <= 0 || size.cy <= 0) return;
        if (!GetClientRect(_hwnd, out var client)) return;

        var availW = client.Right - client.Left - 2 * _padPx;
        var availH = client.Bottom - client.Top - _titlePx - _padPx;
        if (availW <= 0 || availH <= 0) return;

        var scale = System.Math.Min((double)availW / size.cx, (double)availH / size.cy);
        var thumbW = System.Math.Max(1, (int)System.Math.Round(size.cx * scale));
        var thumbH = System.Math.Max(1, (int)System.Math.Round(size.cy * scale));

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmtRectDestination | DwmtVisible | DwmtOpacity | DwmtSourceClientAreaOnly,
            rcDestination = new RECT { Left = _padPx, Top = _titlePx, Right = _padPx + thumbW, Bottom = _titlePx + thumbH },
            rcSource = default,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true
        };
        if (DwmUpdateThumbnailProperties(_thumb, ref props) == 0)
            InvalidateRect(_hwnd, IntPtr.Zero, false);
    }

    // ---------- 有界轮询 ----------

    private void StartWatchdog()
    {
        if (_watchd is null)
        {
            _watchd = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };
            _watchd.Tick += OnWatchdogTick;
        }
        _watchd.Stop();
        _watchd.Start();
    }

    private void StopWatchdog() => _watchd?.Stop();

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        if (_thumb == IntPtr.Zero) { StopWatchdog(); return; }
        // 源消失 / 最小化 → 立即关闭预览（无泄漏：HideInternal 必注销）。
        if (!IsWindow(_source) || IsIconic(_source)) { HideInternal(); return; }
        UpdateSourceSize();
    }

    // ---------- 释放矩阵核心 ----------

    private void HideInternal()
    {
        StopWatchdog();
        if (_thumb != IntPtr.Zero)
        {
            var old = _thumb;
            var hr = DwmUnregisterThumbnail(_thumb);
            Logger.Info($"DWM 缩略图注销 thumb=0x{old.ToInt64():X} hr=0x{hr:X8}");
            if (hr != 0) Logger.Warn($"DwmUnregisterThumbnail 返回 hr=0x{hr:X8}（句柄仍视为已释放）");
            _thumb = IntPtr.Zero;
        }
        _source = IntPtr.Zero;
        // 注意：不清 _title —— DockWindow 在调用 Show 前就写好标题，Show 内部首步 HideInternal 若清标题会把
        // 本次要显示的标题抹掉；标题每次 Set 覆盖，留旧值无害。
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SwHide);
    }

    // ---------- 宿主窗口 ----------

    private void EnsureHost()
    {
        if (_hwnd != IntPtr.Zero) return;
        EnsureClass();
        var exStyle = (uint)(WsExToolwindow | WsExNoactivate); // 常量已合并为唯一 long 定义（终审 M-2）
        _hwnd = CreateWindowExW(exStyle, ClassName, null, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Logger.Warn("预览宿主窗口创建失败");
            return;
        }
        Instances[_hwnd] = this;
        // Win11 原生圆角（背板无关，非分层窗口同样生效）：把窗口裁成系统圆角，卡片四角不再方。
        var round = DwmwcpRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        // 【缺陷 2 的正解】整块客户区开成玻璃：DWM 缩略图只在玻璃像素上显形，没有这一步时注册与写属性
        // 都会"成功"，但屏幕上不画任何东西（v3 退役本 API 时把这个纯 Win32 宿主一起带走了，是本次回归）。
        // margins 四边 -1 = 全客户区玻璃；GDI 画的不透明标题带/留白环压在玻璃之上照旧可见。
        // 这一步失败（无桌面合成的环境，如本 VM）就把预览判为不可用：见 Show 的 _hostGlassReady 守卫。
        var glass = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
        var frameHr = DwmExtendFrameIntoClientArea(_hwnd, ref glass);
        _hostGlassReady = frameHr == 0;
        Logger.Info($"预览宿主玻璃层 hwnd=0x{_hwnd.ToInt64():X} DwmExtendFrameIntoClientArea(-1) hr=0x{frameHr:X8}" +
                    $" → {(frameHr == 0 ? "缩略图可显形" : "本环境开不出玻璃，预览不弹卡片（绝不留黑框）")}");
    }

    /// <summary>不可用成因只播第一条 Warn（悬停每次都重试，全量记只会把日志刷满而不增加信息）。</summary>
    private void LogUnavailableOnce(string reason)
    {
        if (_unavailableLogged) return;
        _unavailableLogged = true;
        Logger.Warn($"悬停缩略图预览不可用（已按失败即不显示处理，同类原因后续不再逐次记录）：{reason}");
    }

    private static void EnsureClass()
    {
        if (_classAtom != 0) return;
        _classProc = WndProc;   // 存静态字段防 GC
        // 类刷底色取主题卡底色（读不到主题时的兜底 #202020 与卡片同色），**不再是 BLACK_BRUSH**：
        // v3.0.0 用 GetStockObject(BLACK_BRUSH) 是为了"初绘不留白闪"，但一旦缩略图没能显形（缺玻璃），
        // 类刷就成了用户看到的整只黑框（缺陷 2）。黑永远不该是任何一个未填充状态的默认答案。
        // 该句柄随窗口类活到进程结束，故意不 DeleteObject（删了类刷就变成悬空指针）。
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CsDropShadow,
            lpfnWndProc = _classProc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = CreateSolidBrush(CardColorRef()),
            lpszMenuName = null,
            lpszClassName = ClassName,
            hIconSm = IntPtr.Zero
        };
        _classAtom = RegisterClassExW(ref wc);
        if (_classAtom == 0)
        {
            // 可能已注册（重载 Dock 复用同类名）：GetLastError==1414(ERROR_CLASS_ALREADY_EXISTS) 视为成功。
            // err 缓存进局部变量：判定与日志必须用同一个值（二次读取之间错误码可能被其他 P/Invoke 改写）。
            var err = Marshal.GetLastWin32Error();
            if (err != 1414)
                Logger.Warn($"RegisterClassExW 失败 err={err}");
            else
                _classAtom = 1;   // 已存在，占位标记避免重复注册（类名与过程一致，安全复用）
        }
    }

    // 静态 WndProc：按 hwnd 找到对应实例（单实例，但用字典键控更稳，创建期消息查不到实例即走默认处理）。
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmEraseBkgnd) return new IntPtr(1);   // 底色统一在 WM_PAINT 铺，避免二次擦除闪烁
        if (msg == WmPaint)
        {
            // 绘制异常绝不能外泄到原生消息栈——那会直接崩掉驻留进程（FillRect 曾踩过）。就地吞掉并降级。
            try { if (Instances.TryGetValue(hwnd, out var inst)) Paint(hwnd, inst); }
            catch (Exception ex) { Logger.Warn($"预览卡片绘制异常（已隔离）: {ex.Message}"); }
            return IntPtr.Zero;
        }
        if (msg == WmDestroy)
        {
            Instances.Remove(hwnd);
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>GDI 画卡片外观：整面主题底色 + 顶部标题带（文字 + 强调条）。实时缩略图由 DWM 覆盖中间区，
    /// 这里被盖住的部分无害——只有留白环与标题带会最终可见（airspace 安全）。</summary>
    private static void Paint(IntPtr hwnd, ThumbnailPreview inst)
    {
        var ps = Marshal.AllocHGlobal(128);   // PAINTSTRUCT 缓冲（x64 约 72 字节，给足余量）
        IntPtr hdc = IntPtr.Zero;
        try
        {
            hdc = BeginPaint(hwnd, ps);
            if (hdc == IntPtr.Zero) return;
            GetClientRect(hwnd, out var client);

            // 1) 整面底色
            PaintFill(hdc, client, inst._bgRef);

            // 2) 顶部标题带
            var band = new RECT { Left = 0, Top = 0, Right = client.Right, Bottom = inst._titlePx };
            PaintFill(hdc, band, inst._bandRef);

            // 3) 左侧强调竖条（在标题带左缘，纯装饰，落在缩略图矩形之外）
            var accentW = System.Math.Max(2, inst._padPx / 4);
            var bar = new RECT { Left = 0, Top = 0, Right = accentW, Bottom = inst._titlePx };
            PaintFill(hdc, bar, inst._accentRef);

            // 4) 标题文字
            var text = new RECT
            {
                Left = accentW + inst._padPx / 2,
                Top = 0,
                Right = System.Math.Max(accentW + inst._padPx / 2 + 1, client.Right - inst._padPx),
                Bottom = inst._titlePx
            };
            var font = CreateFontW((int)System.Math.Round(13 * (inst.DpiScale <= 0 ? 1 : inst.DpiScale)), 0, 0, 0,
                600 /*FW_SEMIBOLD*/, false, false, false,
                1 /*DEFAULT_CHARSET*/, 0, 0, 5 /*CLEARTYPE_QUALITY*/, 0, "Segoe UI");
            var old = SelectObject(hdc, font);
            try
            {
                SetBkMode(hdc, BkTransparent);   // 文字背景透明，避免在深色标题带上画出实心白块
                SetTextColor(hdc, inst._textRef);
                DrawTextW(hdc, inst._title, inst._title.Length, ref text, DtSingleLine | DtEndEllipsis);
            }
            finally
            {
                SelectObject(hdc, old);
                DeleteObject(font);   // 每次绘制用完即删：GDI 字体句柄配平，不随悬停累积
            }
        }
        finally
        {
            if (hdc != IntPtr.Zero) EndPaint(hwnd, ps);
            Marshal.FreeHGlobal(ps);
        }
    }

    private static void PaintFill(IntPtr hdc, RECT rect, uint colorRef)
    {
        var brush = CreateSolidBrush(colorRef);
        try { FillRect(hdc, ref rect, brush); }
        finally { DeleteObject(brush); }   // 用完即删，句柄配平
    }

    // ---------- 颜色 / 资源辅助 ----------

    /// <summary>WPF Color → GDI COLORREF（0x00BBGGRR）。</summary>
    private static uint ToGdi(Color c) => (uint)(c.B << 16 | c.G << 8 | c.R);

    /// <summary>
    /// 主题卡片底色（GDI 无 alpha，取 RGB）。<b>窗口类刷与 WM_PAINT 底色同源</b>：
    /// 这样"缩略图还没画上来"的那一小段时间里，屏上是一块与卡片同色的空卡，而不是黑洞。
    /// </summary>
    private static uint CardColorRef() => ToGdi(ReadColor("ThemeDockBackground", Color.FromRgb(0x20, 0x20, 0x20)));

    private static Color ReadColor(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b.Color;
        }
        catch
        {
            // 资源未合并 / 键缺失：回落兜底值即可，预览卡片不因取色失败而崩
        }
        return fallback;
    }

    /// <summary>设置标题带文字（由宿主在 Show 前写入条目显示名）。</summary>
    public string Title
    {
        get => _title;
        set { _title = value ?? string.Empty; if (_hwnd != IntPtr.Zero) InvalidateRect(_hwnd, IntPtr.Zero, false); }
    }
}
