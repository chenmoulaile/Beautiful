using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Animation;
using static Beautiful.App.Interop.NativeMethods;
using Rect = System.Windows.Rect;

namespace Beautiful.App.Animation;

/// <summary>
/// alpha.26 新引擎：**DWM 实况缩略图 genie**——替身不再是"抓帧位图"，而是 DWM 官方的
/// 实时缩略图通道本身（<c>DwmRegisterThumbnail</c>）。
/// <para>
/// 【为什么彻底换实现】alpha.25 的遮挡重播在真机（26H2）上全军覆没，日志给了三条铁证：
/// ① PrintWindow 对 GPU 合成窗口整幅纯色、屏幕 DC 退路也失败（<c>动画未播…因=抓帧不可用</c>）；
/// ② mesh 引擎起播前要做实心合成 + 网格构建，真机实测事件到手到起播迟到 ~500ms-1s，
/// 用户看到的是原生动画播完之后才来一个卡顿幽灵（P95=78ms/帧）；③ 而
/// <c>DWM 缩略图注册 … hr=0x00000000</c> 在同一台机器上一直成功——悬停预览用的就是它。
/// 结论：这台机器上唯一被证明可靠的实时画面通道就是 DWM 缩略图，动画引擎就该长在它上面。
/// </para>
/// <para>
/// 【做法】替身宿主是一只纯 Win32 弹层（与 <see cref="Preview.ThumbnailPreview"/> 同一模式：
/// WS_POPUP + WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE + 整客户区玻璃），把源窗口按水平切成
/// N 片注册成 N 张缩略图（<c>rcSource</c> = 源窗口的第 i 条），每帧用
/// <c>DwmUpdateThumbnailProperties</c> 写各片的 <c>rcDestination</c>——片间错相收拢
/// （<see cref="GenieMath"/> 的同一套纯函数几何）就是 genie 的弯曲感。
/// 像素永远由 DWM 合成器直接采样源窗口表面：<b>零抓帧、零位图分配、零预渲染</b>，
/// 起播延迟只剩"事件 → BeginInvoke"一跳；每帧 12 次属性写入是微秒级 RPC，不再有 mesh 的 78ms/帧。
/// 源窗口最小化后 DWM 仍合成它的最后一帧（任务栏缩略图同语义），所以"事件到手时已 iconic"
/// 这件 alpha.25 的死穴在新引擎里根本不构成问题。
/// </para>
/// <para>
/// 【退化链】N 片注册任何一步失败 → 整窗单片（无弯曲，整幅矩形飞收）；单片也失败 →
/// 返回 false，调用方退回旧图像链路（抓帧 + mesh/strips）。宿主玻璃开不成
/// （无桌面合成环境）直接判不可用——宁可没有动画，也不给屏幕留黑洞（ThumbnailPreview 缺陷 2 的教训）。
/// </para>
/// <para>
/// 【泄漏红线】缩略图句柄不是随进程回收的普通引用：每次 Play 注册的 N 个句柄在
/// <see cref="Finish"/> 全数注销，<see cref="Dispose"/> 再兜底一次；宿主窗口惰性创建、
/// 全程复用、只在 Dispose 销毁。"注册次数 == 注销次数"的不变量与 ThumbnailPreview 相同。
/// </para>
/// </summary>
public sealed class DwmLiveGhost : IDisposable
{
    /// <summary>切片数。太少（&lt;8）有台阶感，太多（&gt;16）每帧 RPC 白涨；12 在弯曲感与开销间取中。</summary>
    private const int SliceCount = 12;

    /// <summary>画布（窗口矩形 ∪ 图标矩形）四周的呼吸边距：像素取整余量，不是可视设计。</summary>
    private const double CanvasPad = 8.0;

    private const string ClassName = "BeautifulGenieHost";
    private const uint WmEraseBkgnd = 0x0014;

    private static readonly Dictionary<IntPtr, DwmLiveGhost> Instances = new();
    private static ushort _classAtom;
    // WndProc 委托必须留在静态字段：被 GC 后原生侧回调就是随机崩溃（与 ThumbnailPreview 同一纪律）
    private static WndProcDelegate? _classProc;

    private IntPtr _hwnd;
    private bool _glassReady;
    private bool _unavailableLogged;

    // 一次 Play 的活动状态（Finish 清空）
    private readonly List<IntPtr> _thumbs = new();
    private IntPtr _source;
    private Rect _sourceRect;          // 源窗口物理矩形（DWM 采样的坐标系基准）
    private Rect _canvasOrigin;        // 画布物理原点（宿主窗口左上角）
    private double _scaleX, _scaleY, _endWidth;
    private int _slices;
    private bool _singleMode;          // 切片注册失败后的单片退化

    /// <summary>是否处于一次已注册的播放中（宿主可见 + 句柄未注销）。</summary>
    public bool IsActive => _thumbs.Count > 0;

    // ---------- 宿主窗口（惰性创建，全程复用） ----------

    private bool EnsureHost()
    {
        if (_hwnd != IntPtr.Zero) return _glassReady;
        EnsureClass();
        var exStyle = (uint)(WsExToolwindow | WsExNoactivate);
        _hwnd = CreateWindowExW(exStyle, ClassName, null, WsPopup,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            LogUnavailableOnce("genie 宿主窗口创建失败");
            return false;
        }
        Instances[_hwnd] = this;

        // 整客户区玻璃：DWM 缩略图只在玻璃像素上显形（ThumbnailPreview 缺陷 2 的结论，照抄）。
        var glass = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
        var hr = DwmExtendFrameIntoClientArea(_hwnd, ref glass);
        _glassReady = hr == 0;
        if (!_glassReady) LogUnavailableOnce($"DwmExtendFrameIntoClientArea 失败 hr=0x{hr:X8}");
        return _glassReady;
    }

    private static void EnsureClass()
    {
        if (_classAtom != 0) return;
        _classProc = WndProc;
        // 类刷 = NULL：宿主上没有任何 GDI 内容，带片之外的区域就是玻璃（透明）。绝不允许黑底。
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = _classProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = ClassName,
        };
        _classAtom = RegisterClassExW(ref wc);
        if (_classAtom == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err == 1414) _classAtom = 1;   // 类已存在（跨实例重建）：复用安全（同名同过程）
            else Logger.Warn($"genie 宿主 RegisterClassExW 失败 err={err}");
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr ValidateRect(IntPtr hwnd, IntPtr rect);

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 宿主上没有 GDI 内容：擦除与绘制一律空转，玻璃像素由 DWM 直接合成。
        // WM_PAINT 必须 Validate：返回 0 但不清更新区会让系统持续重发 WM_PAINT（消息风暴）。
        if (msg == WmEraseBkgnd) return new IntPtr(1);
        if (msg == WmPaint) { _ = ValidateRect(hwnd, IntPtr.Zero); return IntPtr.Zero; }
        if (msg == WmDestroy)
        {
            Instances.Remove(hwnd);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>同类原因只记第一条（动画每秒都可能尝试，逐次 Warn 只会刷满日志）。</summary>
    private void LogUnavailableOnce(string reason)
    {
        if (_unavailableLogged) return;
        _unavailableLogged = true;
        Logger.Warn($"DWM 实况 genie 不可用（退回图像链路，同类原因不再逐次记录）：{reason}");
    }

    // ---------- 播放 ----------

    /// <summary>
    /// 起播：注册切片缩略图并摆好 t=0 首帧。<paramref name="sourceRect"/> 为源窗口<b>物理</b>矩形
    /// （事件路径取"最后已知非图标矩形"，事件到手时 GetWindowRect 早已是 -32000 哨兵）；
    /// <paramref name="iconRect"/> 为 Dock 图标物理矩形。任何失败返回 false 且不留句柄。
    /// </summary>
    public bool Begin(IntPtr source, Rect sourceRect, Rect iconRect)
    {
        Finish();   // 重入保护：上一条的句柄先清干净
        if (!EnsureHost()) return false;
        if (source == IntPtr.Zero || !IsWindow(source)) return false;
        if (sourceRect.IsEmpty || sourceRect.Width < 1 || sourceRect.Height < 1) return false;

        // 画布 = 源窗口 ∪ 图标：genie 的尾巴要一路收进 Dock，越过窗口下缘的部分不能被裁掉
        //（旧 GhostLayer 画布只有窗口矩形，尾巴在窗口下缘被硬切，就是 alpha.25 的 C2 残影）。
        var canvas = Rect.Union(sourceRect, iconRect);
        canvas.Inflate(CanvasPad, CanvasPad);
        if (canvas.Width < 1 || canvas.Height < 1 || canvas.Width > 16384 || canvas.Height > 16384) return false;
        _canvasOrigin = new Rect(canvas.Left, canvas.Top, 0, 0);
        _sourceRect = sourceRect;
        _source = source;

        // 归一化目标（与 StartStripGenie 同一换算，几何全部复用 Core 的纯函数与单测）
        _scaleX = (iconRect.X + iconRect.Width / 2 - (sourceRect.Left + sourceRect.Width / 2)) / sourceRect.Width;
        _scaleY = (iconRect.Y - sourceRect.Top) / sourceRect.Height;
        _endWidth = Math.Clamp(sourceRect.Width > 0 ? iconRect.Width / sourceRect.Width : 0.10, 0.04, 0.6);

        // 先落位但【不显示】：注册 + 首帧写完再 Show——玻璃在无内容的头一帧里若是深色，
        // 就是用户看得见的一闪；先画后显则屏上直接从第一帧 genie 开始。
        SetWindowPos(_hwnd, HwndTopmost,
            (int)Math.Round(canvas.Left), (int)Math.Round(canvas.Top),
            (int)Math.Round(canvas.Width), (int)Math.Round(canvas.Height),
            SwpNoactivate);

        _slices = SliceCount;
        _singleMode = false;
        if (!RegisterSliced())
        {
            // 退化：整窗单片（无弯曲，一条矩形飞收）。连单片都注册不上 → 判不可用。
            UnregisterAll();
            _singleMode = true;
            _slices = 1;
            if (!RegisterSliced())
            {
                UnregisterAll();
                LogUnavailableOnce("DwmRegisterThumbnail 切片与单片均失败");
                _source = IntPtr.Zero;
                return false;
            }
        }

        Apply(0, reverse: false);   // 先落首帧再可见（与 alpha.25 遮挡重播同一时序纪律）
        ShowWindow(_hwnd, SwShowna);
        return true;
    }

    private bool RegisterSliced()
    {
        for (var i = 0; i < _slices; i++)
        {
            var hr = DwmRegisterThumbnail(_hwnd, _source, out var thumb);
            if (hr != 0 || thumb == IntPtr.Zero) return false;
            _thumbs.Add(thumb);
        }
        return true;
    }

    /// <summary>每帧：把 <see cref="GenieMath"/> 的归一化带矩形写成各片的 rcDestination。</summary>
    public void Apply(double t, bool reverse)
    {
        if (_thumbs.Count == 0 || _sourceRect.IsEmpty) return;
        var p = reverse ? 1.0 - t : t;
        var bands = GenieMath.Sample(p, _slices, GenieMath.DefaultLag,
            0.5 + _scaleX, 1.0 + _scaleY, _endWidth);

        var sw = _sourceRect.Width;
        var sh = _sourceRect.Height;
        var sliceH = sh / _thumbs.Count;

        for (var i = 0; i < _thumbs.Count; i++)
        {
            var (_, band, opacity) = i < bands.Count ? bands[i] : bands[^1];
            // 归一化带矩形（相对源矩形）→ 物理像素 → 画布客户区坐标
            var dest = new RECT
            {
                Left = (int)Math.Round(_sourceRect.Left + band.X * sw - _canvasOrigin.X),
                Top = (int)Math.Round(_sourceRect.Top + band.Y * sh - _canvasOrigin.Y) - (i == 0 ? 0 : 1),
                Right = (int)Math.Round(_sourceRect.Left + (band.X + band.Width) * sw - _canvasOrigin.X),
                Bottom = (int)Math.Round(_sourceRect.Top + (band.Y + band.Height) * sh - _canvasOrigin.Y) + 1,
            };
            // 第 i 片的源内容 = 源窗口的第 i 条（窗口坐标，含边框；单片模式 = 整窗）
            var srcTop = (int)Math.Round(i * sliceH);
            var srcBottom = (int)Math.Round((i + 1) * sliceH);
            var source = new RECT
            {
                Left = 0,
                Top = srcTop,
                Right = (int)Math.Round(sw),
                Bottom = _singleMode ? (int)Math.Round(sh) : Math.Max(srcTop + 1, srcBottom),
            };
            var visible = dest.Right > dest.Left && dest.Bottom > dest.Top;
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DwmtRectSource | DwmtRectDestination | DwmtOpacity | DwmtVisible
                          | DwmtSourceClientAreaOnly,
                rcDestination = dest,
                rcSource = source,
                opacity = (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * 255),
                fVisible = visible,
                fSourceClientAreaOnly = false,   // 含边框：与旧 PrintWindow 抓整窗的口径一致
            };
            _ = DwmUpdateThumbnailProperties(_thumbs[i], ref props);
        }
    }

    /// <summary>收尾：注销全部句柄并隐藏宿主（宿主保留复用）。可安全重复调用。</summary>
    public void Finish()
    {
        UnregisterAll();
        _source = IntPtr.Zero;
        _sourceRect = Rect.Empty;
        if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SwHide);
    }

    private void UnregisterAll()
    {
        for (var i = 0; i < _thumbs.Count; i++) _ = DwmUnregisterThumbnail(_thumbs[i]);
        _thumbs.Clear();
    }

    public void Dispose()
    {
        Finish();
        if (_hwnd != IntPtr.Zero)
        {
            Instances.Remove(_hwnd);
            _ = DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
