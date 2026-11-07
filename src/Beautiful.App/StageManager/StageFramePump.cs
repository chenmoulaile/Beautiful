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
/// 【画面新鲜度】1s 一拍（悬停预览 StillPreviewWindow 的 1.2s 同档）；展开/弹层打开的瞬间
/// <see cref="Start"/> 先抓一拍，首帧不空等。抓帧在后台线程串行跑（PrintWindow 是同步 GDI 调用，
/// N 张卡片顺序抓），每帧冻结后 <see cref="Dispatcher.BeginInvoke"/> 回 UI 线程落地——
/// 与 StillPreviewWindow 的 <c>RefreshAsync</c> 同一条线程纪律。上一拍还没抓完时跳过本拍
/// （<c>_busy</c>），绝不排队堆积。
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

    private readonly Dispatcher _dispatcher;
    private readonly TargetProvider _targets;
    private readonly DispatcherTimer _timer;
    private bool _busy;
    private bool _disposed;

    /// <param name="dispatcher">UI 泵（行模型只在它上面变更）。</param>
    /// <param name="interval">节拍（1s 档；悬停预览 1.2s 同级）。</param>
    /// <param name="targets">每拍现场取目标集（必须只在 UI 线程调用）。</param>
    public StageFramePump(Dispatcher dispatcher, TimeSpan interval, TargetProvider targets)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _targets = targets ?? throw new ArgumentNullException(nameof(targets));
        _timer = new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => Tick(), dispatcher);
    }

    /// <summary>开跑（幂等）：先抓一拍（首帧不空等一个节拍周期），再挂常拍。</summary>
    public void Start()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _timer.Stop();      // 已在跑时重开 = 重置节拍相位，并保证展开瞬间的首帧必发
        _timer.Start();
        Tick();
    }

    /// <summary>停拍（收起/让位/关窗）：进行中的一拍让它自然收尾（迟到帧落旧实例，无害）。</summary>
    public void Stop()
    {
        _dispatcher.VerifyAccess();
        _timer.Stop();
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }

    private void Tick()
    {
        if (_disposed || _busy) return;
        var targets = _targets();
        if (targets.Count == 0) return;

        _busy = true;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
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
                    var captured = frame;
                    _dispatcher.BeginInvoke(new Action(() => apply(captured)));
                }
            }
            finally
            {
                _dispatcher.BeginInvoke(new Action(() => _busy = false));
            }
        });
    }
}
