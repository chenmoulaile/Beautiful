using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using static Beautiful.App.Interop.NativeMethods;
using Rect = System.Windows.Rect;

namespace Beautiful.App.Dock;

/// <summary>
/// alpha.26：Dock 最小化区的<b>实况窗口缩略图</b>——每条最小化条目上方浮一只纯 Win32 小弹层，
/// 用 <c>DwmRegisterThumbnail</c> 把源窗口的实时画面直接合成进条目矩形（macOS 语义：
/// 缩略图 = 窗口内容，不是应用图标）。
/// <para>
/// 【为什么必须用它】alpha.25 的条目图标走帧缓存（PrintWindow 2Hz 取样），真机（26H2）实测
/// 该链路半瘫：<c>动画未播…因=抓帧不可用</c>、<c>PrintWindow纯色判废</c> —— 用户看到的
/// "最小后并没有出现缩略图"就是它。而 DWM 缩略图注册在同一台机器上 hr=0 全通过（悬停预览
/// 在用）。最小化窗口的缩略图内容 = 离开时的最后一帧（任务栏同语义），零抓帧、零位图，
/// 注定不踩抓帧的坑。
/// </para>
/// <para>
/// 【airspace 与输入】缩略图必须注册到<b>非 WPF 表面</b>的窗口上（WPF 呈现面会把缩略图盖住，
/// 见 <c>Preview/ThumbnailPreview</c> 的结论），故每条目一只纯 Win32 弹层。弹层全程
/// <c>WM_NCHITTEST → HTTRANSPARENT</c>：<b>所有输入原样穿透到下面的 Dock 条目</b>——
/// 点击还原 / 悬停放大 / 右键菜单的行为与 alpha.25 完全一致，本层只负责"看"，不负责"摸"。
/// </para>
/// <para>
/// 【跟随与对齐】条目矩形由 <c>DockWindow.IconRectPhysical</c> 提供（含放大场实时缩放），
/// DockWindow 在条目集合变化、Surface 鼠标移动、以及 200ms 兜底计时器三处调
/// <see cref="Sync"/>。弹层矩形 = 条目矩形；缩略图矩形 = 源窗口宽高比<b>等比内切</b>于条目
/// （源宽高比取动画器的"最后已知非图标矩形"，拿不到退条目正方形——变形的画面比空白更糟）。
/// </para>
/// <para>
/// 【泄漏红线】每只弹层至多 1 个缩略图句柄，条目移除（还原/关闭）即注销+销毁；
/// <see cref="Dispose"/> 全量兜底。玻璃开不成（无桌面合成）的弹层立即销毁——条目退应用图标
/// （alpha.25 行为），绝不给屏幕留黑框。
/// </para>
/// </summary>
public sealed class MinimizedTileLayer : IDisposable
{
    private const string ClassName = "BeautifulMinTileHost";
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmPaint = 0x000F;
    private const uint WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);

    private static readonly Dictionary<IntPtr, TileHost> Instances = new();
    private static ushort _classAtom;
    // WndProc 委托必须留在静态字段，否则被 GC 后原生回调就是随机崩溃（与 ThumbnailPreview 同一纪律）
    private static WndProcDelegate? _classProc;

    /// <summary>缩略图宽高比来源（动画器的"最后已知非图标矩形"表）。可为 null。</summary>
    private readonly Func<IntPtr, Rect?>? _aspectProvider;
    private readonly IntPtr _ownerHwnd;

    private sealed class TileHost
    {
        public IntPtr Hwnd;
        public IntPtr Thumb;
        public IntPtr Source;
        public bool GlassReady;
        // 上次摆放的物理矩形（避免每 200ms 重复 SetWindowPos 抖动 DWM）
        public int LastX, LastY, LastW, LastH;
    }

    private readonly Dictionary<IntPtr, TileHost> _tiles = new();

    public MinimizedTileLayer(IntPtr ownerHwnd, Func<IntPtr, Rect?>? aspectProvider)
    {
        _ownerHwnd = ownerHwnd;
        _aspectProvider = aspectProvider;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr ValidateRect(IntPtr hwnd, IntPtr rect);

    private static void EnsureClass()
    {
        if (_classAtom != 0) return;
        _classProc = WndProc;
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
            if (err == 1414) _classAtom = 1;
            else Logger.Warn($"最小化 tile 宿主 RegisterClassExW 失败 err={err}");
        }
    }

    // 输入全穿透：点击/悬停/右键都落到下面的 Dock 条目上（本层只负责"看"）。
    // Dock 窗口与本弹层同线程 → HTTRANSPARENT 把消息递给 z 序下方的 Dock，路由成立。
    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNcHitTest) return HtTransparent;
        if (msg == WmEraseBkgnd) return new IntPtr(1);
        if (msg == WmPaint) { _ = ValidateRect(hwnd, IntPtr.Zero); return IntPtr.Zero; }
        if (msg == WmDestroy)
        {
            Instances.Remove(hwnd);
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---------- 同步 ----------

    /// <summary>
    /// 按当前最小化条目集合对齐弹层：新增 → 建+注册；移除 → 注销+销毁；移动 → 摆位+更新缩略图矩形。
    /// <paramref name="tiles"/> = (窗口句柄, 条目物理矩形)。可高频调用（内部有增量短路）。
    /// </summary>
    public void Sync(IReadOnlyList<(IntPtr hwnd, Rect rect)> tiles)
    {
        // ① 移除：不在本次集合里的弹层立即销毁（还原/关闭窗口的最短路径）
        var keep = new HashSet<IntPtr>();
        foreach (var (hwnd, _) in tiles) keep.Add(hwnd);
        List<IntPtr>? stale = null;
        foreach (var key in _tiles.Keys)
            if (!keep.Contains(key)) (stale ??= new List<IntPtr>()).Add(key);
        if (stale is not null)
            foreach (var hwnd in stale) DestroyTile(hwnd);

        // ② 新增 / 移动
        for (var i = 0; i < tiles.Count; i++)
        {
            var (hwnd, rect) = tiles[i];
            if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8) continue;
            var tile = EnsureTile(hwnd);
            if (tile is null) continue;   // 建不起来（玻璃/注册失败）：条目退应用图标，不再重试
            Place(tile, hwnd, rect);
        }
    }

    private TileHost? EnsureTile(IntPtr source)
    {
        if (_tiles.TryGetValue(source, out var existing)) return existing;
        if (source == IntPtr.Zero || !IsWindow(source)) return null;

        EnsureClass();
        var exStyle = (uint)(WsExToolwindow | WsExNoactivate);
        var hwnd = CreateWindowExW(exStyle, ClassName, null, WsPopup,
            0, 0, 0, 0, _ownerHwnd, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return null;
        Instances[hwnd] = new TileHost { Hwnd = hwnd, Source = source };

        // 整客户区玻璃：缩略图只在玻璃像素上显形（ThumbnailPreview 缺陷 2 的结论）
        var glass = new MARGINS { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
        var glassHr = DwmExtendFrameIntoClientArea(hwnd, ref glass);
        if (glassHr != 0)
        {
            Logger.Warn($"最小化 tile 玻璃层开不成 hr=0x{glassHr:X8}（该条目退应用图标）");
            _ = DestroyWindow(hwnd);
            return null;
        }

        var hr = DwmRegisterThumbnail(hwnd, source, out var thumb);
        if (hr != 0 || thumb == IntPtr.Zero)
        {
            Logger.Info($"最小化 tile 注册失败 hr=0x{hr:X8} src=0x{source.ToInt64():X}（该条目退应用图标）");
            _ = DestroyWindow(hwnd);
            return null;
        }

        var tile = (TileHost)Instances[hwnd];
        tile.Thumb = thumb;
        tile.GlassReady = true;
        Logger.Info($"最小化 tile 注册 src=0x{source.ToInt64():X} thumb=0x{thumb.ToInt64():X} dst=0x{hwnd.ToInt64():X}");
        _tiles[source] = tile;
        return tile;
    }

    /// <summary>摆位 + 按源宽高比更新缩略图矩形。矩形没变就不动（高频调用零开销路径）。</summary>
    private void Place(TileHost tile, IntPtr source, Rect rect)
    {
        var x = (int)Math.Round(rect.Left);
        var y = (int)Math.Round(rect.Top);
        var w = (int)Math.Round(rect.Width);
        var h = (int)Math.Round(rect.Height);
        var moved = tile.LastW != w || tile.LastH != h || tile.LastX != x || tile.LastY != y;
        if (moved)
        {
            // 置顶于 Dock 之上但不夺焦；owner 已把 z 序钉在 Dock 前面，TOPMOST 兜底跨屏拖动
            SetWindowPos(tile.Hwnd, HwndTopmost, x, y, w, h, SwpNoactivate | SwpShowWindow);
            tile.LastX = x; tile.LastY = y; tile.LastW = w; tile.LastH = h;
        }
        else if (tile.Thumb == IntPtr.Zero)
        {
            return;
        }

        // 等比内切：源宽高比取"最后已知非图标矩形"（最小化后 DwmQueryThumbnailSourceSize
        // 对 iconic 窗口的返回不可靠），拿不到退条目正方形。
        double srcAspect = 16.0 / 9.0;
        var known = false;
        if (_aspectProvider?.Invoke(source) is { } srcRect && srcRect.Width > 1 && srcRect.Height > 1)
        {
            srcAspect = srcRect.Width / srcRect.Height;
            known = true;
        }
        else if (DwmQueryThumbnailSourceSize(tile.Thumb, out var size) == 0 && size.cx > 1 && size.cy > 1)
        {
            srcAspect = (double)size.cx / size.cy;
            known = true;
        }
        if (!known) srcAspect = 1.0;   // 什么都没有：正方形（条目下面的应用图标承担兜底观感）

        const double inset = 1.0;   // 留一圈 Dock 玻璃描边，缩略图不压住条目圆角
        var availW = Math.Max(1.0, w - inset * 2);
        var availH = Math.Max(1.0, h - inset * 2);
        var fitW = availW;
        var fitH = fitW / srcAspect;
        if (fitH > availH) { fitH = availH; fitW = fitH * srcAspect; }

        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmtRectDestination | DwmtVisible | DwmtOpacity | DwmtSourceClientAreaOnly,
            rcDestination = new RECT
            {
                Left = (int)Math.Round(inset + (availW - fitW) / 2),
                Top = (int)Math.Round(inset + (availH - fitH) / 2),
                Right = (int)Math.Round(inset + (availW - fitW) / 2 + fitW),
                Bottom = (int)Math.Round(inset + (availH - fitH) / 2 + fitH),
            },
            rcSource = default,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true,   // tile 只要内容区：标题栏文字在 44px 里是噪声
        };
        _ = DwmUpdateThumbnailProperties(tile.Thumb, ref props);
    }

    private void DestroyTile(IntPtr source)
    {
        if (!_tiles.Remove(source, out var tile)) return;
        Instances.Remove(tile.Hwnd);
        if (tile.Thumb != IntPtr.Zero)
        {
            _ = DwmUnregisterThumbnail(tile.Thumb);
            tile.Thumb = IntPtr.Zero;
        }
        _ = DestroyWindow(tile.Hwnd);
    }

    public void Dispose()
    {
        var keys = new List<IntPtr>(_tiles.Keys);
        foreach (var key in keys) DestroyTile(key);
        _tiles.Clear();
    }
}
