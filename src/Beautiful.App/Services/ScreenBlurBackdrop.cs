using System.Buffers;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.Core.Imaging;
using Beautiful.Core.Layout;
using static Beautiful.App.Interop.NativeMethods;
using Brush = System.Windows.Media.Brush;   // UseWindowsForms 隐式全局 using 与 System.Drawing.Brush 冲突

namespace Beautiful.App.Services;

/// <summary>
/// 内容侧的背板就绪通知（v3 T2/T3 替换 v2 的 <c>OnBackdropApplied(BackdropPath)</c> 协商）。
/// <para>
/// v2 之所以要"路径枚举"：那一年只有 DWM 材质能模糊，内容必须知道"有没有真模糊"才能决定底色是否可半透明，
/// 而协商结果在真机上还骗人（<c>hr==0</c> 只证明属性被接受）。v3 的模糊是**我们自己抓屏算出来的**，
/// 于是"有背板"退化成一个事实问题：画刷拿到了就铺上去，没拿到就实心底 —— 不再需要枚举，也不再需要
/// 让内容去猜系统那一侧发生了什么。
/// </para>
/// </summary>
public interface IBackdropAware
{
    /// <summary>
    /// 抓屏背板就绪（宿主已把画刷瞄准到**窗口矩形**）。<paramref name="backdrop"/> 为 <c>null</c> 表示本次
    /// 没有模糊（环境开关关闭 / 抓屏失败 / 取不到显示器），内容必须自铺**实心**卡底——分层窗口上"没画到"
    /// 就是看穿到桌面的洞，任何降级环节都不许出现它。
    /// <para>
    /// 【为什么瞄准矩形给的是窗口而不是卡片】弹层显示那一刻内容的 <c>ActualWidth/Height</c> 还没定形
    /// （布局排在 Render 优先级上），按卡片矩形算会有时序依赖；窗口矩形则是宿主在 <c>ShowAt</c> 里
    /// 同步量出来的确定值。卡片相对窗口内缩的那点投影留白（≤20 DIP）只是让模糊平移了不到一个像素格，
    /// 在半径 32 的模糊下不可分辨——用一次不可分辨的位移换掉一整类布局时序 bug，是划算的取舍。
    /// </para>
    /// </summary>
    void OnBackdropReady(ScreenBackdrop? backdrop);
}

/// <summary>
/// 一次抓屏 + 模糊的成果：覆盖捕获区的模糊位图，加上一个可以反复瞄准到面板矩形的画刷。
/// <para>
/// 【为什么要"瞄准"而不是把整张图 Stretch 到窗口】面板会长大（切换器展开多窗口子行、菜单换行）。
/// 若把捕获图整体拉伸到窗口矩形，尺寸一变整幅模糊就被重新缩放一次（背景跟着面板"呼吸"，一眼假）。
/// 这里把画刷的 <c>Viewbox</c>（绝对单位 = 图像自身坐标）设成"面板在捕获区里的那一块"，
/// <c>Viewport</c> 保持默认整元素包围盒：<c>TileMode=None + Stretch=Fill</c> 下等价于
/// "从同一张已抓好的图里，随面板长大露出旁边已经抓好的内容"——不重抓屏，也不重算缩放。
/// </para>
/// <para>
/// 【Viewport 为什么留 RelativeToBoundingBox】绝对单位的 Viewport 原点同样是"被绘制元素的左上角"，
/// 与默认包围盒写法结果一致，却要多拿一次元素 DIP 尺寸（多一处可算错的地方）。屏幕坐标全部体现在
/// <c>Viewbox</c> 里，语义与 Viewport-绝对等价而自由度更少。
/// </para>
/// </summary>
public sealed class ScreenBackdrop
{
    private readonly PhysicalRect _region;
    private readonly ImageBrush _brush;
    private readonly double _imgWidth;    // 模糊图尺寸（图像单位；1 单位 = DownscaleFactor 个屏幕物理 px）
    private readonly double _imgHeight;
    private bool _outOfRegionWarned;

    internal ScreenBackdrop(in PhysicalRect region, BitmapSource image)
    {
        _region = region;
        _imgWidth = image.PixelWidth;
        _imgHeight = image.PixelHeight;
        _brush = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,          // 比例由 Viewbox→Viewport 的映射保证，这里不需要保持宽高
            TileMode = TileMode.None,        // 越出 Viewbox 不平铺（平铺会在面板边缘叠出一条镜像带）
            ViewboxUnits = BrushMappingMode.Absolute,   // 绝对 = 图像自身坐标（图按 96DPI 建，1 单位 = 1 图像像素）
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
        };
    }

    /// <summary>
    /// 配好比例的画刷（**未冻结**：面板长大后要重瞄，见 <see cref="Aim(PhysicalRect)"/>）。
    /// 赋给 <c>Border.Background</c> 即可；圆角由宿主 Border 的 <c>CornerRadius</c> 裁切——
    /// 分层窗口的逐像素 alpha 是这里唯一的形状来源。
    /// </summary>
    public ImageBrush Brush => _brush;

    /// <summary>捕获区（屏幕物理像素）。日志与排障用（"背板为什么是实心底"先看这一行）。</summary>
    public PhysicalRect Region => _region;

    /// <summary>瞄准给定的屏幕物理矩形。返回 false = 该矩形超出捕获区（调用方须退回实心底）。</summary>
    public bool Aim(in PhysicalRect panelPx)
    {
        const double f = ScreenBlurBackdrop.DownscaleFactor;
        var vb = new Rect(
            (panelPx.Left - _region.Left) / f,
            (panelPx.Top - _region.Top) / f,
            Math.Max(1, panelPx.Width) / f,
            Math.Max(1, panelPx.Height) / f);
        // 越界必须查：TileMode=None 时 Viewbox 超出图像的那部分**什么都不画**，而分层窗口上"不画"
        // = 真透明 → 卡片上出现一块能看穿到桌面的洞（正是 v2 顶栏"露壁纸"的同款故障，绝不能重演）。
        if (vb.X < 0 || vb.Y < 0 || vb.Right > _imgWidth + 0.5 || vb.Bottom > _imgHeight + 0.5)
        {
            if (!_outOfRegionWarned)
            {
                _outOfRegionWarned = true;
                Logger.Warn($"背板瞄准越界：面板=({panelPx.Left},{panelPx.Top})-({panelPx.Right},{panelPx.Bottom}) " +
                            $"捕获区=({_region.Left},{_region.Top})-({_region.Right},{_region.Bottom}) " +
                            $"图={_imgWidth:0}×{_imgHeight:0} → 本次改用实心卡底（四周余量不足）");
            }
            return false;
        }
        _brush.Viewbox = vb;
        return true;
    }
}

/// <summary>
/// 抓屏式背板（v3 T2）：显示前把面板下方的屏幕内容取回 → 1/4 降采样 → 三次盒式模糊 → 交回画刷。
/// <para>
/// 【为什么存在（v3 §1.B 裁定）】v2 假定"非分层 WPF 窗口 + <c>DwmExtendFrameIntoClientArea(-1)</c> +
/// <c>DWMWA_SYSTEMBACKDROP_TYPE=Acrylic</c>"就能让 DWM 把客户端 alpha 与真亚克力一起合成。用户真机证伪：
/// Alt+Tab 面板画成**带硬边的黑矩形**（半透明卡底下的客户端像素没被合成，alpha=0 即黑），
/// <c>SetWindowRgn</c> 的圆角同时也没生效。属性返回 S_OK 只证明"被接受"，从来不证明"被画出"
/// （<c>AcrylicWindow</c> v2 已把这条标为 UNVERIFIED）。于是 v3 把材质收回自己手里：
/// 透明与圆角由分层窗口 + 自绘承担，"玻璃"由本类抓屏模糊承担，两者都不依赖 DWM/驱动版本，
/// 因此**本机可截屏验证**。
/// </para>
/// <para>
/// 【只在瞬态表面用】切换器、托盘/顶栏菜单、媒体面板这类"显示一次"的表面在**首次 Show 之前**抓屏，
/// 抓到的一定是干净的下方内容。常驻表面（顶栏、Dock）绝不调用本类：重抓会把自己已有的像素抓进去
/// （鬼影），而为了抓屏先隐藏一帧就是可见的闪烁——那条路按裁定走"自绘半透明 tint + best-effort 旧亚克力"，
/// 见 <see cref="AcrylicWindow.ApplyTintFallback"/>。
/// </para>
/// <para>
/// 【降级不得变黑】抓屏失败（无显示器 / 安全桌面 / 全零帧 / GDI 异常）一律返回 null，
/// 调用方改用现成的实心渐变卡底（<c>CardFillSolid</c> / <c>PanelFillSolid</c>）。
/// 环境开关 <c>BEAUTIFUL_NOBLUR=1</c> 强制走这条降级，便于肉眼验证降级形态。
/// </para>
/// </summary>
public static class ScreenBlurBackdrop
{
    /// <summary>降采样倍数：1 图像像素 = 4 个屏幕物理像素。模糊代价按像素数算，这是 1/16 的工作量。</summary>
    public const int DownscaleFactor = 4;

    /// <summary>
    /// 模糊半径（作用在**降采样后**的图上）：8 × 4 = 相当于对原分辨率做半径 32 的高斯，与 Win11 亚克力
    /// （约 30px）同级；再大只会糊成一片，失去"看得出背后是什么"的信息量。
    /// </summary>
    public const int BlurRadius = 8;

    /// <summary>
    /// 捕获区在面板四周多留的余量（DIP）。瞬态面板会长大（切换器展开窗口子行、菜单改行），
    /// 留足余量后长大只是"露出旁边已抓好的内容"，不必重抓屏；160 DIP 覆盖本项目所有表面的生长量。
    /// </summary>
    public const double DefaultMarginDip = 160;

    /// <summary>
    /// 抓屏降级开关：<c>BEAUTIFUL_NOBLUR=1</c> 时本类所有入口恒返回 null（全部表面走实心卡底）。
    /// 与 <c>BEAUTIFUL_FRAMETIME</c> 同一套"环境变量做 QA 开关"的做法；只读一次并缓存。
    /// </summary>
    public static bool DisabledByEnvironment { get; } =
        Environment.GetEnvironmentVariable("BEAUTIFUL_NOBLUR") == "1";

    // 开关只播报一次（每次开菜单都打一行会把日志冲走）。UI 线程单点访问，无需加锁。
    private static bool _envSwitchAnnounced;

    /// <summary>
    /// 抓取指定屏幕区域（物理像素）并返回**模糊后**的位图。任何失败都返回 false + null，
    /// 绝不抛异常：抓屏发生在弹层显示路径上，一次 GDI 失败不能变成"菜单打不开"。
    /// <para>
    /// 返回类型是 <see cref="BitmapSource"/>（实际为冻结的 <see cref="WriteableBitmap"/>）而不是
    /// <c>RenderTargetBitmap</c>：后者只能由 WPF 渲染产生，而本路径的像素来自 GDI，再走一次 RTB
    /// 等于把已经算好的图重画一遍（多一次 GPU 往返、零收益）。
    /// </para>
    /// </summary>
    public static bool TryCapture(in PhysicalRect region, out BitmapSource? blurred)
    {
        blurred = null;
        if (DisabledByEnvironment) { AnnounceEnvSwitch(); return false; }
        if (region.Width <= 0 || region.Height <= 0) return false;
        byte[]? raw = null;
        try
        {
            raw = CaptureRegion(region, out var gotPixels);
            if (!gotPixels || raw is null) return false;
            if (IsAllBlack(raw, region.Width, region.Height))
            {
                // 全零 = 什么都没抓到（安全桌面/UAC 提权界面、无显示输出、远程会话被策略禁抓）。
                // 判据只能是"逐字节为 0"：任何阈值都会把"桌面本来就很暗"误判成抓屏失败。
                Logger.Warn($"背板抓屏得到全零帧，放弃模糊：区=({region.Left},{region.Top})-({region.Right},{region.Bottom})");
                return false;
            }
            var small = BoxBlur.Downscale(raw, region.Width, region.Height, DownscaleFactor,
                                          out var sw, out var sh);
            BoxBlur.Apply(small, sw, sh, BlurRadius);
            var bmp = new WriteableBitmap(sw, sh, 96, 96, PixelFormats.Bgr32, null);
            bmp.WritePixels(new Int32Rect(0, 0, sw, sh), small, sw * BoxBlur.BytesPerPixel, 0);
            bmp.Freeze();   // 冻结后才能安全地被画刷长期引用（也免掉变更跟踪）
            blurred = bmp;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"背板抓屏失败（改用实心卡底）: {ex.Message}");
            return false;
        }
        finally
        {
            if (raw is not null) ArrayPool<byte>.Shared.Return(raw);
        }
    }

    /// <summary>
    /// 面板矩形 → 可直接用的背板（抓屏 + 模糊 + 画刷已瞄准到该矩形）。返回 null = 本次无模糊。
    /// </summary>
    /// <param name="panelPx">面板的屏幕物理矩形（调用方在**首次显示之前**算出的那一个）。</param>
    /// <param name="marginDip">四周多抓的余量（DIP），见 <see cref="DefaultMarginDip"/>。</param>
    public static ScreenBackdrop? ForPanel(in PhysicalRect panelPx, double marginDip = DefaultMarginDip)
    {
        if (DisabledByEnvironment) { AnnounceEnvSwitch(); return null; }
        if (panelPx.Width <= 0 || panelPx.Height <= 0) return null;
        var area = MonitorGeometry.AreaForPoint((panelPx.Left + panelPx.Right) / 2,
                                               (panelPx.Top + panelPx.Bottom) / 2);
        if (area is null) return null;              // 取不到显示器：宁可不模糊，也不画一块洞
        var a = area.Value;
        var region = CaptureRegionFor(panelPx, marginDip * a.DpiScale, a);
        if (region.Width <= 0 || region.Height <= 0) return null;
        if (!TryCapture(region, out var img) || img is null)
        {
            Logger.Info($"背板=实心降级（抓屏未产出） 面板=({panelPx.Left},{panelPx.Top})-({panelPx.Right},{panelPx.Bottom}) " +
                        $"区=({region.Left},{region.Top})-({region.Right},{region.Bottom})");
            return null;
        }
        var backdrop = new ScreenBackdrop(region, img);
        // 首次瞄准就失败说明捕获区没包住面板（钳制把工作区外的部分裁掉了）：交回 null 让调用方铺实心底。
        if (!backdrop.Aim(panelPx))
        {
            Logger.Info("背板=实心降级（瞄准越界） 面板与捕获区见上一行告警");
            return null;
        }
        // 每次显示留一行"背板=…"：真机上用户报"看不到模糊"时，这一行就是第一手判据
        // （v2 靠 "背板路径=" 那行做同样的事，退役后诊断入口不能跟着退役）。
        Logger.Info($"背板=抓屏模糊 面板=({panelPx.Left},{panelPx.Top})-({panelPx.Right},{panelPx.Bottom}) " +
                    $"区=({region.Left},{region.Top})-({region.Right},{region.Bottom}) 图={img.PixelWidth}×{img.PixelHeight} " +
                    $"半径={BlurRadius}×{DownscaleFactor}");
        return backdrop;
    }

    /// <summary>
    /// 便捷入口：给"尺寸此后不再变化"的窗口面板返回一个**已冻结**的画刷（冻结后 WPF 免变更跟踪）。
    /// 面板会长大的表面（切换器）请改用 <see cref="ForPanel"/> + 每次重排后 <see cref="ScreenBackdrop.Aim"/>。
    /// </summary>
    /// <param name="window">目标窗口（内部 <c>EnsureHandle</c>；调用点须已定形 Left/Top/Width/Height）。</param>
    /// <param name="panelDip">面板在窗口客户区内的 DIP 矩形（本项目所有表面 <c>WindowStyle=None</c> ⇒ 客户区即窗口矩形）。</param>
    public static Brush? ForWindow(Window window, Rect panelDip)
    {
        var panelPx = PanelPhysicalRect(window, panelDip);
        if (panelPx is null) return null;
        var backdrop = ForPanel(panelPx.Value);
        if (backdrop is null) return null;
        var clone = (ImageBrush)backdrop.Brush.Clone();
        clone.Freeze();   // 一次性：面板不再长大，用重瞄能力换冻结
        return clone;
    }

    // ---------- 内部 ----------

    /// <summary>窗口客户区内的 DIP 矩形 → 屏幕物理矩形（缩放直接取"HWND 物理宽 / WPF DIP 宽"）。</summary>
    private static PhysicalRect? PanelPhysicalRect(Window window, Rect panelDip)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr)) return null;
        var winW = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        if (winW <= 0 || !double.IsFinite(winW)) return null;
        var scale = (wr.Right - wr.Left) / winW;
        if (!double.IsFinite(scale) || scale <= 0) return null;
        return new PhysicalRect(
            (int)Math.Round(wr.Left + panelDip.X * scale),
            (int)Math.Round(wr.Top + panelDip.Y * scale),
            (int)Math.Round(wr.Left + panelDip.Right * scale),
            (int)Math.Round(wr.Top + panelDip.Bottom * scale));
    }

    /// <summary>
    /// 捕获区 = 面板外扩 marginPx 后钳到该屏工作区；面板本身不在工作区内时改钳整屏
    /// （两块矩形的取舍见 <see cref="MonitorGeometry.AreaForPoint"/>）。
    /// </summary>
    private static PhysicalRect CaptureRegionFor(in PhysicalRect panelPx, double marginPx, MonitorArea area)
    {
        var m = (int)Math.Ceiling(Math.Max(0, marginPx));
        var inflated = new PhysicalRect(panelPx.Left - m, panelPx.Top - m, panelPx.Right + m, panelPx.Bottom + m);
        return Contains(area.Work, panelPx)
            ? Intersect(inflated, area.Work)
            : Intersect(inflated, area.Monitor);   // 面板压到任务栏/条带外：背板里混一条任务栏也比缺一块好
    }

    private static PhysicalRect Intersect(in PhysicalRect a, in PhysicalRect b)
        => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right),
               Math.Min(a.Bottom, b.Bottom));

    private static bool Contains(in PhysicalRect outer, in PhysicalRect inner)
        => outer.Left <= inner.Left && outer.Top <= inner.Top &&
           outer.Right >= inner.Right && outer.Bottom >= inner.Bottom;

    /// <summary>
    /// GDI 抓屏：区域一次性取进 top-down 32bpp DIB section，再拷进租借的托管缓冲；
    /// alpha 归一为 255（GDI 那侧恒 0，而屏幕内容是不透明的——留着 0 会让"越模糊越黑"，
    /// 见 <see cref="BoxBlur"/> 的 alpha 说明）。
    /// </summary>
    private static byte[] CaptureRegion(in PhysicalRect region, out bool gotPixels)
    {
        gotPixels = false;
        var w = region.Width;
        var h = region.Height;
        var len = w * h * BoxBlur.BytesPerPixel;
        var buf = ArrayPool<byte>.Shared.Rent(len);
        var screenDc = GetDC(IntPtr.Zero);         // NULL → 整个屏幕的 DC（虚拟桌面原点由 region 自己带）
        var memDc = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var oldObj = IntPtr.Zero;
        try
        {
            if (screenDc == IntPtr.Zero) return buf;
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return buf;
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,          // 负高 = top-down：第一行就是区域顶行，与 WPF/Core 的行主序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            };
            dib = CreateDIBSection(memDc, ref bmi, DibRgbColor, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return buf;
            // 顺序是硬要求：CreateDIBSection 只建位图对象，BitBlt 写的是 DC 的**当前**位图，
            // 所以必须先 SelectObject 再 BitBlt，否则像素落在原来的 1×1 compatible bitmap 上。
            oldObj = SelectObject(memDc, dib);
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, region.Left, region.Top, Srccopy | CaptureBlt))
                return buf;
            Marshal.Copy(bits, buf, 0, len);
            for (var i = 3; i < len; i += BoxBlur.BytesPerPixel) buf[i] = 255;
            gotPixels = true;
            return buf;
        }
        finally
        {
            // 句柄成对归还（驻留进程漏一个是句柄数单调上涨）：先把原对象选回去再删 DIB，
            // 仍被 DC 选中的位图 DeleteObject 会失败。
            if (oldObj != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>整块缓冲是否逐字节全零（采样点数固定 ≈4096，不为判定遍历几百万字节）。
    /// <para>
    /// 【遍历上界必须是 w·h·4，不能是 <c>bgra.Length</c>】传入的是 <see cref="ArrayPool{T}"/> 借来的缓冲，
    /// 实际长度 **≥** 请求长度且尾部是上一次归还未定时的旧字节；按 Length 扫会让"越界那一段里恰好有非零
    /// 残值"被当成"抓到了内容"，于是全零帧检测形同虚设（黑背板照旧铺上卡片）。判据本身仍是逐字节为 0，
    /// 只是把窗口收回到真正被 BitBlt 写过的像素上。
    /// </para>
    /// </summary>
    private static bool IsAllBlack(ReadOnlySpan<byte> bgra, int width, int height)
    {
        var len = Math.Min(bgra.Length, width * height * BoxBlur.BytesPerPixel);
        if (len <= 0) return true;
        var step = Math.Max(1, width * height / 4096) * BoxBlur.BytesPerPixel;
        for (var i = 0; i + 2 < len; i += step)
            if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0) return false;
        return true;
    }

    private static void AnnounceEnvSwitch()
    {
        if (_envSwitchAnnounced) return;
        _envSwitchAnnounced = true;
        Logger.Info("BEAUTIFUL_NOBLUR=1：抓屏背板已关闭，所有表面使用实心卡底（降级形态自检用）");
    }
}
