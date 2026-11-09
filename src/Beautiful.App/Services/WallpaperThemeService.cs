using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Beautiful.App.Themes;
using Beautiful.Core.Design;
using Beautiful.Core.Imaging;
// UseWindowsForms 隐式全局 using 引入同名类型，定向消歧（与 FluentMenu.xaml.cs 同法）：
using Timer = System.Windows.Threading.DispatcherTimer;

namespace Beautiful.App.Services;

/// <summary>
/// 墙纸自适应取色服务（极致美化①）。开关开时把当前桌面壁纸的主色调成玻璃 tint，
/// 经 <see cref="ThemeManager.ApplyWallpaperTint"/> 混进 Dock 玻璃（Aurora 公式混入位 /
/// macOS 三停同权重混入）；关闭或取不出色时撤回 tint，玻璃回主题原生色。
/// <para>
/// 【三路触发，一个收敛点】① <see cref="SystemEvents.UserPreferenceChanged"/>（所有类别都进来
/// ——壁纸广播的确切类别不做假设，进来后只做"路径+改写时间"比对，没变就零成本返回）；
/// ② <see cref="App.SetWallpaperTint"/> 静态入口（设置页改开关/强度时调用，清缓存强制重提——
/// 强度变了 tint 就得重派生）；③ 60s 兜底复查定时器（SystemEvents 若在某个 Windows 版本上漏报
/// 壁纸变更，最慢一分钟内跟上）。三路都汇到 <see cref="Schedule"/> 的 1.5s 防抖拍上。
/// </para>
/// <para>
/// 【性能红线】提色全程在后台线程（UI 线程只收最终 hex 与比对缓存）：解码用
/// <c>DecodePixelWidth=64</c>（WIC 解码期降采样，4K 壁纸也只解出 64px 宽的缩略，
/// 与 LaunchpadWindow 的壁纸加载同一套路，位图 Freeze 后不跨线程），聚类在 ≤64×64 样本上做
/// ——单次成本毫秒级，远低于 500ms 红线。防抖之外还有完成冷却（幻灯片连页合并成
/// "占位等待 + 补一拍"），不会连环解码。
/// </para>
/// <para>
/// 【壁纸不变不重算】(路径, LastWriteTimeUtc) 缓存比对，全部共享状态只在 UI 线程读写
/// （后台线程只收参数、回结果）。提色失败（文件消失/解码失败/灰阶壁纸）同样缓存，避免坏输入
/// 每拍重解码；用户动设置时由入口②清缓存强制重试。
/// </para>
/// <para>
/// 【附属能力纪律】与顶栏/切换器同一条：监听装不上只记日志（退化为无实时跟随，启动时的
/// 一次性提色与兜底轮询仍然有效），绝不连坐主 UI。
/// </para>
/// </summary>
public sealed class WallpaperThemeService : IDisposable
{
    /// <summary>防抖窗口：触发后等这么久没新触发才落一拍（壁纸切换常伴随多条系统广播）。</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>两次提色完成的最小间隔：幻灯片换页节流，冷却期内的触发只记待办、跑完补一拍。</summary>
    private static readonly TimeSpan MinRunGap = TimeSpan.FromSeconds(3);

    /// <summary>兜底复查周期（SystemEvents 漏报时最慢这个时长内跟上；拍内只做一次比对）。</summary>
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>解码期降采样宽度（WIC 缩略解码，性能红线的主要保证；Core 管线自会再钳 64 边）。</summary>
    private const int DecodePixelWidth = 64;

    private readonly Dispatcher _dispatcher;
    private Timer? _debounce;
    private Timer? _poll;
    private bool _inFlight;          // 只在 UI 线程读写（RunAsync 的序列锚点）
    private bool _pendingRescan;     // 在途期间又来触发：跑完补一拍
    private DateTime _lastCompleted = DateTime.MinValue;
    private string? _lastPath;       // 提色缓存：路径 + 改写时间都没变就不重算
    private DateTime _lastWriteUtc;

    public static WallpaperThemeService? Current { get; private set; }

    public WallpaperThemeService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>启动监听（幂等）。必须在 WPF UI 线程调用（创建 DispatcherTimer）。开关开时立即排一拍。</summary>
    public void Start()
    {
        if (_debounce is not null) return;
        Current = this;
        _debounce = new Timer(DispatcherPriority.Background, _dispatcher) { Interval = DebounceDelay };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunAsync();
        };
        _poll = new Timer(DispatcherPriority.Background, _dispatcher) { Interval = FallbackPollInterval };
        _poll.Tick += (_, _) => Schedule();
        try
        {
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        }
        catch (Exception ex)
        {
            // 监听失败只影响"换壁纸实时跟随"：兜底轮询与设置入口仍在（降级日志，不连坐）
            Logger.Warn($"墙纸变更监听未启动（换壁纸将靠 {FallbackPollInterval.TotalSeconds:0}s 兜底复查跟随）: {ex.Message}");
        }

        _poll.Start();
        Schedule();
        Logger.Info("墙纸取色服务已启动");
    }

    /// <summary>
    /// 设置变更入口（App.SetWallpaperTint 静态入口调用，UI 线程）：关 = 撤 tint 回主题原生色；
    /// 开 = 清缓存强制重提（强度/开关变了，缓存的旧结果必须作废）。
    /// </summary>
    public void ApplySettings()
    {
        _lastPath = null;
        _lastWriteUtc = default;
        if (!SettingsService.Current.WallpaperTintEnabled)
        {
            _pendingRescan = false;
            ApplyTint(string.Empty);
            return;
        }

        Schedule();
    }

    /// <summary>收到任意用户偏好广播（SystemEvents 线程）：转 UI 线程排防抖拍。类别不做假设，
    /// 值不值得重算由 <see cref="RunAsync"/> 里的"路径+改写时间"比对裁决。</summary>
    private void OnPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
        => _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Schedule));

    /// <summary>排一拍（UI 线程）：开关关 = 零成本返回；开着就重置防抖计时器。</summary>
    private void Schedule()
    {
        if (_debounce is null || !SettingsService.Current.WallpaperTintEnabled) return;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>
    /// 防抖拍到：后台提色 → UI 线程落 tint。占住 <see cref="_inFlight"/> 后若正处冷却
    /// （<see cref="MinRunGap"/> 内刚跑过一拍，幻灯片连页场景）就先等满冷却再跑，等待期间的
    /// 新触发记待办、跑完自动补一拍——连环换页最终合并成"两拍"，不会连环解码。
    /// </summary>
    private async Task RunAsync()
    {
        if (_inFlight)
        {
            _pendingRescan = true;
            return;
        }

        _inFlight = true;
        try
        {
            var gap = DateTime.UtcNow - _lastCompleted;
            if (gap < MinRunGap) await Task.Delay(MinRunGap - gap);

            var path = await Task.Run(MachineInfoService.WallpaperPath);
            if (path is null)
            {
                _lastPath = null;
                _lastWriteUtc = default;
                ApplyTint(string.Empty);   // 幻灯片/纯色桌面：撤 tint，玻璃回主题原生色
                return;
            }

            var writeUtc = await Task.Run(() => SafeGetLastWrite(path));
            if (path == _lastPath && writeUtc == _lastWriteUtc) return;   // 壁纸不变不重算

            var glassBase = CurrentGlassBase();
            var tintHex = glassBase is null
                ? string.Empty
                : await Task.Run(() => DecodeAndExtract(path, glassBase.Value));
            _lastPath = path;
            _lastWriteUtc = writeUtc;   // 失败（hex=""）也缓存：坏输入不每拍重解码，动设置时入口会清缓存
            ApplyTint(tintHex);
            _lastCompleted = DateTime.UtcNow;
        }
        finally
        {
            _inFlight = false;
        }

        if (_pendingRescan)
        {
            _pendingRescan = false;
            Schedule();
        }
    }

    /// <summary>改写时间读取失败（文件正在被换）返回 DateTime.MinValue：与任何真实时间都不相等，
    /// 天然判定为"变了"，下一拍自然重试。</summary>
    private static DateTime SafeGetLastWrite(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// 后台线程：缩略解码 + Core 纯函数管线。返回最终 tint hex（"" = 撤回：文件消失/解码失败/
    /// 灰阶壁纸）。失败只静默返回空串（Apply 空串 = 撤回，与"取不出色"同一语义）。
    /// </summary>
    private static string DecodeAndExtract(string path, RgbColor glassBase)
    {
        try
        {
            // 与 LaunchpadWindow 的壁纸加载同套路（BeginInit/OnLoad/DecodePixelWidth/EndInit/Freeze）；
            // DecodePixelWidth=64 让 WIC 在解码期就缩到 64px 宽：4K JPEG 也只读一次文件 + 解 64px，
            // CopyPixels 出来 ≤64×64×4 = 16KB，Core 管线（降采样 + k-means）在亚毫秒量级收尾。
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // 立即读完释放文件句柄
            image.DecodePixelWidth = DecodePixelWidth;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();   // 冻结后不再有线程亲和（与 LaunchpadWindow 同一道防御）
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0) return string.Empty;

            var width = image.PixelWidth;
            var height = image.PixelHeight;
            var bgra = new byte[width * height * 4];
            image.CopyPixels(bgra, width * 4, 0);
            return WallpaperTint.TryCompute(bgra, width, height, glassBase, SettingsService.Current.WallpaperTintStrength,
                out var result)
                ? result.TintHex
                : string.Empty;
        }
        catch (Exception ex)
        {
            Logger.Info($"墙纸取色未生效：{ex.GetType().Name} {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>当前主题的玻璃基色（tint 的明度贴着它走）：Aurora 取 GlassBase token；
    /// macOS 没有显式基色 token，用玻璃三停的中间停作基色（明度语义一致）。</summary>
    private static RgbColor? CurrentGlassBase()
    {
        if (ThemeManager.Current.Id == "Aurora")
        {
            var tokens = AuroraTokens.For(ThemeManager.IsDark);
            return WallpaperTint.TryParseHex(tokens.GlassBase, out var r, out var g, out var b, out _)
                ? new RgbColor(r, g, b)
                : null;
        }

        // macOS：玻璃三停中间停 #B8242426 的 RGB（与 MacTokenDictionary.DockGlassStops[1] 同源）。
        return new RgbColor(0x24, 0x24, 0x26);
    }

    /// <summary>UI 线程：把 hex 落进 ThemeManager（"" = 撤回）。同值在此去重，兜底轮询/广播连发
    /// 不会反复重写 token。</summary>
    private void ApplyTint(string tintHex)
    {
        if (tintHex == ThemeManager.WallpaperTintHex) return;
        ThemeManager.ApplyWallpaperTint(tintHex, System.Windows.Application.Current);
    }

    /// <summary>停服务（退出链路，幂等）：退订系统广播、停两颗定时器。</summary>
    public void Dispose()
    {
        try
        {
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
        }
        catch (Exception ex)
        {
            Logger.Warn($"墙纸取色监听退订失败: {ex.Message}");
        }

        _debounce?.Stop();
        _debounce = null;
        _poll?.Stop();
        _poll = null;
        if (Current == this) Current = null;
    }
}
