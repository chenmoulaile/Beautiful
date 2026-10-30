using System.IO;
using Beautiful.Core.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Beautiful.App.Services;

/// <summary>
/// SMTC（GSMTC，System Media Transport Controls）全局媒体会话服务——"自动兼容所有播放器"的核心。
/// 通过 <see cref="GlobalSystemMediaTransportControlsSessionManager.RequestAsync"/> 挂入系统媒体总线，
/// 任何注册了 SMTC 会话的播放器（Spotify / 网易云 / 浏览器 / Apple Music ...）都会成为可发现、可控的会话。
/// </summary>
/// <remarks>
/// <para>
/// <b>线程契约（重要）：</b>本服务<b>不触碰 WPF / Dispatcher</b>。WinRT 事件到达于线程池线程，
/// 所有对外通知（<see cref="SessionChanged"/> / <see cref="TrackChanged"/> / <see cref="PlayingChanged"/> /
/// <see cref="TimelineUpdated"/>）都是普通 <see cref="Action"/> 委托，在事件到达线程上直接触发；
/// 由消费者（媒体小组件 VM）自行编组到 UI 线程。
/// </para>
/// <para>
/// <b>TimelineUpdated 载荷约定：</b>按 (Position, LastUpdatedTime.UtcDateTime, Playing) <b>原样</b>透传，
/// 不预加 (now - lastUpdated) 的外推量——消费方的 <c>PlaybackClock.Estimate</c>（Task 5 设计）负责外推。
/// </para>
/// <para>
/// <b>错误策略：</b>会话可能在任何异步间隙被销毁（播放器退出 / 媒体切换），所有 WinRT 调用均 try/catch
/// 包裹并静默降级。会话失效是常态竞态而非故障，逐次记日志会刷屏；关键路径失败（初始化）由
/// MediaViewModel 记入 Logger（Task 17）。
/// </para>
/// <para>
/// <b>投影适配（net8.0-windows10.0.19041.0 的 Microsoft.Windows.SDK.NET.Ref 10.0.19041.56）：</b>
/// 19041 契约没有 <c>CurrentSession</c> 属性（用 <c>GetCurrentSession()</c> 方法）、没有会话 <c>Id</c>
/// （择会话回退用 GetSessions 尾元素近似"最新"）；<c>Thumbnail</c> 投影为 IRandomAccessStreamReference 接口
/// 且该版本投影无 AsStreamForRead 扩展（封面用 DataReader 分块读取）。
/// </para>
/// </remarks>
public sealed class SmtcService : IDisposable
{
    private const long MaxCoverBytes = 32 * 1024 * 1024; // 封面防御上限 32MB

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _disposed;

    /// <summary>当前是否绑定了一个媒体会话（false 表示系统当前没有任何可控制的播放器）。</summary>
    public bool HasSession => _session != null;

    /// <summary>绑定会话变化（含解绑为 null）。</summary>
    public event Action? SessionChanged;
    /// <summary>当前曲目的元数据刷新（封面异步加载完成后才发布）。</summary>
    public event Action<TrackMeta>? TrackChanged;
    /// <summary>播放/暂停状态变化。</summary>
    public event Action<bool>? PlayingChanged;
    /// <summary>时间线快照（原样透传，外推由消费方的 PlaybackClock 负责）。</summary>
    public event Action<(TimeSpan Pos, DateTime LastUpdatedUtc, bool Playing)>? TimelineUpdated;

    /// <summary>当前曲目元数据；无会话或尚未加载完成时为 null。</summary>
    public TrackMeta? Current { get; private set; }

    /// <summary>
    /// 请求系统媒体会话管理器并订阅事件，随后立即绑定当前会话（有则发布一次初始状态）。
    /// 重复调用为 no-op。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SmtcService));
        if (_mgr is not null) return; // 幂等
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _mgr = mgr;
        mgr.CurrentSessionChanged += OnCurrentSessionChanged;
        Bind(PickSession(mgr));
    }

    // ---------- 会话绑定 ----------

    /// <summary>切换绑定的会话（跨线程可达：来自线程池事件）。同一会话重复绑定为 no-op。</summary>
    private void Bind(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_disposed || ReferenceEquals(session, _session)) return;
        UnhookSession(_session);
        _session = session;
        Current = null; // 换绑/解绑均清空：避免新会话刷新失败时仍显示上一会话曲目的脏状态
        HookSession(_session);
        SessionChanged?.Invoke();
        _ = RefreshTrackAsync(); // fire-and-forget：封面加载是异步的，完成后经 TrackChanged 发布
        PublishTimeline();
    }

    private void HookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null) return;
        try
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        catch { /* 会话对象可能已失效：Hook 失败仅损失实时性 */ }
    }

    private void UnhookSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null) return;
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch { /* 会话对象可能已销毁：注销失败无需处理（对象随即被回收） */ }
    }

    /// <summary>
    /// 择会话策略：优先管理器的当前会话（GetCurrentSession()，19041 投影为方法）；
    /// 没有时回退 GetSessions() 尾元素近似"最新会话"（19041 契约无会话 Id 可排序）。
    /// </summary>
    private static GlobalSystemMediaTransportControlsSession? PickSession(GlobalSystemMediaTransportControlsSessionManager mgr)
    {
        GlobalSystemMediaTransportControlsSession? current = null;
        try { current = mgr.GetCurrentSession(); }
        catch { /* 管理器暂时不可用 */ }
        if (current is not null) return current;
        try { return mgr.GetSessions().LastOrDefault(); }
        catch { return null; }
    }

    // ---------- 管理器事件 ----------

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => Bind(PickSession(sender));

    // ---------- 会话事件 ----------

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => await RefreshTrackAsync();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
        => PublishTimeline();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        PublishPlaying(sender);
        PublishTimeline();
    }

    // ---------- 状态发布 ----------

    private void PublishPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var playing = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (!_disposed) PlayingChanged?.Invoke(playing);
        }
        catch { /* 会话可能已销毁 */ }
    }

    private void PublishTimeline()
    {
        if (_disposed) return;
        var session = _session;
        if (session is null) return;
        try
        {
            var tl = session.GetTimelineProperties();
            var playing = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            // 原样透传（LastUpdatedTime 投影为 DateTimeOffset，取 UtcDateTime）；外推归消费方 PlaybackClock.Estimate
            TimelineUpdated?.Invoke((tl.Position, tl.LastUpdatedTime.UtcDateTime, playing));
        }
        catch { /* 会话可能已销毁 */ }
    }

    /// <summary>拉取当前曲目的元数据与封面。会话在异步间隙失效时静默丢弃（不发布过期数据）。</summary>
    private async Task RefreshTrackAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (_disposed || !ReferenceEquals(session, _session)) return; // 期间已切换/销毁会话：丢弃过期结果

            byte[]? cover = null;
            var thumbnail = props.Thumbnail; // 投影为 IRandomAccessStreamReference 接口（勿对具体类做 is 模式匹配）
            if (thumbnail is not null)
                cover = await ReadCoverAsync(thumbnail);

            var tl = session.GetTimelineProperties();
            var meta = new TrackMeta(props.Title, props.Artist, props.AlbumTitle, tl.EndTime, cover);
            if (_disposed || !ReferenceEquals(session, _session)) return;
            Current = meta;
            TrackChanged?.Invoke(meta);
        }
        catch
        {
            // 会话可能在异步间隙被销毁（播放器退出/媒体切换）：静默降级
        }
    }

    /// <summary>封面 → byte[]（DataReader 分块读；该投影版本无 AsStreamForRead 扩展）。失败返回 null，不影响曲目信息。</summary>
    private static async Task<byte[]?> ReadCoverAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            var total = stream.Size; // 0 = 长度未知（个别播放器），按块读到上限
            if (total > (ulong)MaxCoverBytes) return null;
            using var reader = new DataReader(stream);
            using var ms = new MemoryStream();
            while (total == 0 || ms.Length < (long)total)
            {
                uint want = (uint)Math.Min(total > 0 ? (long)total - ms.Length : MaxCoverBytes, 64 * 1024);
                if (await reader.LoadAsync(want) == 0) break; // 流提前结束
                var chunk = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(chunk);
                ms.Write(chunk, 0, chunk.Length);
                if (ms.Length > MaxCoverBytes) return null; // Size==0（长度未知）时兜底：流谎报长度/永不结束也强制 32MB 上限
            }
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- 命令（由媒体小组件调用，任意线程可调） ----------

    public async Task PlayAsync()
    {
        var session = _session;
        if (session is null) return;
        try { await session.TryPlayAsync(); }
        catch { /* 会话可能已销毁 */ }
    }

    public async Task PauseAsync()
    {
        var session = _session;
        if (session is null) return;
        try { await session.TryPauseAsync(); }
        catch { /* 会话可能已销毁 */ }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null) return;
        try
        {
            var playing = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing) await PauseAsync();
            else await PlayAsync();
        }
        catch { /* 会话可能已销毁 */ }
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session is null) return;
        try { await session.TrySkipNextAsync(); }
        catch { /* 会话可能已销毁 */ }
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session is null) return;
        try { await session.TrySkipPreviousAsync(); }
        catch { /* 会话可能已销毁 */ }
    }

    /// <summary>
    /// 跳转播放位置。会话为 null、无播放控制能力或 IsPlaybackPositionEnabled=false 时返回 false；
    /// 否则调用 TryChangePlaybackPositionAsync（tick）并返回其结果。
    /// </summary>
    public async Task<bool> SeekAsync(TimeSpan pos)
    {
        var session = _session;
        if (session is null) return false;
        try
        {
            var controls = session.GetPlaybackInfo().Controls;
            if (controls is null || !controls.IsPlaybackPositionEnabled) return false;
            return await session.TryChangePlaybackPositionAsync(pos.Ticks);
        }
        catch
        {
            return false; // 会话可能已销毁
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_mgr is not null)
        {
            try { _mgr.CurrentSessionChanged -= OnCurrentSessionChanged; }
            catch { /* 管理器已失效：忽略 */ }
        }
        UnhookSession(_session);
        _session = null;
        _mgr = null;
    }
}
