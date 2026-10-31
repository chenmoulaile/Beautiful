using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Animation;

namespace Beautiful.App.Animation;

/// <summary>
/// 与 <c>native/hook/Beautiful.Hook.dll</c>（MinGW 交叉编译的前置钩子层，协议 v2）配对的宿主端。
/// <para>
/// 【它改变了什么】alpha.15 及以前，最小化走 <c>EVENT_SYSTEM_MINIMIZESTART</c>——事件到手时
/// 原生"缩向任务栏"已经播完或正在播，我们的 genie 只是事后补播（真机反馈：两动画打架/白光/像没有）。
/// alpha.16 起 CBT 钩子在<b>原生动画开始之前</b>抓帧并阻断原生最小化（返回 1），由本端播 genie、
/// 播完静默落位——原生动画被真替换。alpha.17（协议 v2）把同一套"前置拦截"推广到三条路径：
/// <b>MIN</b>（阻断原生最小化，v1 语义不变）、<b>SHOW</b>（钩子在目标进程内、首帧上屏之前
/// 就地 Cloak 真窗口，宿主抓帧播入场替身、演完 Uncloak——原生"出现"被整个吃掉）、
/// <b>DESTROY</b>（窗口销毁前抓最后一帧，不阻断销毁，宿主拿帧播收拢替身）。
/// </para>
/// <para>
/// 【安全模型】DLL 是注入组件：装不上/加载失败/任何异常 → 本端如实降级回
/// 事后路径（WinEvent + 帧缓存），最小化/打开/关闭本身永远是系统的，我们只添动画。
/// SHOW 路径的额外保险：钩子侧 40ms 无确认就地 Uncloak；宿主侧 600ms 看门狗兜底 Uncloak——
/// 任何一层失职都不会让窗口"隐形卡死"。
/// </para>
/// <para>
/// 【共享内存布局】与 C 侧 <c>BH_SHARED / BH_SLOT</c> 逐字节对齐（x64：8 字节对齐），
/// 偏移量单一来源是 <see cref="HookProtocol"/>（Core 常量类 + 钉子测试）。
/// </para>
/// </summary>
internal sealed class MinimizeHookClient : IDisposable
{
    private const string SharedName = "Local\\Beautiful.Hook.Shared.v2";
    private const string AckName = "Local\\Beautiful.Hook.Ack.v2";
    private const string NotifyMessageName = "Beautiful.Hook.Notify.v2";

    private const int MaxWidth = HookProtocol.MaxWidth;
    private const int MaxHeight = HookProtocol.MaxHeight;

    [DllImport("Beautiful.Hook.dll", EntryPoint = "BH_Install")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BH_Install(IntPtr host, uint msg);

    [DllImport("Beautiful.Hook.dll", EntryPoint = "BH_Uninstall")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BH_Uninstall();

    /// <summary>钩子层的可用状态行（诊断块用）。null = 不可用。</summary>
    public string? Status { get; private set; }

    /// <summary>DLL 是否已成功加载（供 HookBridge 快速短路，避免每次调用抛异常）。</summary>
    public bool Available => Status is not null && Status.Contains("已挂载");

    private readonly MinimizeAnimator _animator;
    private readonly Dispatcher _dispatcher;
    private MemoryMappedFile? _shared;
    private MemoryMappedViewAccessor? _view;
    private EventWaitHandle? _ack;
    private HwndSource? _source;
    private IntPtr _hostHwnd;
    private uint _notifyMsg;
    private DispatcherTimer? _flagsTimer;
    private bool _disposed;

    /// <summary>SHOW 接管台账：hwnd → 接管代次（看门狗与迟到抓帧结果用它互相识别）。</summary>
    private readonly Dictionary<IntPtr, int> _cloakOwned = [];
    private int _showGeneration;

    public MinimizeHookClient(MinimizeAnimator animator)
    {
        _animator = animator;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var dllPath = Path.Combine(AppContext.BaseDirectory, "Beautiful.Hook.dll");
        if (!File.Exists(dllPath))
        {
            Status = "CBT 前置钩子不可用：Beautiful.Hook.dll 不在程序目录（回退事后路径）";
            Logger.Info(Status);
            return;
        }

        try
        {
            var sectionSize = HookProtocol.SectionBytes;
            _shared = MemoryMappedFile.CreateOrOpen(SharedName, sectionSize, MemoryMappedFileAccess.ReadWrite);
            _view = _shared.CreateViewAccessor(0, sectionSize, MemoryMappedFileAccess.ReadWrite);
            _view.Write(HookProtocol.OffsetMagic, HookProtocol.Magic);
            _view.Write(HookProtocol.OffsetVersion, HookProtocol.Version);
            _view.Write(HookProtocol.OffsetSlotBytes, MaxWidth * MaxHeight * 4);
            _view.Write(HookProtocol.OffsetSlotCount, HookProtocol.SlotCount);
            // HostHwnd(8) / NotifyMsg(16) 在拿到消息窗口后补写（BH_Install 之前必须就位）

            _ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckName, out var createdNew);
            if (!createdNew)
                Logger.Info("CBT 钩子：确认事件已存在（上一会话残留，直接复用）");

            // 消息窗口：只收钩子层的"我拦到了"通知，不参与任何 UI
            _notifyMsg = NativeMethods.RegisterWindowMessage(NotifyMessageName);
            _source = new HwndSource(new HwndSourceParameters("Beautiful.Hook.Host")
            {
                ParentWindow = new IntPtr(-3),          // HWND_MESSAGE
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            });
            _source.AddHook(WndProc);
            _hostHwnd = _source.Handle;
            _view.Write(HookProtocol.OffsetHostHwnd, _hostHwnd.ToInt64());
            _view.Write(HookProtocol.OffsetNotifyMsg, _notifyMsg);

            if (!BH_Install(_hostHwnd, _notifyMsg))
            {
                Status = "CBT 前置钩子：BH_Install 失败（SetWindowsHookEx 被拒？回退事后路径）";
                Logger.Warn(Status);
                return;
            }

            // Flags bit1 = 关闭动画开关：设置随时可改而钩子共享区必须实时知道，
            // 用一条 2s 定时器做读-改-写同步（4 字节对齐写是原子的，与 BH_Uninstall 的 bit0 互不打断）。
            _flagsTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background,
                (_, _) => SyncCloseFlag(), _dispatcher);
            SyncCloseFlag();
            _flagsTimer.Start();

            Status = "CBT 前置钩子已挂载（v2：最小化/打开/关闭 三路真替换）";
            Logger.Info(Status);
            HookBridge.Available = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                     or IOException or UnauthorizedAccessException
                                     or System.ComponentModel.Win32Exception)
        {
            Status = $"CBT 前置钩子不可用（{ex.GetType().Name}）：回退事后路径";
            Logger.Warn(Status);
            CleanupLocal();
        }
    }

    /// <summary>程序化最小化前的抑制开关（防钩子拦自家动作）。Dock 等路径共用。</summary>
    internal static void Suppress(IntPtr hwnd)
    {
        if (!HookBridge.Available) return;
        try { HookBridge.BH_Suppress(hwnd); }
        catch (DllNotFoundException) { HookBridge.Available = false; }
        catch (Exception ex) { Logger.Warn($"BH_Suppress 异常（已隔离）：{ex.Message}"); }
    }

    /// <summary>把 Flags bit1 同步成设置项 CloseAnimation 的实值（保留 bit0 退出位）。</summary>
    private void SyncCloseFlag()
    {
        if (_view is null) return;
        try
        {
            var flags = _view.ReadUInt32(HookProtocol.OffsetFlags);
            var desired = SettingsService.Current.CloseAnimation ? HookProtocol.FlagCloseEnabled : 0u;
            var next = (flags & ~HookProtocol.FlagCloseEnabled) | desired;
            if (next != flags)
            {
                _view.Write(HookProtocol.OffsetFlags, next);
                Logger.Info($"CBT 钩子：关闭动画拦截开关 → {(desired != 0 ? "开" : "关")}");
            }
        }
        catch (Exception)
        {
            // 共享区已被卸载侧清掉/异常形态：下一拍再试，静默
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != (int)_notifyMsg || _disposed) return IntPtr.Zero;
        handled = true;

        var seq = (uint)wParam.ToInt64();

        // MIN：先拷像素再确认（注入侧在 40ms 内等确认，拷帧 ≤14MB 远快于超时）；
        // SHOW：簿记（接管台账 + 看门狗）极快，确认照发——真正抓帧在 BeginInvoke 之后异步做；
        // DESTROY：注入侧不等待，确认照发无害。
        try { DispatchNotify(seq); }
        catch (Exception ex)
        {
            Logger.Warn($"CBT 钩子：通知分派异常（{ex.GetType().Name} {ex.Message}），放行原生路径");
        }
        finally
        {
            try { _ack?.Set(); }
            catch (ObjectDisposedException) { }
        }

        return IntPtr.Zero;
    }

    /// <summary>按槽头的 Kind 分派（协议 v2）。槽内容过期（另一窗口抢先写入）如实记日志放弃。</summary>
    private void DispatchNotify(uint seq)
    {
        if (_view is null) return;
        var slotBase = HookProtocol.SlotBase(seq);
        var ready = _view.ReadUInt32(slotBase);
        if (ready != seq)
        {
            Logger.Info($"CBT 钩子：槽内容过期 seq={seq}（双槽已被更新），放行");
            return;
        }

        var kind = _view.ReadUInt32(slotBase + HookProtocol.SlotOffsetKind);
        var target = (IntPtr)_view.ReadUInt64(slotBase + HookProtocol.SlotOffsetHwnd);
        var pid = _view.ReadUInt32(slotBase + HookProtocol.SlotOffsetPid);
        var l = _view.ReadInt32(slotBase + HookProtocol.SlotOffsetRectLeft);
        var t = _view.ReadInt32(slotBase + HookProtocol.SlotOffsetRectTop);
        var r = _view.ReadInt32(slotBase + HookProtocol.SlotOffsetRectRight);
        var b = _view.ReadInt32(slotBase + HookProtocol.SlotOffsetRectBottom);
        var rect = r > l && b > t ? new Rect(l, t, r - l, b - t) : Rect.Empty;

        switch (kind)
        {
            case HookProtocol.KindMinimize:
            {
                BitmapSource? image = null;
                try { image = TryReadFrame(seq, slotBase); }
                catch (Exception ex)
                {
                    Logger.Warn($"CBT 钩子：读帧失败（{ex.GetType().Name} {ex.Message}），放行原生路径");
                }
                _dispatcher.BeginInvoke(() => HandleNotify(target, image, rect), DispatcherPriority.Send);
                break;
            }

            case HookProtocol.KindDestroy:
            {
                BitmapSource? image = null;
                try { image = TryReadFrame(seq, slotBase); }
                catch (Exception ex)
                {
                    Logger.Warn($"CBT 钩子：关闭帧读取失败（{ex.GetType().Name} {ex.Message}）");
                }
                if (image is not null)
                {
                    _dispatcher.BeginInvoke(() => _animator.PlayCloseFromHook(target, pid, image!, rect),
                        DispatcherPriority.Send);
                }
                break;
            }

            case HookProtocol.KindShow:
            {
                _dispatcher.BeginInvoke(() => HandleShowNotify(target, rect), DispatcherPriority.Send);
                break;
            }
        }
    }

    /// <summary>
    /// CBT 最小化路径（协议 v1 语义不变）：注入侧已拦下原生最小化并交帧，本方法全权接管——
    /// 门禁放行就播 genie，播完静默落位；门禁拒绝/无帧也必须静默落位（原生最小化已被拦，
    /// 我们不落位窗口就缩不下去，比没有动画严重得多）。
    /// </summary>
    private void HandleNotify(IntPtr hwnd, BitmapSource? image, Rect slotRect)
    {
        try
        {
            if (hwnd == IntPtr.Zero || hwnd == _hostHwnd) return;

            // 优先用活矩形（窗口此刻还画着、已被拦住不动）；读不到再退钩子抓帧瞬间的矩形
            var rect = slotRect;
            if (NativeMethods.GetWindowRect(hwnd, out var live))
            {
                rect = new Rect(live.Left, live.Top, live.Right - live.Left, live.Bottom - live.Top);
            }
            else if (rect.Width <= 0)
            {
                _animator.SilentMinimize(hwnd, "CBT 接管但矩形失效");
                return;
            }

            _animator.PlayMinimizeFromHook(hwnd, image, rect);
        }
        catch (Exception ex)
        {
            // 已向注入侧确认接管，异常也必须把最小化做完：交给静默路径兜底
            Logger.Warn($"CBT 钩子：播发异常（{ex.GetType().Name} {ex.Message}），静默落位兜底");
            try { _animator.SilentMinimize(hwnd, "CBT 播发异常兜底"); } catch { /* 兜底再失败只能放弃 */ }
        }
    }

    /// <summary>
    /// CBT-SHOW 路径（协议 v2）：钩子已在目标进程内、首帧上屏之前把真窗口 Cloak。
    /// 本端：验活（钩子 40ms 超时会自己还原，那时我们不接）→ 记接管台账 + 起看门狗 →
    /// 后台抓帧（PrintWindow 驱动 Cloak 窗口渲染，绝不退屏幕路）→ 门禁放行播入场替身、
    /// 演完 Uncloak；抓帧失败/门禁拒绝/看门狗超时一律立即 Uncloak 放行原生显示。
    /// </summary>
    private void HandleShowNotify(IntPtr hwnd, Rect slotRect)
    {
        try
        {
            if (hwnd == IntPtr.Zero || hwnd == _hostHwnd || _disposed) return;
            if (!NativeMethods.IsWindow(hwnd))
            {
                Logger.Info("CBT 钩子：SHOW 窗口已销毁，无需接管");
                return;
            }

            // 钩子 40ms 没等到我们的确认会就地 Uncloak——那种情况下窗口已在屏幕上，
            // 再播入场动画就成了"事后补播"（用户点名不要的形态）。如实放弃。
            if (!IsCloaked(hwnd))
            {
                Logger.Info("CBT 钩子：SHOW 窗口未被 Cloak（钩子超时已还原），放行原生显示");
                return;
            }

            var generation = ++_showGeneration;
            _cloakOwned[hwnd] = generation;
            StartShowWatchdog(hwnd, generation);
            Logger.Info($"CBT 钩子：SHOW 接管 0x{hwnd.ToInt64():X}（代次 {generation}），后台抓帧中");

            var p = AnimParamsStore.Current;
            var attempts = Math.Max(1, p.CaptureRetryCount);
            var retryMs = Math.Max(1, p.CaptureRetryMs);
            var rect = slotRect;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                BitmapSource? image = null;
                for (var i = 1; i <= attempts; i++)
                {
                    image = WindowCapture.CapturePrintWindowOnly(hwnd);
                    if (image is not null) break;
                    System.Threading.Thread.Sleep(retryMs);
                }
                _dispatcher.BeginInvoke(() => FinishShow(hwnd, generation, image, rect),
                    DispatcherPriority.Send);
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"CBT 钩子：SHOW 接管异常（{ex.GetType().Name} {ex.Message}），Uncloak 放行");
            MinimizeAnimator.SetCloak(hwnd, false);
        }
    }

    /// <summary>后台抓帧完成：台账核对（看门狗可能已收摊）→ 交动画器播 → 拒播立即 Uncloak。</summary>
    private void FinishShow(IntPtr hwnd, int generation, BitmapSource? image, Rect slotRect)
    {
        if (!_cloakOwned.TryGetValue(hwnd, out var owned) || owned != generation) return;
        _cloakOwned.Remove(hwnd);   // 动画自己负责 Uncloak；看门狗到此为止

        if (image is null || !NativeMethods.IsWindow(hwnd))
        {
            Logger.Info(AnimReport.Skipped("CBT-SHOW", "抓帧", image is null ? "Cloak 窗口抓帧不可用" : "窗口已销毁",
                "立即 Uncloak 放行原生显示"));
            MinimizeAnimator.SetCloak(hwnd, false);
            return;
        }

        // 窗口在 Cloak 期间可能还在挪位：以此刻的活矩形为准（读不到用钩子瞬间矩形）
        var rect = slotRect;
        if (NativeMethods.GetWindowRect(hwnd, out var live) && live.Right > live.Left && live.Bottom > live.Top)
        {
            rect = new Rect(live.Left, live.Top, live.Right - live.Left, live.Bottom - live.Top);
        }

        if (!_animator.PlayOpenFromHook(hwnd, image, rect))
        {
            Logger.Info(AnimReport.Skipped("CBT-SHOW", "门禁", "动画器拒绝接管", "立即 Uncloak 放行原生显示"));
            MinimizeAnimator.SetCloak(hwnd, false);
        }
    }

    /// <summary>
    /// SHOW 看门狗：600ms 内抓帧/播放没有把台账销掉（PrintWindow 对挂死应用可能长期不返回），
    /// 强制 Uncloak——窗口宁可原生出现，也绝不允许"隐形卡死"。
    /// </summary>
    private void StartShowWatchdog(IntPtr hwnd, int generation)
    {
        DispatcherTimer watchdog = null!;
        watchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Send,
            (_, _) =>
            {
                watchdog.Stop();
                if (_cloakOwned.TryGetValue(hwnd, out var owned) && owned == generation)
                {
                    _cloakOwned.Remove(hwnd);
                    Logger.Warn("CBT 钩子：SHOW 看门狗触发（抓帧 600ms 未完成），Uncloak 放行原生显示");
                    MinimizeAnimator.SetCloak(hwnd, false);
                }
            }, _dispatcher);
        watchdog.Start();
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out var cloaked, 4) == 0
                   && cloaked != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private BitmapSource? TryReadFrame(uint seq, int slotBase)
    {
        if (_view is null) return null;
        var slotBytes = _view.ReadUInt32(HookProtocol.OffsetSlotBytes);
        if (slotBytes == 0) return null;
        var width = (int)_view.ReadUInt32(slotBase + HookProtocol.SlotOffsetWidth);
        var height = (int)_view.ReadUInt32(slotBase + HookProtocol.SlotOffsetHeight);
        var stride = (int)_view.ReadUInt32(slotBase + HookProtocol.SlotOffsetStride);
        if (width <= 0 || height <= 0 || stride < width * 4) return null;

        using var stream = _shared!.CreateViewStream(slotBase + HookProtocol.SlotHeaderBytes, slotBytes,
            MemoryMappedFileAccess.Read);
        var bytes = new byte[(long)stride * height];
        var validPerRow = width * 4;
        for (var y = 0; y < height; y++)
        {
            stream.Seek((long)y * stride, SeekOrigin.Begin);
            var read = stream.Read(bytes, y * stride, validPerRow);
            if (read < validPerRow) return null;
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
            null, bytes, stride);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { HookBridge.BH_Suppress(IntPtr.Zero); } catch { /* 尽力而为 */ }
        try { BH_Uninstall(); } catch { /* DLL 已不在：等效卸载 */ }
        try
        {
            // 退出时刻 SHOW 接管可能还挂着：把代管窗口全部还原（宁原生出现，不留隐形窗）
            foreach (var hwnd in _cloakOwned.Keys) MinimizeAnimator.SetCloak(hwnd, false);
            _cloakOwned.Clear();
        }
        catch { /* 收尾尽力而为 */ }
        _flagsTimer?.Stop();
        _flagsTimer = null;
        CleanupLocal();
    }

    private void CleanupLocal()
    {
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
        _view?.Dispose();
        _view = null;
        _shared?.Dispose();
        _shared = null;
        _ack?.Dispose();
        _ack = null;
    }
}

/// <summary>
/// 钩子层 P/Invoke 的静态桥（供动画器在无 <see cref="MinimizeHookClient"/> 实例时也能抑制）。
/// <see cref="Available"/> 由客户端在成功安装后置位；DLL 缺失时保持 false，调用方零开销短路。
/// </summary>
internal static class HookBridge
{
    internal static bool Available { get; set; }

    [DllImport("Beautiful.Hook.dll", EntryPoint = "BH_Suppress")]
    internal static extern void BH_Suppress(IntPtr hwnd);
}
