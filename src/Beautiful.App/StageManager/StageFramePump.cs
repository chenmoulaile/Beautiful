using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;

namespace Beautiful.App.StageManager;

/// <summary>
/// 台前调度卡片的<b>静帧画面泵</b>（round5 缺陷 4 的修口）：按节拍把每张卡片/每一行的<b>源窗口
/// 当前画面</b>抓成冻结位图，交回 UI 线程落到行模型（<c>Frame</c>），由 WPF 卡片 <c>ThumbArea</c>
/// 里的 Image 呈现。
/// <para>
/// 【为什么弃用 DWM 实况通道】舞台条/二级弹层原走 <c>DwmRegisterThumbnail</c> 玻璃弹层
/// （StageThumbHostWindow + StageThumbStrip，ThumbnailPreview/DwmLiveGhost 同血统），
/// 真机（Win11 26300）验收实拍：注册/写属性 HRESULT 全 0（验收日志无一条失败），屏幕上
/// 缩略矩形却是<b>纯黑</b>——与 round2 在 24H2 上记录的"DWM 玻璃宿主三步全 0 却不合成"
/// 同一失效模式在新系统上对这套弹层宿主复现。DWM 通道"是否真合成"无法程序化探测
/// （ThumbnailPreview 缺陷 2 的既有结论），静帧链是仓库里唯一被真机目视验证过出画面的路
/// （StillPreviewWindow，round2 落地后悬停预览即修复）。两害相权：实况通道坏在哪台机器上
/// 不可预知且表现为黑块，静帧链在 PrintWindow 判废/失败时诚实地退回占位底色——选后者。
/// 点击语义不变：缩略矩形在卡片/行 Border 内，点击本就走 WPF 的 Row_Click/卡片点击。
/// </para>
/// <para>
/// 【画面新鲜度（round7 缺陷 3 加固）】常态 1s 一拍（悬停预览 StillPreviewWindow 的 1.2s 同档）；
/// 展开/弹层打开的瞬间 <see cref="Start"/> 先抓一拍，首帧不空等。抓帧在后台线程串行跑
/// （PrintWindow 是同步 GDI 调用，N 张卡片顺序抓），每帧冻结后 <see cref="Dispatcher.BeginInvoke"/>
/// 回 UI 线程落地——与 StillPreviewWindow 的 <c>RefreshAsync</c> 同一条线程纪律。
/// 两条"首帧必发"的时序保证：① 上一拍还在抓（<c>_busy</c>）时新拍不再静默丢弃，短延迟补发，
/// busy 一解除立刻补上——Start() 承诺的首拍不该被前拍的尾巴吞掉；② <b>首帧争夺期</b>（本次
/// Start 起还没落地过任何一帧）里整拍抓空时 150ms 内快速补拍（上限 3 次）——首拍撞上目标窗口
/// 的抓空过渡（DWM 未就绪/刚被遮挡/WinUI3 首帧 PrintWindow 纯色判废）时，等满一个节拍就是
/// 肉眼可见的黑卡；补拍仍失败则回落常态节拍，目标真不可抓时不空转。
/// </para>
/// <para>
/// 【失败语义】单张抓帧失败/判废 → 保留该卡片上一帧（位图引用还在行模型上）；行模型实例
/// 已被下一拍快照换掉时，迟到的帧落在旧实例上自然丢弃（apply 闭包绑的是抓帧时刻的实例）。
/// 目标句柄失效/为空由调用方的 targets 提供端过滤。
/// </para>
/// <para>
/// 【线程模型】构造/Start/Stop 仅限 WPF UI 线程（与舞台条同泵）；抓帧在线程池。
/// </para>
/// </summary>
internal sealed class StageFramePump : IDisposable
{
    /// <summary>一拍的目标集：源窗口句柄 + 该帧落地到哪个行模型（调用方每拍现场取，避免持有过期行）。</summary>
    public delegate IReadOnlyList<(IntPtr Hwnd, Action<BitmapSource?> Apply)> TargetProvider();

    private const int BusyRetryMs = 120;        // busy 撞车后的补发延迟（展开首拍被前拍尾巴吞掉的时序修正）
    private const int FirstFrameRetryMs = 150;  // 首帧争夺期的补拍间隔（1s 常态节拍下黑窗太长）
    private const int MaxFastRetries = 3;       // 首帧补拍上限：目标真不可抓时回落常态节拍，不空转

    private readonly Dispatcher _dispatcher;
    private readonly TargetProvider _targets;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _retryTimer;   // 一次性重试（busy 补发/首帧补拍共用一支，防叠加）
    private bool _pumping;                          // Start/Stop 语义旗：停拍后重试与收尾补拍一律不再发
    private bool _busy;
    private bool _everLanded;                       // 本次 Start 起是否落地过至少一帧（首帧争夺期判定）
    private int _fastRetries;                       // 争夺期内已补拍次数
    private bool _disposed;

    /// <param name="dispatcher">UI 泵（行模型只在它上面变更）。</param>
    /// <param name="interval">节拍（1s 档；悬停预览 1.2s 同级）。</param>
    /// <param name="targets">每拍现场取目标集（必须只在 UI 线程调用）。</param>
    public StageFramePump(Dispatcher dispatcher, TimeSpan interval, TargetProvider targets)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
        _retryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(FirstFrameRetryMs)
        };
        _retryTimer.Tick += (_, _) => { _retryTimer.Stop(); Tick(); };
    }

    /// <summary>开跑（幂等）：先抓一拍（首帧不空等一个节拍周期），再挂常拍。</summary>
    public void Start()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _everLanded = false;    // 每次开跑重新进入首帧争夺期：展开瞬间的黑窗不该等满一个节拍
        _fastRetries = 0;
        _retryTimer.Stop();
        _pumping = true;
        _timer.Stop();      // 已在跑时重开 = 重置节拍相位，并保证展开瞬间的首帧必发
        _timer.Start();
        Tick();
    }

    /// <summary>停拍（收起/让位/关窗）：进行中的一拍让它自然收尾（迟到帧落旧实例，无害），
    /// 但收尾不再安排任何补拍。</summary>
    public void Stop()
    {
        _dispatcher.VerifyAccess();
        _pumping = false;
        _timer.Stop();
        _retryTimer.Stop();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _pumping = false;
        _timer.Stop();
        _retryTimer.Stop();
    }

    private void Tick()
    {
        if (_disposed || !_pumping) return;
        if (_busy)
        {
            // 上一拍还在抓（PrintWindow 慢窗口/N 张卡时可能超过一个节拍）：绝不把"展开首拍"
            // 静默吞掉，短延迟补发，busy 一解除立刻补上。
            ScheduleRetry(BusyRetryMs);
            return;
        }
        var targets = _targets();
        if (targets.Count == 0) return;

        _busy = true;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            int landed = 0;
            try
            {
                foreach (var (hwnd, apply) in targets)
                {
                    if (_disposed) return;
                    BitmapSource? frame = null;
                    try
                    {
                        frame = WindowCapture.Capture(hwnd);
                    }
                    catch
                    {
                        // 抓帧失败就保留该卡片上一帧：宁可画面旧一点，也不闪回占位空块
                    }

                    if (frame is null) continue;
                    landed++;
                    var captured = frame;
                    _dispatcher.BeginInvoke(new Action(() => apply(captured)));
                }
            }
            finally
            {
                _dispatcher.BeginInvoke(new Action(() => { _busy = false; TickCompleted(landed); }));
            }
        });
    }

    /// <summary>一拍收尾（UI 线程）：有帧落地清零争夺期；整拍抓空且首帧还没着落 → 快速补拍。</summary>
    private void TickCompleted(int landed)
    {
        if (_disposed) return;
        if (landed > 0)
        {
            _everLanded = true;
            _fastRetries = 0;
            return;
        }
        // 首帧争夺期：目标窗口可能正处抓空过渡，等下一个常态节拍就是肉眼可见的黑卡。
        // 快速补拍几次（上限防"目标真不可抓"空转），仍失败就回落 1s 常态节拍。
        if (_pumping && !_everLanded && _fastRetries < MaxFastRetries)
        {
            _fastRetries++;
            ScheduleRetry(FirstFrameRetryMs);
        }
    }

    /// <summary>安排一次延迟重试（UI 线程）；已有重试在途时不叠加（busy 补发与首帧补拍共用一支）。</summary>
    private void ScheduleRetry(int delayMs)
    {
        if (_disposed || !_pumping) return;
        if (_retryTimer.IsEnabled) return;
        _retryTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _retryTimer.Start();
    }
}
