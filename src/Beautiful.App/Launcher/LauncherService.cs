using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;   // Dispatcher：索引后台构建完成后回投 UI 线程
using Beautiful.App.Controls;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Launcher;
using static Beautiful.App.Interop.NativeMethods;
using Point = System.Windows.Point;   // UseWindowsForms 隐式 using 与 System.Drawing.Point 冲突
using Size = System.Windows.Size;     // 同上

namespace Beautiful.App.Launcher;

/// <summary>
/// 启动器的装配层（v3 T7）：索引的异步生命周期 + 覆盖层的开合 + 真启动。
/// <para>
/// 【为什么单独一层而不是塞进 App.xaml.cs】它同时握着三样有状态的东西（后台索引任务、弹层宿主、
/// "刚关过"抑制位），而这三样的清理时机各不相同。App 只保留组合根职责：装配它、把热键事件接过来、
/// 退出时 <see cref="Dispose"/>。与 <see cref="Switcher.SwitcherService"/> 同型。
/// </para>
/// <para>
/// 【线程模型】公开成员只在 WPF UI 线程调用（热键事件本身由钩子投递到 UI 线程消息泵，天然满足）。
/// 磁盘扫描在 <see cref="Task.Run"/> 上跑，完成后回 UI 线程落索引——UI 线程从头到尾没有一次
/// 目录枚举或 .lnk 解析（这条是切换器评审 I4 立下的规矩：按键路径零磁盘）。
/// </para>
/// </summary>
public sealed class LauncherService : IDisposable
{
    /// <summary>卡片顶边落在工作区的比例位置（上三分之一，Spotlight 的实际观感）。</summary>
    private const double TopThird = 1.0 / 3.0;

    /// <summary>越出可用高度时卡片与工作区下缘的留白（物理 px）。</summary>
    private const int BottomInset = 16;

    /// <summary>
    /// "刚关过就别立刻重开"的时间窗（毫秒）。顶栏入口按钮与控制中心面对的是同一颗竞态：
    /// 点按钮的那一刻本窗口失活 → 看门狗先把面板关掉，而 <c>Click</c> 可能在它之后才到，
    /// 若按"宿主已是 null"判断就会把刚关掉的面板又开一遍（用户看到"点了没反应"）。
    /// </summary>
    private const int ReopenGuardMs = 250;

    private readonly Dispatcher _ui;

    private LauncherIndex _index = LauncherIndex.Empty;
    private Task? _indexBuild;
    private bool _indexReady;

    private FluentPopupHost? _host;
    private LauncherOverlay? _panel;
    private DateTime _closedAt = DateTime.MinValue;
    private bool _disposed;

    public LauncherService()
    {
        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!_ui.CheckAccess())
            throw new InvalidOperationException("LauncherService 必须在 WPF UI 线程上构造。");
    }

    /// <summary>索引是否还在后台构建（面板的状态行据此说"正在建立索引…"而不是空白）。</summary>
    public bool IsIndexBuilding => !_indexReady;

    /// <summary>索引是否已就绪（Launchpad 打开时据此决定显示空状态还是网格）。</summary>
    public bool IsIndexReady => _indexReady;

    /// <summary>全部可启动应用（Build 后的确定次序）。Launchpad 复用同一份索引，不重复扫盘。</summary>
    public IReadOnlyList<LauncherApp> AllApps => _index.All;

    /// <summary>索引构建完成（UI 线程）；Launchpad/Spotlight 据此刷新空状态。</summary>
    public event Action? IndexChanged;

    /// <summary>
     /// 启动即预热索引（幂等，不阻塞）。放在 App 的启动序里而不是首次打开时：
     /// 第一次按热键就等 1 秒磁盘 IO 是"功能没反应"的观感，而这份扫描本来就得有人做。
     /// </summary>
    public void PrewarmIndex()
    {
        if (_disposed || _indexReady || _indexBuild is not null) return;
        // ScanAllAsync 内部两段：桌面开始菜单（磁盘 IO）+ MSIX 枚举（WinRT/COM）。
        // Task.Run 让它跑在线程池上，WinRT 的续接也落回线程池（这里没有同步上下文），
        // UI 线程一次目录枚举、一条 COM 都不碰——这台 VM 上 shell/WinRT 崩起来的代价是整进程，
        // 所以"绝不上 UI 线程"是本方法的硬约束（同 SmtcService 的线程契约纪律）。
        _indexBuild = Task.Run(LauncherIndexSource.ScanAllAsync)
            .ContinueWith(OnIndexBuilt, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnIndexBuilt(Task<IReadOnlyList<LauncherApp>> scan)
    {
        if (_disposed) return;   // 退出竞态：任务晚到也不要把东西塞回已经拆掉的界面
        if (scan.IsFaulted)
        {
            // 扫不动就保留空索引而不是抛出去：启动器还能开、还能显示"索引为空"，
            // 比让一个磁盘异常顺着 ContinueWith 冒进全局兜底更有用。
            Logger.Error("启动器：索引构建失败（本次运行内索引保持为空）", scan.Exception ?? new Exception("unknown"));
            _index = LauncherIndex.Empty;
        }
        else
        {
            _index = LauncherIndex.Build(scan.Result);
            Logger.Info($"启动器：索引就绪，{_index.Count} 条可启动项");

            // --launchpad-index-dump：把前 15 条的 显示名|启动路径 写进日志。
            // 用途：文件夹成员键 = 启动路径，手写/排查 config/launchpad.json 时得知道真实口径。
            if (Environment.GetCommandLineArgs()
                .Any(a => string.Equals(a, "--launchpad-index-dump", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var app in _index.All.Take(15))
                {
                    Logger.Info($"启动器索引：{app.Name} | {app.LaunchPath}");
                }
            }
        }
        _indexReady = true;
        IndexChanged?.Invoke();
        // 面板若正开着（用户没等索引就先打开了）：按当前查询重跑一次，绝不留一个"永远在建立索引"的脸
        _panel?.RefreshAfterIndexReady();
    }

    // ---------- 检索与图标（面板只经这两个口子拿数据） ----------

    /// <summary>按当前索引检索；索引未就绪时返回空（面板会显示"正在建立索引…"）。</summary>
    public IReadOnlyList<LauncherMatch> Search(string query, int limit)
        => _index.Search(query, limit);

    /// <summary>
    /// 结果行图标：<b>走 v3 T6 的提取管线（请求 256px，LRU 缓存）</b>，不在这里按 26/32px 重新提一次。
    /// 提取失败返回 null → 行画空框而不是编一个图标。
    /// </summary>
    public BitmapSource? IconOf(LauncherApp app, int size = IconExtractor.PreferredSize)
    {
        // 【size 参数】alpha.22 OOM 整改：调用方按"实际显示尺寸 × DPI"来要（启动台 ≈176、
        // 启动器列表 32），不再一刀切 256——位图内存按像素数平方下降，缓存键含尺寸不会串。
        // AppsFolder 条目（MSIX）的 LaunchPath 是 explorer.exe：拿它当图标源会让每个 UWP 应用都画成
        // 资源管理器图标——那是"错图标"，比"没图标"更糟。这类条目只认 IconPath（包内 logo 路径，
        // 读不到就是空框）。其余条目沿用"没有 IconPath 就用启动目标"的降级链。
        var isAppsFolder = LauncherTarget.AppsFolderAumidOf(app.LaunchPath, app.Arguments) is not null;
        var source = isAppsFolder
            ? app.IconPath
            : string.IsNullOrWhiteSpace(app.IconPath) ? app.LaunchPath : app.IconPath;
        return string.IsNullOrWhiteSpace(source) ? null : IconExtractor.GetIcon(source, size);
    }

    // ---------- 开合 ----------

    /// <summary>热键/入口按钮的统一动作：没开则开，开着则收。</summary>
    public void Toggle()
    {
        if (_disposed) return;
        if (_host is not null) { Close(); return; }
        if ((DateTime.UtcNow - _closedAt).TotalMilliseconds < ReopenGuardMs) return;
        Open();
    }

    /// <summary>
    /// 打开覆盖层：先建面板与宿主、<b>全部接线完成后</b>才 Show（本仓库被"半构造对象挂静态事件"咬过多次，
    /// 这里连实例事件也一律在 Show 之前接完、在 <see cref="OnHostClosed"/> 里成对解绑）。
    /// </summary>
    private void Open()
    {
        PrewarmIndex();
        var panel = new LauncherOverlay(this);
        var host = new FluentPopupHost();
        panel.AttachHost(host);
        host.Content = panel;
        host.KeepAliveProbe = () => host.IsActive || host.CursorInInflated(FluentPopupHost.RectInflate);
        host.Closed += OnHostClosed;
        _panel = panel;
        _host = host;

        // 落位：光标所在显示器的工作区，水平居中、顶边在上三分之一。
        // 尺寸与缩放基准与宿主内部同一套（WorkAreaOf/ScaleOfAnchor 已设为 internal，就是为了让启动器
        // 复用这条已在真机上验证过的每显示器 DPI 规则，而不是另写一份 GetSystemMetrics）。
        if (!GetCursorPos(out var cursorPoint)) cursorPoint = new POINT { X = 0, Y = 0 };
        var anchor = new Point(cursorPoint.X, cursorPoint.Y);
        var work = FluentPopupHost.WorkAreaOf(anchor);
        var scale = FluentPopupHost.ScaleOfAnchor(anchor, IntPtr.Zero);

        // 宿主用同步 Measure 的 DesiredSize 决定窗口尺寸，所以这里也用同一次测量算居中量，
        // 不依赖任何 dispatcher 冲刷时机（Show 后的 ActualWidth 那时还没定形）。
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var wPx = panel.DesiredSize.Width * scale;
        var hPx = panel.DesiredSize.Height * scale;

        var x = work.Left + Math.Max(0, (work.Width - wPx) / 2);
        if (x + wPx > work.Right) x = Math.Max(work.Left, work.Right - wPx);
        var y = work.Top + work.Height * TopThird;
        // 卡片可能因结果条数长到高过三分之一屏：把它抬到工作区里，而不是画到屏幕外
        if (y + hPx > work.Bottom - BottomInset) y = Math.Max(work.Top, work.Bottom - BottomInset - hPx);

        host.ShowAtPhysical(new Point(x, y), new Point(x, y));
        panel.FocusSearch();
        panel.RefreshInitial();
        Logger.Info($"启动器已打开（索引 {(_indexReady ? _index.Count.ToString() : "建立中")} 条）");
    }

    /// <summary>收起覆盖层（App 侧在"启动器被关掉"时调用它，绝不留一个还能打字却已经失效的面板）。</summary>
    public void Close()
    {
        if (_host is null) return;
        var host = _host;
        _host = null;   // 先摘引用：Close 会同步走 OnHostClosed，避免它把 null 当"刚被看门狗关掉"记两次
        _closedAt = DateTime.UtcNow;
        // OnHostClosed 会拆面板（唯一的清理点）：这里刻意不再重复 CleanupPanel，
        // 否则"谁负责拆"有两个答案，将来加释放逻辑就会漏一边。
        try { host.Close(); }
        catch (Exception ex) { Logger.Warn($"启动器：关闭异常: {ex.Message}"); }
    }

    /// <summary>
    /// 宿主关闭（Esc / 点别处 / 启动成功 / 看门狗）：清字段 → 解实例事件 → 拆面板，顺序与 <see cref="Open"/>
    /// 的接线一一对称。<see cref="FluentPopupHost.KeepAliveProbe"/> 由宿主自己在 OnClosed 里置 null，
    /// 这里不重复处理它（谁创建谁负责）。
    /// </summary>
    private void OnHostClosed(object? sender, EventArgs e)
    {
        if (sender is FluentPopupHost host) host.Closed -= OnHostClosed;
        if (ReferenceEquals(_host, sender)) _host = null;
        _closedAt = DateTime.UtcNow;
        CleanupPanel();
        Logger.Info("启动器已关闭");
    }

    private void CleanupPanel()
    {
        var panel = _panel;
        _panel = null;
        panel?.Shutdown();
    }

    // ---------- 启动 ----------

    /// <summary>
    /// 用 shell 启动解析出的目标（<see cref="ProcessStartInfo.UseShellExecute"/> = true，与
    /// <c>TopBarMenus.Launch</c> 同一条语义，因此 .lnk/文档/URL 三类目标都能开）。
    /// 失败一律就地接住并回原因：<b>调用方（面板）据此不收面板</b>，让用户立刻能改选另一条。
    /// 绝不 <c>cmd /c</c>（多一层进程、多一份引号歧义，且控制台会闪）。
    /// </summary>
    public bool Launch(LauncherApp app, out string? error)
    {
        error = null;
        try
        {
            var start = new ProcessStartInfo(app.LaunchPath)
            {
                UseShellExecute = true,
                Arguments = app.Arguments ?? string.Empty,
            };
            using var process = Process.Start(start);
            Logger.Info($"启动器：已启动 {app.Name} → {app.LaunchPath}");
            return process is not null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            Logger.Error($"启动器：启动失败 {app.Name}（{app.LaunchPath}）", ex);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_host is not null) Close();   // 幂等：OnHostClosed 再进来时字段已空
        Logger.Info("启动器已销毁");
    }
}
