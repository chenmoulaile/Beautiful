using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Services;
using Beautiful.Core.Media;
using Beautiful.Core.Timeline;

namespace Beautiful.App.MediaWidget;

/// <summary>
/// 媒体胶囊 + "正在播放"面板共用 VM。消费 <see cref="SmtcService"/>（WinRT 事件到达于线程池线程，
/// 全部经 Dispatcher 编组到 UI 线程后再触碰状态），<see cref="PlaybackClock"/> 负责位置外推，
/// 500ms DispatcherTimer 驱动进度。
/// <para>
/// v4：旧逐行文本引擎整体删除（Phase 1 T2）。面板回到 macOS "正在播放"语义——
/// 封面 + 标题/艺术家 + 进度 + 三键；不再有任何音乐服务网络请求。
/// </para>
/// </summary>
public sealed class MediaViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SmtcService _smtc;
    private readonly PlaybackClock _clock = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _tickTimer;
    private bool _seeking;       // 面板 Slider 交互期间：暂停定时器/时间线对 PositionSeconds 的回写（防抖结算后复位）
    private int _seekGen;        // seek 代际号：BeginSeek/换曲递增；结算时比对，飞行期间又有新交互/换曲则本结果作废
    private bool _seekFailed;    // 首次 SeekAsync 失败 → 本任务内隐藏进度条（换会话时重置）
    private bool _hasSession;
    private string _trackTitle = "";
    private string _artist = "";
    private string _capsuleText = "";
    private BitmapImage? _coverImage;
    private bool _isPlaying;
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _isSeekable = true;
    private bool _capsuleVisible;             // Dock 媒体胶囊显示开关（设置项）
    private bool _panelAutoHideOnDeactivate;  // 媒体面板失焦自动收起（设置项；MediaPanel 消费）

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand TogglePlayCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }

    public MediaViewModel(SmtcService smtc, Dispatcher dispatcher)
    {
        _smtc = smtc;
        _dispatcher = dispatcher;
        // 设置项初值（SettingsService.Load 先于本构造发生；App.OnStartup 顺序保证）
        var settings = SettingsService.Current;
        _capsuleVisible = settings.ShowMediaCapsule;
        _panelAutoHideOnDeactivate = settings.HidePanelOnDeactivate;

        TogglePlayCommand = new RelayCommand(_ => _ = _smtc.TogglePlayPauseAsync());
        NextCommand = new RelayCommand(_ => _ = _smtc.NextAsync());
        PrevCommand = new RelayCommand(_ => _ = _smtc.PreviousAsync());

        _smtc.SessionChanged += OnSessionChanged;
        _smtc.TrackChanged += OnTrackChanged;
        _smtc.PlayingChanged += OnPlayingChanged;
        _smtc.TimelineUpdated += OnTimelineUpdated;

        _tickTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _tickTimer.Tick += OnTick;

        _ = InitializeAsync(); // 构造期自启动 SMTC（组合根免显式接线；失败仅降级为无胶囊）
    }

    /// <summary>请求系统媒体会话管理器（SmtcService 内幂等；异常仅记录，不冒泡）。</summary>
    public async Task InitializeAsync()
    {
        try { await _smtc.InitializeAsync(); }
        catch (Exception ex) { Logger.Warn($"SMTC 初始化失败: {ex.Message}"); }
    }

    // ---------- 面板 / 胶囊交互入口 ----------

    /// <summary>面板 Slider 交互开始（点击跳转/拖动）：暂停定时器与时间线事件对 PositionSeconds 的回写，避免拖动被覆盖。</summary>
    public void BeginSeek()
    {
        _seekGen++;   // 在途的上一轮结算作废（其完成前又开始了新交互）
        _seeking = true;
    }

    /// <summary>面板关闭时放弃进行中的 Slider 交互，恢复进度回写。</summary>
    public void CancelSeek() => _seeking = false;

    public async Task SeekToAsync(TimeSpan pos)
    {
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (_durationSeconds > 0 && pos.TotalSeconds > _durationSeconds)
            pos = TimeSpan.FromSeconds(_durationSeconds);
        var gen = _seekGen;
        var ok = await _smtc.SeekAsync(pos);
        if (gen != _seekGen) return; // 过期结算：期间又有新交互/换曲，保持其挂起状态
        _seeking = false;            // 防抖结算完成：复位挂起（不复位则回写永久冻结），恢复定时器/时间线回写
        if (ok)
        {
            _clock.Seek(pos); // 乐观回拨本地时钟；SMTC 的 TimelinePropertiesChanged 随后纠偏
            SetPosition(pos);
        }
        else if (!_seekFailed)
        {
            _seekFailed = true;  // 会话不支持位置控制：首次失败即隐藏进度条
            IsSeekable = false;
        }
    }

    public void Dispose()
    {
        _tickTimer.Tick -= OnTick;
        _tickTimer.Stop();
        _smtc.SessionChanged -= OnSessionChanged;
        _smtc.TrackChanged -= OnTrackChanged;
        _smtc.PlayingChanged -= OnPlayingChanged;
        _smtc.TimelineUpdated -= OnTimelineUpdated;
    }

    // ---------- SmtcService 事件（线程池线程到达 → 全部编组到 UI 线程） ----------

    private void OnSessionChanged() => _dispatcher.BeginInvoke(ApplySessionState);
    private void OnTrackChanged(TrackMeta meta) => _dispatcher.BeginInvoke(() => ApplyTrack(meta));
    private void OnPlayingChanged(bool playing) => _dispatcher.BeginInvoke(() => ApplyPlaying(playing));
    private void OnTimelineUpdated((TimeSpan Pos, DateTime LastUpdatedUtc, bool Playing) snapshot)
        => _dispatcher.BeginInvoke(() => ApplyTimeline(snapshot.Pos, snapshot.LastUpdatedUtc, snapshot.Playing));

    // ---------- 状态应用（UI 线程） ----------

    private void ApplySessionState()
    {
        ClearTrack(); // 换绑/解绑一律先清旧曲目状态（新数据经 TrackChanged 异步到达）
        HasSession = _smtc.HasSession;
        if (HasSession) _tickTimer.Start();
        else _tickTimer.Stop();
    }

    private void ApplyTrack(TrackMeta meta)
    {
        // 播放器过渡期（如 bilibili 视频切换/广告位）会发布空元数据：忽略，保持上一曲目的显示
        if (string.IsNullOrWhiteSpace(meta.Title)) return;
        TrackTitle = meta.Title;
        Artist = meta.Artist;
        CapsuleText = string.IsNullOrWhiteSpace(meta.Artist) ? meta.Title : $"{meta.Artist} - {meta.Title}";
        CoverImage = DecodeCover(meta.CoverBytes);
        DurationSeconds = Math.Max(0, meta.Duration.TotalSeconds);
    }

    private void ApplyPlaying(bool playing)
    {
        _clock.SetPlaying(playing);
        IsPlaying = playing;
    }

    private void ApplyTimeline(TimeSpan pos, DateTime lastUpdatedUtc, bool playing)
    {
        // SmtcService 原样透传 (Position, LastUpdatedUtc)：Estimate 外推到"现在"再 Sync，
        // 之后由 PlaybackClock 按本地秒表继续外推
        _clock.Sync(PlaybackClock.Estimate(pos, lastUpdatedUtc, playing), playing);
        IsPlaying = playing;
        if (!_seeking) SetPosition(_clock.CurrentPosition);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!HasSession) return;
        if (!_seeking) SetPosition(_clock.CurrentPosition);
    }

    private void ClearTrack()
    {
        TrackTitle = "";
        Artist = "";
        CapsuleText = "";
        CoverImage = null;
        DurationSeconds = 0;
        SetPosition(TimeSpan.Zero);
        _clock.Sync(TimeSpan.Zero, false);
        IsPlaying = false;
        _seekGen++;          // 在途 seek 结算作废（其结果不得回写已清零的时钟/进度）
        _seeking = false;    // 换曲/换会话必解除 Slider 交互挂起（面板实例复用，Closed 未必触发）
        _seekFailed = false;
        IsSeekable = true;
    }

    private void SetPosition(TimeSpan pos)
    {
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (_durationSeconds > 0 && pos.TotalSeconds > _durationSeconds)
            pos = TimeSpan.FromSeconds(_durationSeconds);
        PositionSeconds = pos.TotalSeconds;
    }

    /// <summary>封面字节 → 冻结 BitmapImage（UI 线程解码；OnLoad 后流即可释放；按显示尺寸限宽解码）。</summary>
    private static BitmapImage? DecodeCover(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 256; // 64px 显示 × 高 DPI 余量；避免大图全量解码
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Logger.Warn($"封面解码失败: {ex.Message}");
            return null;
        }
    }

    // ---------- 绑定属性 ----------

    public bool HasSession
    {
        get => _hasSession;
        private set
        {
            if (_hasSession == value) return;
            _hasSession = value;
            RaisePropertyChanged();
        }
    }

    public string TrackTitle
    {
        get => _trackTitle;
        private set
        {
            if (_trackTitle == value) return;
            _trackTitle = value;
            RaisePropertyChanged();
        }
    }

    public string Artist
    {
        get => _artist;
        private set
        {
            if (_artist == value) return;
            _artist = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>胶囊跑马灯文本："Artist - Title"（无歌手时仅标题）。</summary>
    public string CapsuleText
    {
        get => _capsuleText;
        private set
        {
            if (_capsuleText == value) return;
            _capsuleText = value;
            RaisePropertyChanged();
        }
    }

    public BitmapImage? CoverImage
    {
        get => _coverImage;
        private set
        {
            if (ReferenceEquals(_coverImage, value)) return;
            _coverImage = value;
            RaisePropertyChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>进度条值（秒）；面板绑定单向（VM → Slider），用户 seek 由面板防抖后调 SeekToAsync 结算。</summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        private set
        {
            var v = value;
            if (v < 0) v = 0;
            if (_durationSeconds > 0 && v > _durationSeconds) v = _durationSeconds;
            if (Math.Abs(_positionSeconds - v) < 0.001) return;
            _positionSeconds = v;
            RaisePropertyChanged();
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (Math.Abs(_durationSeconds - value) < 0.001) return;
            _durationSeconds = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>false = 会话不支持位置控制（SeekAsync 首次失败）→ 面板隐藏进度条。</summary>
    public bool IsSeekable
    {
        get => _isSeekable;
        private set
        {
            if (_isSeekable == value) return;
            _isSeekable = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>Dock 是否显示媒体胶囊条目（设置项；MediaItemTemplate 宽度触发器消费）。</summary>
    public bool CapsuleVisible
    {
        get => _capsuleVisible;
        set
        {
            if (_capsuleVisible == value) return;
            _capsuleVisible = value;
            RaisePropertyChanged();
        }
    }

    /// <summary>媒体面板失焦是否自动收起（设置项；MediaPanel.Deactivated 消费）。</summary>
    public bool PanelAutoHideOnDeactivate
    {
        get => _panelAutoHideOnDeactivate;
        set
        {
            if (_panelAutoHideOnDeactivate == value) return;
            _panelAutoHideOnDeactivate = value;
            RaisePropertyChanged();
        }
    }

    private void RaisePropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class RelayCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }
}
