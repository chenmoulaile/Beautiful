using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using static Beautiful.App.Interop.NativeMethods;
using Rect = System.Windows.Rect;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧（与 StageBarWindow.xaml.cs 同法）：
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace Beautiful.App.StageManager;

/// <summary>
/// 台前调度·二级弹层（wp-macos-parity r6）：同应用多窗时，点卡片弹出的"该组全部窗口"列。
/// 每行 = 图标 + 窗口标题 + DWM 实况缩略图预留区；点行（或点行上的缩略）即切到该窗口，
/// 最小化的窗口自动还原（SwitchTo 统一处理）。
/// <para>
/// 【宿主血统与舞台条完全一致】本窗口是 WPF <b>分层</b>窗口（真机取证见 StageBarWindow.xaml
/// 头注释）。行缩略画面与舞台条卡片同走 <see cref="StageFramePump"/> 静帧链（round5 缺陷 4：
/// DWM 实况弹层在 26300 真机上注册全 0 却不合成像素）——Open 重建行 → 泵开跑拿首帧；
/// CloseQuiet / Dispose → 泵停拍。点击语义不变：整行（含缩略区）都在 WPF 的行 Border 里，
/// Row_Click 一条路切窗口。
/// </para>
/// <para>
/// 【生命周期】由舞台条（StageBarWindow）唯一持有：Open 复用同一实例（重建行 + 重落位），
/// CloseQuiet 收回（句柄全注销），Dispose 随舞台条销毁。显隐全部显式管理，不依赖任何
/// "owner 隐藏弹层自动藏"的不保证规则。落位矩形由舞台条按 <see cref="Core.StageManager.StageFlyoutPlan"/>
/// 算好传入（物理 px），本类不关心多屏几何。
/// </para>
/// <para>
/// 【线程模型】仅限 WPF UI 线程（与舞台条同泵）。
/// </para>
/// </summary>
public partial class StageGroupFlyoutWindow : Window
{
    // ---------- 视觉/布局常量（DIP；宿主算落位与行数上限时读同一份） ----------
    internal const double FlyoutWidthDip = 236;
    internal const double RowSlotHeightDip = 106;   // 行头 24 + 缩略区 64 + 收尾 7 + 描边 1.5×2 + 下边距 8
    internal const double PadVerticalDip = 16;      // 根 Border 上下 Padding 合计
    internal const int MaxRows = 6;                 // 弹层最多列 6 窗（超出按 MRU 丢弃，不做滚动）

    private readonly StageManagerService _service;
    private StageFramePump? _frames;                // 行静帧泵（Open 起跑；round5 缺陷 4 后替代 DWM 实况通道）
    private List<StageWindowRow> _rows = new();
    private bool _closed;

    /// <summary>点击了某行/某缩略并完成切换：宿主条借此安排延时收起。</summary>
    public event Action? SwitchedToWindow;

    public StageGroupFlyoutWindow(StageManagerService service)
    {
        InitializeComponent();
        _service = service ?? throw new ArgumentNullException(nameof(service));
        // 首帧防闪烁：与舞台条同一手法——Show() 前挪到屏外，Open() 再 SetWindowPos 精确落位。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -30000;
    }

    /// <summary>当前弹的是哪个组（AppId；null = 未开）。宿主据此做"再点同一组 = 收起"与快照对账。</summary>
    public string? GroupId { get; private set; }

    /// <summary>弹层是否在场（可见）。</summary>
    public bool IsOpen => IsVisible;

    /// <summary>宿主窗口句柄（玻璃弹层的 owner；未初始化返回 Zero）。</summary>
    public IntPtr WindowHandle => new WindowInteropHelper(this).Handle;

    /// <summary>光标（屏幕物理坐标）是否落在弹层当前矩形内（舞台条的自动隐藏轮询用）。</summary>
    public bool ScreenRectContains(int x, int y)
        => WindowHandle != IntPtr.Zero && GetWindowRect(WindowHandle, out var r)
           && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    // ---------- 显隐 ----------

    /// <summary>
    /// 建行 + 落位 + 显出 + 贴缩略（幂等复用同一窗口实例）。<paramref name="screenPhysicalRect"/>
    /// 为屏幕物理像素矩形（舞台条按 Core StageFlyoutPlan 算出）。
    /// </summary>
    public void Open(IReadOnlyList<StageWindowRow> rows, string groupId, Rect screenPhysicalRect)
    {
        if (_closed || rows.Count == 0) return;
        GroupId = groupId;
        _rows = new List<StageWindowRow>(rows);
        RowsControl.ItemsSource = _rows;

        // 高度先于落位定型：窗口高 = 上下内边距 + 行数×槽高（与舞台条 UpdateGroups 同一手法）。
        var newHeight = PadVerticalDip + _rows.Count * RowSlotHeightDip;
        if (Math.Abs(Height - newHeight) > 0.5) Height = newHeight;

        if (!IsVisible) Show();   // ShowActivated=False：不激活、不抢前台
        var hwnd = WindowHandle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HwndTopmost,
            (int)Math.Round(screenPhysicalRect.Left), (int)Math.Round(screenPhysicalRect.Top),
            (int)Math.Round(screenPhysicalRect.Width), (int)Math.Round(screenPhysicalRect.Height),
            SwpNoactivate | SwpShowWindow);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => EnsureFramePump().Start());
    }

    /// <summary>收回（幂等）：静帧泵停拍、自身隐藏。窗口实例保留待复用。</summary>
    public void CloseQuiet()
    {
        GroupId = null;
        _frames?.Stop();
        if (IsVisible) Hide();
    }

    /// <summary>宿主条销毁时的收口：静帧泵销毁 + 关窗（幂等）。</summary>
    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        CloseQuiet();
        _frames?.Dispose();
        _frames = null;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _frames?.Dispose();
        _frames = null;
        base.OnClosed(e);
    }

    // ---------- 行静帧（StageFramePump，round5 缺陷 4 后替代 DWM 实况弹层） ----------

    /// <summary>
    /// 静帧泵惰性建（Open 首拍才需要）。目标集每拍现场取 <see cref="FrameTargets"/>——
    /// 行模型随 Open 重建，泵绝不持有过期行。
    /// </summary>
    private StageFramePump EnsureFramePump()
        => _frames ??= new StageFramePump(Dispatcher, TimeSpan.FromSeconds(1), FrameTargets);

    /// <summary>
    /// 本拍抓帧目标：每一行（窗口句柄 → <see cref="StageWindowRow.Frame"/>）。句柄失效/为零的行跳过。
    /// </summary>
    private IReadOnlyList<(IntPtr Hwnd, Action<BitmapSource?> Apply)> FrameTargets()
    {
        var list = new List<(IntPtr, Action<BitmapSource?>)>(_rows.Count);
        foreach (var row in _rows)
        {
            var r = row;
            if (r.Handle != IntPtr.Zero)
                list.Add((r.Handle, frame => r.Frame = frame));
        }
        return list;
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { DataContext: StageWindowRow row }) return;
        SwitchTo(row.Handle);
    }

    private void SwitchTo(IntPtr hwnd)
    {
        if (_closed) return;
        _service.SwitchTo(hwnd);
        SwitchedToWindow?.Invoke();
        CloseQuiet();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // 兜底路：弹层持有焦点（点过行但切换被压制）时 Esc 就地收回；主路是舞台条的全局 LL 钩子。
        if (e.Key == Key.Escape)
        {
            CloseQuiet();
            e.Handled = true;
        }
    }

    // ---------- 行数据 ----------

    /// <summary>
    /// 组 → 窗口行（MRU 序，截到 <paramref name="maxRows"/>）。所有行共用该应用的图标；
    /// 空标题回落应用名（绝不出现空白行头）。
    /// </summary>
    public static List<StageWindowRow> BuildRows(
        StageBarWindow.StageGroupItem group, int maxRows, BitmapSource? icon)
    {
        ArgumentNullException.ThrowIfNull(group);
        var rows = new List<StageWindowRow>(Math.Min(maxRows, group.Windows.Count));
        for (int i = 0; i < group.Windows.Count && rows.Count < maxRows; i++)
            rows.Add(new StageWindowRow(group.Windows[i], icon));
        return rows;
    }

    /// <summary>二级弹层一行的数据模型（快照 → UI 的唯一映射）。</summary>
    public sealed class StageWindowRow : INotifyPropertyChanged
    {
        public StageWindowRow(Core.StageManager.StageWindow window, BitmapSource? icon)
        {
            Handle = window.Handle;
            IsMinimized = window.IsMinimized;
            Icon = icon;
            var title = window.Title;
            TitleLabel = title.Length > 0 ? title : window.AppName;
            if (IsMinimized) TitleLabel += "（已最小化）";
        }

        public IntPtr Handle { get; }       // 点击切换的目标
        public string TitleLabel { get; }
        public bool IsMinimized { get; }
        public BitmapSource? Icon { get; }
        public bool HasIcon => Icon is not null;

        private BitmapSource? _frame;

        /// <summary>
        /// 该行窗口的静帧（<see cref="StageFramePump"/> 抓取落地；null = 还没抓到，
        /// ThumbArea 露占位底色）。INPC：泵在后台抓帧、回 UI 线程落这一个属性，行画面原地更新。
        /// </summary>
        public BitmapSource? Frame
        {
            get => _frame;
            set
            {
                if (ReferenceEquals(_frame, value)) return;
                _frame = value;
                OnPropertyChanged(nameof(Frame));
                OnPropertyChanged(nameof(HasFrame));
            }
        }

        public bool HasFrame => Frame is not null;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
