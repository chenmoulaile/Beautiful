using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Beautiful.App.Controls;   // FluentPopupHost 宿主
using Beautiful.App.Services;
using Beautiful.Core.Launcher;
using Beautiful.Core.Layout;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;   // UseWindowsForms 隐式 using 与 Forms.KeyEventArgs 冲突
using Brush = System.Windows.Media.Brush;                 // 同上，System.Drawing.Brush
using Brushes = System.Windows.Media.Brushes;             // 同上，System.Drawing.Brushes
using UserControl = System.Windows.Controls.UserControl;

namespace Beautiful.App.Launcher;

/// <summary>
/// 启动器覆盖层的内容面板（v3 T7）：一个输入框 + 一列结果，由 <see cref="FluentPopupHost"/> 承载。
/// <para>
/// 【瞬态生命周期，与控制中心同一套约定】每次打开新建实例、关闭由 <see cref="LauncherService"/> 调
/// <see cref="Shutdown"/>；本类<b>不订阅任何静态事件</b>（要接的线全在实例事件与宿主上，成对解绑在
/// <see cref="LauncherService"/> 的关闭回调里做）。状态更新走 <see cref="INotifyPropertyChanged"/>
/// 而不是 DispatcherTimer：面板存活期就是用户敲字的这几秒，值得为它跑一条定时器的只有"索引建好了要刷一次"，
/// 而那件事由服务在任务完成时直接调 <see cref="ShowIndexState"/>。
/// </para>
/// <para>
/// 【键位在面板内处理，不交给宿主】↑↓/Enter/Esc 都必须只在这些键真的属于"选应用"这件事时才生效，
/// 而输入框里的空格、退格、方向键（编辑光标移动）绝不能被当成列表操作 —— 面板是激活式窗口，
/// 键先到这里（PreviewKeyDown 隧道）比宿主更合适：<see cref="SearchBox"/> 拿到焦点后，
/// 未处理的键继续正常进输入框。
/// </para>
/// </summary>
public partial class LauncherOverlay : UserControl, IBackdropAware, IPopupShadow
{
    /// <summary>投影留白 = XAML 里 <c>Shell.Margin</c> 的唯一来源，宿主据此对齐<b>卡片</b>而非窗口外框。</summary>
    public PopupShadow ShadowInset => new(Shell.Margin.Left, Shell.Margin.Top, Shell.Margin.Right, Shell.Margin.Bottom);

    /// <summary>一次显示的结果条数上限（超过就靠用户继续敲字收窄，而不是让卡片长过屏幕）。</summary>
    public const int MaxResults = 8;

    private readonly LauncherService _service;
    private readonly System.Collections.ObjectModel.ObservableCollection<LauncherRow> _rows = new();

    /// <summary>承载本面板的弹层宿主（由 <see cref="LauncherService"/> 在显示前注入）。</summary>
    private FluentPopupHost? _host;

    public LauncherOverlay(LauncherService service)
    {
        InitializeComponent();
        _service = service;
        Results.ItemsSource = _rows;
    }

    /// <summary>注入宿主（打开方在 ShowAt 之前调用）。</summary>
    public void AttachHost(FluentPopupHost host) => _host = host;

    // ---------- 背板（与控制中心/托盘菜单同一套两层材质） ----------

    /// <summary>
    /// 拿到抓屏模糊 → 卡底铺该画刷并压 <c>CardFillBlur</c> 深色遮罩；拿不到 → 铺实心 <c>CardFillSolid</c>、
    /// 遮罩透明。两条分支都铺满整张卡（分层窗口上"没画到"就是看穿到桌面的洞）。
    /// </summary>
    public void OnBackdropReady(ScreenBackdrop? backdrop)
    {
        var blur = backdrop?.Brush;
        if (blur is not null)
        {
            Card.Background = blur;
            Scrim.Background = (Brush)Resources["CardFillBlur"];
            return;
        }
        Card.Background = (Brush)Resources["CardFillSolid"];
        Scrim.Background = Brushes.Transparent;
    }

    // ---------- 输入与结果 ----------

    /// <summary>把输入框焦点交给它：面板显示后用户直接开始敲字，不需要先点一下。</summary>
    public void FocusSearch() => Dispatcher.BeginInvoke(() =>
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();   // 二次打开时全选：直接敲就覆盖上一次查询，符合 Spotlight 手感
    }, System.Windows.Threading.DispatcherPriority.Input);

    /// <summary>当前输入文本（服务侧在索引迟到时要按它重跑一次检索）。</summary>
    public string Query => SearchBox.Text;

    /// <summary>打开时的首次填充：空查询也给内容（索引前若干条），面板绝不会是一片空白。</summary>
    public void RefreshInitial()
    {
        Rebuild();
        if (_service.IsIndexBuilding && _rows.Count == 0) StatusText.Text = "正在建立应用索引…";
    }

    /// <summary>索引在面板开着的时候才建好：按当前查询重跑一次，不留"永远在建立索引"的脸。</summary>
    public void RefreshAfterIndexReady() => Rebuild();

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Rebuild();

    /// <summary>
    /// 重算结果列表。检索本身在 Core（<see cref="LauncherIndex.Search"/>，纯函数 + 单测），
    /// 这里只把命中铺成行并把"第一行选中"这件事定下来——两个入口（打字、索引迟到）共用一条路径，
    /// 不会出现"打字时选首行、索引迟到后谁都不选"的分歧。
    /// </summary>
    private void Rebuild()
    {
        var hits = _service.Search(SearchBox.Text, MaxResults);
        _rows.Clear();
        foreach (var hit in hits) _rows.Add(new LauncherRow(hit.App, _service.IconOf(hit.App)));
        if (_rows.Count > 0) _rows[0].IsSelected = true;

        // 状态行只在"没有结果"或"索引还在建"时说话，其余时候留空：有结果时用户要看的是列表本身。
        if (hits.Count > 0)
            StatusText.Text = _service.IsIndexBuilding ? $"正在建立应用索引…（已命中 {_rows.Count} 条）" : string.Empty;
        else if (_service.IsIndexBuilding)
            StatusText.Text = "正在建立应用索引…";
        else
            StatusText.Text = SearchBox.Text.Length == 0 ? "索引为空" : "没有匹配的应用";
    }

    // ---------- 键盘 ----------

    /// <summary>
    /// 面板内的按键分派：↑↓ 移动选择、Enter 启动选中项、Esc 关闭；其余键原样交给输入框。
    /// <b>没有结果时 Enter/Esc 之外一律不拦</b>（避免"按了没反应"的第三种状态）。
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                _host?.Close();
                break;
            case Key.Enter:
                e.Handled = true;
                Commit();
                break;
            case Key.Up:
                e.Handled = Step(-1);
                break;
            case Key.Down:
                e.Handled = Step(1);
                break;
            default:
                base.OnPreviewKeyDown(e);
                break;
        }
    }

    private bool Step(int delta)
    {
        if (_rows.Count == 0) return false;
        var current = IndexOfSelected();
        // 回绕而不是撞墙：8 行结果里从第 1 行按 ↑ 到最后一行，与 Spotlight/启动菜单的观感一致
        var next = (current + delta + _rows.Count) % _rows.Count;
        if (next == current) return true;
        _rows[current].IsSelected = false;
        _rows[next].IsSelected = true;
        return true;
    }

    private int IndexOfSelected()
    {
        for (var i = 0; i < _rows.Count; i++) if (_rows[i].IsSelected) return i;
        return 0;
    }

    /// <summary>启动当前选中项（Enter 与鼠标点击同一条路径）。失败由服务把原因写进状态行。</summary>
    private void Commit()
    {
        if (_rows.Count == 0) return;
        var row = _rows[IndexOfSelected()];
        if (!_service.Launch(row.App, out var error))
        {
            // 启动失败**不收面板**：用户要能立刻换一条或改查询，而不是被弹回桌面再重开
            StatusText.Text = $"启动失败：{error}";
            return;
        }
        _host?.Close();
    }

    private void Row_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LauncherRow row }) return;
        for (var i = 0; i < _rows.Count; i++) _rows[i].IsSelected = ReferenceEquals(_rows[i], row);
        Commit();
        e.Handled = true;
    }

    // ---------- 释放 ----------

    /// <summary>
    /// 面板拆除（宿主 Closed 时由服务调用）。本类没有静态挂点需要解绑，这里只断掉对宿主的引用并清空行：
    /// 行里握着的是 <see cref="IconExtractor"/> 缓存中的冻结位图（进程级共享，不归本面板所有），
    /// 清空只是让面板对象图可以整体回收，不释放任何跨面板的资产。
    /// </summary>
    public void Shutdown()
    {
        _rows.Clear();
        _host = null;
    }
}

/// <summary>
/// 一行的视图模型。<see cref="IsSelected"/> 必须是可通知属性：选中行的高亮靠 DataTrigger 绑它，
/// 而不是在 code-behind 里逐个 Border 改颜色（后者会让"哪一行被选中"这件事有两个属主）。
/// </summary>
public sealed class LauncherRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public LauncherRow(LauncherApp app, ImageSource? icon)
    {
        App = app;
        Name = app.Name;
        LaunchPath = app.LaunchPath;
        Icon = icon;
        // 目标列显示"从哪儿启动"，取不到图标时也至少看得见名字与去处（不造假图标，见 XAML 里的空框注释）。
        // MSIX 条目的启动目标是 explorer.exe——那一行写 "explorer.exe" 是在说宿主而不是应用，
        // 改成 Core 给这条算出的检索键（记事本 → notepad），与用户敲进去、搜出来的那个词一致。
        Target = LauncherTarget.AppsFolderAumidOf(app.LaunchPath, app.Arguments) is not null
            ? LauncherTarget.SearchKeyOf(app.LaunchPath, app.Arguments)
            : System.IO.Path.GetFileName(app.LaunchPath);
    }

    public LauncherApp App { get; }
    public string Name { get; }
    public string LaunchPath { get; }
    public string Target { get; }
    public ImageSource? Icon { get; }

    /// <summary>取不到图标时显示空框占位（XAML 里绑这个可见性）。</summary>
    public Visibility IconMissing => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
