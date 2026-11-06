using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using static Beautiful.App.Interop.NativeMethods;
// UseWindowsForms 把 System.Drawing 引入全局命名空间，与 WPF 的 Rect 同名 → 显式取 WPF 版本。
using Rect = System.Windows.Rect;
using Application = System.Windows.Application;   // 同上，System.Windows.Forms.Application

namespace Beautiful.App.StageManager;

/// <summary>
/// 台前调度缩略图的<b>纯 Win32 玻璃弹层宿主</b>（wp-macos-parity r5）：舞台条窗口本体是
/// WPF <b>分层</b>窗口（真机取证：WPF 非分层窗口在 Win11 26300 上整机黑屏——见
/// Finder/QuickLookWindow.xaml 头注释；分层窗口上 DWM 缩略图不合成——见 ThumbnailPreview
/// 头注释），所以实况缩略图必须落在一个<b>独立</b>的 Win32 弹层上，浮在舞台条窗口的
/// 缩略图列正上方。本类就是那个弹层：与 <see cref="Preview.ThumbnailPreview"/>/
/// <c>Animation.DwmLiveGhost</c> 的宿主同一血统（WS_POPUP + WS_EX_TOOLWINDOW +
/// WS_EX_NOACTIVATE + 整客户区玻璃），差异只在它是<b>常驻多张</b>缩略图的底板。
/// <para>
/// 【归属与 z 序】以舞台条窗口为 owner（CreateWindowExW 的 parenthwnd 参数）——owned 弹层
/// 恒在 owner 之上，且不会被 owner 挡住；显隐由 StageBarWindow 显式管理（Collapse /
/// SetSuppressed(true) / 关闭都收），不依赖"owner 隐藏弹层自动藏"这类不保证的规则。
/// </para>
/// <para>
/// 【命中与点击】缩略图矩形之外的整片玻璃 <c>WM_NCHITTEST</c> 返回 HTTRANSPARENT——
/// 鼠标穿透到下面的 WPF 卡片（图标行/卡片留白照常可点）；矩形之内返回 HTCLIENT，
/// <c>WM_LBUTTONUP</c> 按 y 命中卡片并回调 <see cref="ThumbClicked"/> → 切换窗口。
/// 玻璃本身没有 GDI 内容（WM_PAINT 只 Validate，与 DwmLiveGhost 同纪律：不清更新区会
/// 消息风暴）；DWM 不合成像素的环境里玻璃就是透明的，露出的是 WPF 卡片的占位底色，
/// 绝无黑框。
/// </para>
/// <para>
/// 【线程模型】仅限 WPF UI 线程创建与调用（舞台条同线程）。Dispose 销毁窗口；
/// 消息循环结束后的调用为 no-op（IsWindow 兜底）。
/// </para>
/// </summary>
internal sealed class StageThumbHostWindow : IDisposable
{
    /// <summary>点击命中某张缩略图（参数 = 该卡片的源窗口句柄，调用方负责切换）。</summary>
    public delegate void ThumbClicked(IntPtr sourceHwnd);

    private const string ClassName = "BeautifulStageThumbHost";
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmLbuttonUp = 0x0202;
    private const int HtClient = 1;
    private const int HtTransparent = -1;

    private static readonly Dictionary<IntPtr, StageThumbHostWindow> Instances = new();
    private static ushort _classAtom;
    // WndProc 委托必须留在静态字段：被 GC 后原生侧回调就是随机崩溃（ThumbnailPreview 同纪律）。
    private static WndProcDelegate? _classProc;

    private readonly Dispatcher _dispatcher;
    private readonly ThumbClicked _onClicked;
    private IntPtr _hwnd;

    // 点击/命中判定用的卡片矩形（弹层客户区、物理 px；SetHitRects 维护）。
    private List<(IntPtr Source, Rect ClientPx)> _hitRects = new();

    public StageThumbHostWindow(IntPtr ownerHwnd, ThumbClicked onClicked)
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _onClicked = onClicked ?? throw new ArgumentNullException(nameof(onClicked));
        EnsureClass();
        var exStyle = (uint)(WsExToolwindow | WsExNoactivate);
        // WS_POPUP + parenthwnd = owned popup：恒浮于 owner 之上，不进任务栏/Alt+Tab。
        _hwnd = CreateWindowExW(exStyle, ClassName, null, WsPopup,
            0, 0, 0, 0, ownerHwnd, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Logger.Warn($"台前调度：缩略图宿主弹层创建失败 err={Marshal.GetLastWin32Error()}");
            return;
        }
        Instances[_hwnd] = this;

        // 整客户区玻璃：DWM 缩略图只在带玻璃通道的宿主上显形（ThumbnailPreview 缺陷 2 的结论，
        // 与 DwmLiveGhost 宿主同一手法）。hr==0 只代表"受理"，真机合成与否只能目视确认（诚实边界）。
        var glass = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
        var glassHr = DwmExtendFrameIntoClientArea(_hwnd, ref glass);
        Logger.Info($"台前调度：缩略图宿主弹层 hwnd=0x{_hwnd.ToInt64():X} " +
                    $"DwmExtendFrameIntoClientArea(-1) hr=0x{glassHr:X8}");
    }

    /// <summary>宿主弹层句柄（StageThumbStrip 的注册目标；创建失败为 Zero）。</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>更新点击/穿透判定矩形（弹层客户区、物理 px；与 DWM 注册矩形同源）。</summary>
    public void SetHitRects(IReadOnlyList<(IntPtr Source, Rect ClientPx)> rects)
    {
        _dispatcher.VerifyAccess();
        _hitRects = new List<(IntPtr, Rect)>(rects);
    }

    /// <summary>定位到屏幕物理矩形并以不激活方式显出（StageBarWindow 的 Place 链调用）。</summary>
    public void Place(Rect screenPhysicalRect)
    {
        _dispatcher.VerifyAccess();
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        SetWindowPos(_hwnd, HwndTopmost,
            (int)Math.Round(screenPhysicalRect.Left), (int)Math.Round(screenPhysicalRect.Top),
            (int)Math.Round(screenPhysicalRect.Width), (int)Math.Round(screenPhysicalRect.Height),
            SwpNoactivate | SwpShowWindow);
    }

    /// <summary>隐藏弹层（收起 / 全屏让位；缩略句柄由 StageThumbStrip 另行注销）。</summary>
    public void Hide()
    {
        _dispatcher.VerifyAccess();
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        ShowWindow(_hwnd, SwHide);
    }

    /// <summary>光标（屏幕物理坐标）是否落在弹层当前矩形内（自动隐藏轮询用；隐藏后仍按最后矩形算，
    /// 调用方保证只在展开态——弹层可见——时使用）。</summary>
    public bool ScreenRectContains(int x, int y)
        => _hwnd != IntPtr.Zero && IsWindow(_hwnd) && GetWindowRect(_hwnd, out var r)
           && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_hwnd == IntPtr.Zero) return;
        var hwnd = _hwnd;
        _hwnd = IntPtr.Zero;
        Instances.Remove(hwnd);
        if (IsWindow(hwnd)) DestroyWindow(hwnd);   // 无 GDI 资源、无缩略句柄（那归 StageThumbStrip）
    }

    // ---------- 窗口类 / 消息处理 ----------

    private static void EnsureClass()
    {
        if (_classAtom != 0) return;
        _classProc = WndProc;
        // 类刷 = NULL：弹层上没有任何 GDI 内容，缩略矩形之外就是玻璃（透明）。绝不允许黑底。
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
            if (err == 1414) _classAtom = 1;   // 类已存在（重载复用）：同名同过程，安全
            else Logger.Warn($"台前调度：缩略图宿主 RegisterClassExW 失败 err={err}");
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 玻璃弹层没有 GDI 内容：擦除/绘制一律空转。WM_PAINT 必须 Validate（不清更新区 = 消息风暴）。
        if (msg == WmEraseBkgnd) return new IntPtr(1);
        if (msg == WmPaint)
        {
            ValidateRect(hwnd, IntPtr.Zero);
            return IntPtr.Zero;
        }
        if (msg == WmNcHitTest)
        {
            // 缩略矩形内 HTCLIENT（收点击），其余 HTTRANSPARENT（穿透到下面的 WPF 卡片）。
            if (Instances.TryGetValue(hwnd, out var inst) && inst.HitTest(lParam)) return new IntPtr(HtClient);
            return new IntPtr(HtTransparent);
        }
        if (msg == WmLbuttonUp)
        {
            // 客户区坐标（物理 px）→ 命中卡片 → 回调切换。命中判定与 NCHITTEST 同一份矩形表。
            var x = (short)(lParam.ToInt64() & 0xFFFF);
            var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            if (Instances.TryGetValue(hwnd, out var inst))
                inst.RaiseClick(x, y);
            return IntPtr.Zero;
        }
        if (msg == WmDestroy)
        {
            Instances.Remove(hwnd);
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>屏幕坐标（NCHITTEST 的 lParam）是否落在某张缩略矩形内。</summary>
    private bool HitTest(IntPtr lParam)
    {
        var sx = (short)(lParam.ToInt64() & 0xFFFF);
        var sy = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        if (!GetWindowRect(_hwnd, out var wr)) return false;
        var cx = sx - wr.Left;   // WindowStyle=None 弹层：客户区原点 == 窗口原点
        var cy = sy - wr.Top;
        foreach (var (_, rect) in _hitRects)
            if (cx >= rect.Left && cx < rect.Right && cy >= rect.Top && cy < rect.Bottom) return true;
        return false;
    }

    /// <summary>客户区坐标（WM_LBUTTONUP 的 lParam）→ 命中卡片 → 回调（未命中不动作）。</summary>
    private void RaiseClick(int clientX, int clientY)
    {
        foreach (var (source, rect) in _hitRects)
        {
            if (clientX < rect.Left || clientX >= rect.Right) continue;
            if (clientY < rect.Top || clientY >= rect.Bottom) continue;
            try { _onClicked(source); }
            catch (Exception ex) { Logger.Warn($"台前调度：缩略图点击处理异常（已隔离）: {ex.Message}"); }
            return;
        }
    }

    // 与 DwmLiveGhost 同一签名（BOOL 返回值按 IntPtr 接，调用点不使用结果）。
    [DllImport("user32.dll")]
    private static extern IntPtr ValidateRect(IntPtr hwnd, IntPtr rect);
}
