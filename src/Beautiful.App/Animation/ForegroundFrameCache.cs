using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Animation;

namespace Beautiful.App.Animation;

/// <summary>
/// 前台窗口的「最近一帧」缓存（v3.0 §Q2-3 的兜底捕获层）。
/// <para>
/// 【它补的是哪条缝】四条最小化触发路径里，只有 Dock 点击能让我们"抢在 <c>ShowWindow</c> 之前"抓帧；
/// 标题栏按钮、Win+↓、任务栏按钮这三条，事件到手时窗口已经 iconic，画面只剩 -16000 的占位。
/// 画面只能在窗口还在前台时先留一份——这就是本缓存存在的全部理由。
/// </para>
/// <para>
/// 【为什么有"龄"】留一份旧照片给"刚发生的最小化"用是合理的，给"三分钟前的最小化"用就是撒谎：
/// 窗口内容早换了，替身会播一段过期画面。所以每帧带抓取时间戳，超过
/// <see cref="AnimParams.ForegroundCacheMaxAgeMs"/> 只能当兜底（日志写龄），超过
/// <see cref="AnimParams.ForegroundCacheDiscardMs"/> 直接判废。
/// </para>
/// <para>
/// 【为什么分全尺寸/半尺寸】待播的那一窗要按原尺寸铺满屏幕（720px 宽已经会糊，但可接受）；
/// 其余的只服务预览与"万一用得上"，缩一半省内存。1 全 + 4 半是 v2.2 §5 的配比。
/// </para>
/// </summary>
internal sealed class ForegroundFrameCache
{
    /// <summary>样张最长边（物理 px）。720 ≈ 预览卡片 340 DIP 的 2 倍屏，再大是浪费。</summary>
    private const int FullMaxWidth = 720;

    private sealed class Entry
    {
        public required BitmapSource Image { get; set; }
        public required Rect Rect { get; set; }
        public required long Ticks { get; set; }
        public bool IsFull { get; set; }
    }

    private readonly Dictionary<IntPtr, Entry> _frames = new();
    private readonly List<IntPtr> _order = new();          // 最旧在前
    private readonly object _gate = new();

    private long _lastSampleTicks;
    private long _lastSameWindowTicks;

    public int Count { get { lock (_gate) return _frames.Count; } }

    /// <summary>存一帧（覆盖同窗口旧帧）。窗口矩形是抓这一帧时的屏幕物理矩形。</summary>
    public void Put(IntPtr hwnd, BitmapSource? image, Rect rectPhysical)
    {
        if (hwnd == IntPtr.Zero || image is null) return;
        var full = Downscale(image, FullMaxWidth);
        if (full is null) return;

        var p = AnimParamsStore.Current;
        lock (_gate)
        {
            // 已存在的全尺寸降级为半尺寸：只降"当前的全尺寸"这一张，代价恒定
            foreach (var key in _frames.Keys.ToList())
            {
                var e = _frames[key];
                if (e.IsFull && key != hwnd)
                {
                    var half = Downscale(e.Image, FullMaxWidth / 2);
                    if (half is not null) { e.Image = half; e.IsFull = false; }
                }
            }

            var entry = new Entry
            {
                Image = full,
                Rect = rectPhysical,
                Ticks = Stopwatch.GetTimestamp(),
                IsFull = true,
            };
            _frames[hwnd] = entry;
            _order.Remove(hwnd);
            _order.Add(hwnd);

            var capacity = Math.Max(1, p.CacheFullSizeCount) + Math.Max(0, p.CacheHalfSizeCount);
            while (_order.Count > capacity)
            {
                var oldest = _order[0];
                _order.RemoveAt(0);
                _frames.Remove(oldest);
            }
        }
    }

    /// <summary>取该窗口的帧；超过 <paramref name="maxAgeMs"/> 视为不可用。</summary>
    public bool TryGet(IntPtr hwnd, int maxAgeMs, out BitmapSource? image, out Rect rectPhysical, out double ageMs)
    {
        image = null;
        rectPhysical = Rect.Empty;
        ageMs = double.MaxValue;
        if (hwnd == IntPtr.Zero) return false;

        lock (_gate)
        {
            if (!_frames.TryGetValue(hwnd, out var entry)) return false;
            ageMs = (Stopwatch.GetTimestamp() - entry.Ticks) * 1000.0 / Stopwatch.Frequency;
            if (ageMs > maxAgeMs) return false;
            image = entry.Image;
            rectPhysical = entry.Rect;
            return true;
        }
    }

    /// <summary>清空（退出/锁屏/停用动画）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _order.Clear();
        }
    }

    /// <summary>该窗口的帧龄（没有则 null），诊断用。</summary>
    public double? AgeOf(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (!_frames.TryGetValue(hwnd, out var e)) return null;
            return (Stopwatch.GetTimestamp() - e.Ticks) * 1000.0 / Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// 抓一张前台窗口（节流：全局 <see cref="AnimParams.ForegroundRefreshMs"/>，同窗口至少 350ms）。
    /// <b>必须在后台线程调用</b>：<c>PrintWindow</c> 是同步阻塞调用，4K 窗口几十毫秒，
    /// 放在 UI 线程上等于每秒抽掉两三帧。
    /// </summary>
    public void SampleForeground(uint ownPid, string? foregroundProcess = null, bool fullscreen = false)
    {
        if (fullscreen) return;   // 全屏应用期间零轮询（省掉一整条捕获链的代价）

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || NativeMethods.IsIconic(hwnd)) return;
        if (!NativeMethods.IsWindowVisible(hwnd)) return;
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == ownPid) return;   // 自己的 Dock/顶栏不该进缓存

        _ = foregroundProcess;
        var p = AnimParamsStore.Current;
        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastSampleTicks) < Stopwatch.Frequency * p.ForegroundRefreshMs / 1000.0) return;
        if (now - Interlocked.Read(ref _lastSameWindowTicks) < Stopwatch.Frequency * 0.35) return;
        Interlocked.Exchange(ref _lastSampleTicks, now);
        Interlocked.Exchange(ref _lastSameWindowTicks, now);

        var image = WindowCapture.Capture(hwnd, out var layer);
        if (image is null)
        {
            Logger.Info(AnimReport.Skipped("前台取样", "无", "抓帧不可用"));
            return;
        }
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;
        Put(hwnd, image, new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
    }

    private static BitmapSource? Downscale(BitmapSource image, int maxWidth)
    {
        try
        {
            var w = image.PixelWidth;
            var h = image.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            if (w <= maxWidth)
            {
                if (image.IsFrozen) return image;
                var clone = image.Clone();
                clone.Freeze();
                return clone;
            }
            var scale = (double)maxWidth / w;
            var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var frozen = new WriteableBitmap(scaled);
            frozen.Freeze();
            return frozen;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException
                                      or NotSupportedException)
        {
            Logger.Warn($"前台缓存缩放失败（这一张不入缓存）：{ex.Message}");
            return null;
        }
    }
}
