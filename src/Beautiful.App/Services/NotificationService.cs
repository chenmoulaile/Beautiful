using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Beautiful.Core.Notifications;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Beautiful.App.Services;

/// <summary>系统通知读取的授权状态（对应 <c>UserNotificationListenerAccessStatus</c>）。</summary>
public enum NotificationAccess
{
    /// <summary>尚未答复（用户没点系统弹窗，或弹窗超时）。</summary>
    Unknown,

    /// <summary>已授权，可以读通知。</summary>
    Allowed,

    /// <summary>用户明确拒绝（系统里关掉了本应用的通知访问）。</summary>
    Denied,

    /// <summary>本机/本运行时拿不到监听器（投影缺失、系统版本过旧）。</summary>
    Unsupported
}

/// <summary>
/// 通知中心的数据服务：进程内直连 <see cref="UserNotificationListener"/>（Phase 4-T5）。
/// <para>
/// 【为什么是进程内而不是子进程】D:\MacDock（同作者的 C++ 版）实测过：MinGW 进程里 WinRT 的
/// IAsyncOperation 永不完成，必须开 .NET 子进程；而本项目本身就是 .NET 8 / CsWinRT 投影，
/// 同一个 API 直接 await 即可，不需要 sidecar、不需要行协议、不需要第二份 JSON 解析。
/// </para>
/// <para>
/// 【授权时机】首次打开通知中心时才 <c>RequestAccessAsync</c>，不在启动时弹系统授权框——
/// 启动即弹窗是最讨人厌的权限索取方式，而用户点开通知中心本就表达了"我想看通知"。
/// </para>
/// <para>
/// 【隐私】通知标题/正文只存在于内存与本进程 UI：<b>不写盘、不进日志</b>。
/// 日志只允许出现条数、跳过数、HRESULT、授权状态这些元信息（与 MacDock ADR-0025 同一约束）。
/// </para>
/// <para>
/// 【实时性】订阅 <see cref="UserNotificationListener.NotificationChanged"/>（事件可能来自非 UI 线程，
/// 这里做 400ms 合并后回 UI 线程刷新），另有 30s 兜底轮询——事件丢失时历史最多晚 30 秒。
/// </para>
/// </summary>
public sealed class NotificationService
{
    /// <summary>
    /// 轮询间隔。本机实测 <c>NotificationChanged</c> 在非打包进程里订阅就抛 0x80070490，
    /// 所以轮询事实上是主路径，取 10s（单次枚举 ~200ms，约 2% 单核占用；事件可用的机器上它只是补漏）。
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>事件合并窗口：一次推送风暴里连开十几条 toast 时只刷一次。</summary>
    private static readonly TimeSpan ChangeThrottle = TimeSpan.FromMilliseconds(400);

    /// <summary>等待用户答复系统授权弹窗的上限。</summary>
    private static readonly TimeSpan AccessTimeout = TimeSpan.FromSeconds(20);

    private readonly NotificationStore _store = new();
    private readonly Dispatcher _dispatcher;

    /// <summary>进程内单例（App 启动时赋值）：通知历史要在面板反复开关之间保持连续。</summary>
    public static NotificationService? Current { get; set; }

    private UserNotificationListener? _listener;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _changeTimer;
    private bool _refreshing;
    private string _status = "尚未启动";

    public NotificationService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    /// <summary>通知历史（仅内存，最新在前）。</summary>
    public NotificationStore Store => _store;

    /// <summary>授权状态。</summary>
    public NotificationAccess Access { get; private set; } = NotificationAccess.Unknown;

    /// <summary>给 UI 的一行说明（未授权/不支持时写明原因，绝不把"读不到"装作"没有通知"）。</summary>
    public string StatusText => _status;

    /// <summary>最近一次系统枚举返回的原始条数（诊断用，不含正文）。</summary>
    public int LastRawCount { get; private set; }

    /// <summary>最近一次枚举里因投影异常被跳过的条数。</summary>
    public int LastSkipped { get; private set; }

    /// <summary>数据或状态变化（UI 据此重画）。</summary>
    public event EventHandler? Updated;

    /// <summary>新到达的通知（每条一次）。横幅用它；首次同步建立基线不会触发。</summary>
    public event EventHandler<NotificationItem>? Arrived;

    /// <summary>是否已拿到可用的监听器。</summary>
    public bool Started => _listener is not null;

    /// <summary>
    /// 启动服务：请求授权 → 订阅事件 → 起轮询 → 首次同步（首次同步只建立基线，不当作新到达）。
    /// 返回是否成功拿到授权；失败原因写在 <see cref="StatusText"/>，不抛异常。
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (_listener is not null)
        {
            return true;
        }

        try
        {
            UserNotificationListener? listener;
            try
            {
                listener = UserNotificationListener.Current;
                Logger.Info("通知中心：listener 获取成功");
            }
            catch (Exception ex)
            {
                Logger.Warn($"通知中心：UserNotificationListener.Current 失败 " +
                            $"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
                throw;
            }

            if (listener is null)
            {
                Access = NotificationAccess.Unsupported;
                _status = "本机不支持通知读取";
                RaiseUpdated();
                return false;
            }

            // 先读既有状态：已授权时**不再**调 RequestAccessAsync——非打包进程在已授权状态下调它
            // 会抛 E_NOTFOUND(0x80070490)。这是本轮真机探针抓到的实测行为，不是猜测。
            UserNotificationListenerAccessStatus existing;
            try
            {
                existing = listener.GetAccessStatus();
                Logger.Info($"通知中心：既有授权状态={existing}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"通知中心：GetAccessStatus 失败 {ex.GetType().Name} 0x{ex.HResult:X8}");
                existing = UserNotificationListenerAccessStatus.Unspecified;
            }

            var access = existing == UserNotificationListenerAccessStatus.Allowed
                ? existing
                : await RequestAccessWithTimeoutAsync(listener);
            Access = access switch
            {
                UserNotificationListenerAccessStatus.Allowed => NotificationAccess.Allowed,
                UserNotificationListenerAccessStatus.Denied => NotificationAccess.Denied,
                _ => NotificationAccess.Unknown
            };

            if (Access != NotificationAccess.Allowed)
            {
                _status = Access == NotificationAccess.Denied
                    ? "通知访问已被系统拒绝"
                    : "等待系统授权答复";
                Logger.Warn($"通知中心：授权未通过（{Access}）");
                RaiseUpdated();
                return false;
            }

            _listener = listener;
            try
            {
                _listener.NotificationChanged += OnNotificationChanged;
                Logger.Info("通知中心：已订阅 NotificationChanged");
            }
            catch (Exception ex)
            {
                // 实测：非打包进程订阅该事件可能直接抛 E_NOTFOUND——降级为纯轮询，
                // 通知照样能出来，只是最多晚一个轮询周期。
                Logger.Warn($"通知中心：订阅变更事件失败，降级为轮询 {ex.GetType().Name} 0x{ex.HResult:X8}");
            }

            _pollTimer = new DispatcherTimer(PollInterval, DispatcherPriority.Background,
                (_, _) => _ = RefreshAsync(), _dispatcher);
            _pollTimer.Start();

            _status = "已连接系统通知";
            Logger.Info("通知中心：授权通过，开始同步");
            await RefreshAsync();
            Logger.Info($"通知中心：首次同步完成，历史 {_store.Count} 条");
            return true;
        }
        catch (Exception ex)
        {
            Access = NotificationAccess.Unsupported;
            _status = $"通知读取不可用（{ex.GetType().Name} 0x{ex.HResult:X8}）";
            Logger.Warn($"通知中心启动失败：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            RaiseUpdated();
            return false;
        }
    }

    /// <summary>拉一次系统通知并并入历史（首次调用只建立基线）。</summary>
    public async Task RefreshAsync()
    {
        if (_listener is null || _refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var raw = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var mapped = new List<NotificationItem>(raw.Count);
            var skipped = 0;

            foreach (var notification in raw)
            {
                try
                {
                    var item = Map(notification);
                    if (item is not null)
                    {
                        mapped.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    // 单条 toast 的投影异常不该让整次同步失败（也不该把正文写进日志）
                    skipped++;
                    Logger.Warn($"通知条目解析跳过：{ex.GetType().Name} 0x{ex.HResult:X8}");
                }
            }

            var added = _store.Apply(mapped);
            LastRawCount = raw.Count;
            LastSkipped = skipped;
            Logger.Info($"通知同步：系统 {raw.Count} 条，可用 {mapped.Count}，跳过 {skipped}，" +
                        $"新增 {added.Count}，历史 {_store.Count}");

            foreach (var item in added)
            {
                Arrived?.Invoke(this, item);
            }

            RaiseUpdated();
        }
        catch (Exception ex)
        {
            Logger.Warn($"通知同步失败：{ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>清空：系统通知中心（ClearNotifications）与本地历史一起清。</summary>
    public async Task ClearAllAsync()
    {
        try
        {
            _listener?.ClearNotifications();
        }
        catch (Exception ex)
        {
            Logger.Warn($"清空系统通知失败：{ex.GetType().Name} 0x{ex.HResult:X8}");
        }

        _store.Clear();
        RaiseUpdated();
        await Task.CompletedTask;
    }

    /// <summary>停止：退订事件、停轮询（窗口关闭/应用退出时调用）。</summary>
    public void Stop()
    {
        if (_listener is not null)
        {
            try
            {
                _listener.NotificationChanged -= OnNotificationChanged;
            }
            catch (Exception ex)
            {
                Logger.Warn($"通知退订失败：{ex.GetType().Name}");
            }

            _listener = null;
        }

        _pollTimer?.Stop();
        _pollTimer = null;
        _changeTimer?.Stop();
        _changeTimer = null;
    }

    /// <summary>
    /// 诊断探针：跑一次启动 + 同步，返回一行**不含通知正文**的汇总。
    /// 这是"先证据后 UI"的入口：真机上授权/枚举是否可用，必须先用它证明。
    /// </summary>
    public async Task<string> ProbeAsync()
    {
        var started = await StartAsync();
        if (started)
        {
            await RefreshAsync();
        }

        return $"access={Access} started={started} raw={LastRawCount} usable={_store.Count} " +
               $"skipped={LastSkipped} primed={_store.Primed} status={_status}";
    }

    private async Task<UserNotificationListenerAccessStatus> RequestAccessWithTimeoutAsync(
        UserNotificationListener listener)
    {
        var request = listener.RequestAccessAsync().AsTask();
        var completed = await Task.WhenAny(request, Task.Delay(AccessTimeout));
        if (completed != request)
        {
            Logger.Warn($"通知中心：授权请求 {AccessTimeout.TotalSeconds:0}s 未答复");
            return UserNotificationListenerAccessStatus.Unspecified;
        }

        return await request;
    }

    private static NotificationItem? Map(UserNotification notification)
    {
        if (notification is null)
        {
            return null;
        }

        var appId = string.Empty;
        var appName = string.Empty;
        try
        {
            var info = notification.AppInfo;
            appId = info?.AppUserModelId ?? string.Empty;
            appName = info?.DisplayInfo?.DisplayName ?? string.Empty;
        }
        catch (Exception)
        {
            // AppInfo 在部分 Win32 toast 上会抛（没有注册的 AUMID）：应用名留空，卡片退回"通知"
        }

        var texts = new List<string>(4);
        var visual = notification.Notification?.Visual;
        if (visual is not null)
        {
            foreach (var binding in visual.Bindings)
            {
                try
                {
                    foreach (var element in binding.GetTextElements())
                    {
                        if (!string.IsNullOrWhiteSpace(element?.Text))
                        {
                            texts.Add(element!.Text);
                        }
                    }
                }
                catch (Exception)
                {
                    // 某个 binding 不是文本型（进度条/图片）：跳过它，继续读其它 binding
                }
            }
        }

        var title = texts.Count > 0
            ? NotificationText.Clean(texts[0], NotificationText.MaxTitleChars)
            : string.Empty;
        var body = texts.Count > 1
            ? NotificationText.CleanMultiline(string.Join("\n", texts.GetRange(1, texts.Count - 1)),
                NotificationText.MaxBodyChars)
            : string.Empty;

        return new NotificationItem(
            notification.Id,
            NotificationText.Clean(appId, 128),
            NotificationText.Clean(appName, 80),
            title,
            body,
            notification.CreationTime);
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        // 事件来自系统线程：只做节流调度，不在这里碰 UI 与 Store。
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_changeTimer is null)
            {
                _changeTimer = new DispatcherTimer(ChangeThrottle, DispatcherPriority.Background,
                    (_, _) =>
                    {
                        _changeTimer?.Stop();
                        _ = RefreshAsync();
                    }, _dispatcher);
            }

            _changeTimer.Stop();
            _changeTimer.Start();
        }));
    }

    private void RaiseUpdated() => Updated?.Invoke(this, EventArgs.Empty);
}
