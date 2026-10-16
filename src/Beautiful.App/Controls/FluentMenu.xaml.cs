// 自绘菜单，视觉对标 macOS；无第三方代码。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Beautiful.App.Services;
using Beautiful.Core.Layout;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧：
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;          // 同上，System.Drawing.Brush
using Brushes = System.Windows.Media.Brushes;      // 同上，System.Drawing.Brushes

namespace Beautiful.App.Controls;

/// <summary>
/// 高级自绘菜单（渲染进 <see cref="FluentPopupHost"/> 弹层）：圆角毛玻璃卡、悬停高亮滑入、
/// 勾选列/图标列/▸ 子菜单箭头、分隔线；子菜单为第二个弹层，自父项右侧驻留 ~140ms 滑出，
/// 左侧越界自动翻转到父项左缘（macOS 行为）。
/// <para>
/// 【激活式键盘模型，评审 Important#3】根弹层显示即夺取前台激活与键盘焦点（ShowActivated/Focusable），
/// ↑/↓ 移动高亮、→ 展开并进子菜单、← 收子菜单回父、Enter/Space 命中当前项、Esc 关整链、Home/End 跳首/末
/// 可选行、字母键 type-ahead 跳转到首个同名前缀项。高亮单一来源＝容器 IsSelected（鼠标经 Row_MouseEnter
/// 同步选中项、键盘直接改 SelectedIndex，二者共用同一条 Enter/ExitActions，观感一致）。
/// 关闭＝Deactivated→关链为主：失活只在链内所有窗口都不再持有激活时才真正关（子弹层激活导致父层 Deactivated
/// 属链内存活，不误关）；光标探测看门狗（<see cref="ChainAlive"/> 注入根宿主）作兜底安全网，链持有激活时绝不关。
/// </para>
/// <para>
/// 全局同一时刻仅一个根菜单：新 Show 顶掉旧菜单（托盘连点安全）。叶子命中 → 先级联关链、再于 Input
/// 优先级派发回调（退出/开窗动作不与弹层拆除打架，也无焦点劫持）。
/// </para>
/// </summary>
public partial class FluentMenu : UserControl, IBackdropAware, IPopupShadow
{
    /// <summary>投影留白 = XAML 里 <c>Shell.Margin</c> 的唯一来源，宿主据此对齐<b>卡片</b>而非窗口外框。</summary>
    public PopupShadow ShadowInset => new(Shell.Margin.Left, Shell.Margin.Top, Shell.Margin.Right, Shell.Margin.Bottom);

    private const int SubmenuDwellMs = 140; // 悬停父项多久滑出子菜单
    private const int SubmenuDecayMs = 260; // 移开到普通项后子菜单延迟关闭（防横扫闪烁）

    private static FluentPopupHost? _openRootHost;

    private readonly Action<MenuItemView>? _onSelect;
    private FluentPopupHost? _host;          // 本页所在弹层
    private FluentMenu? _parentMenu;         // 父菜单（子菜单链向上回溯，评审 Important#2 停祖先 decay/dwell）
    private FluentMenu? _childMenu;          // 当前打开的子菜单（链式，最多一层活跃）
    private MenuItemView? _childFor;         // 子菜单对应的父项
    private Border? _dwellRow;               // 驻留计时中的行
    private MenuItemView? _dwellItem;
    private bool _closingChain;              // 根链正在拆除：抑制 Deactivated→Close 级联重入

    private readonly DispatcherTimer _dwellTimer = new() { Interval = TimeSpan.FromMilliseconds(SubmenuDwellMs) };
    private readonly DispatcherTimer _decayTimer = new() { Interval = TimeSpan.FromMilliseconds(SubmenuDecayMs) };

    private FluentMenu(IEnumerable<MenuItemView> items, Action<MenuItemView>? onSelect)
    {
        InitializeComponent();
        _onSelect = onSelect;
        Rows.ItemsSource = items;
        _dwellTimer.Tick += OnDwellTick;
        // 评审 Important#2：子菜单 decay 触发前先判"光标是否仍在子（含更深）弹层矩形内"——
        // 若是则指针已扫进子菜单，取消收起（父 decay 自兜底，不误关指针所在的子层）。
        _decayTimer.Tick += (_, _) =>
        {
            _decayTimer.Stop();
            if (DescendantAlive()) return; // 指针在子孙弹层矩形内 → 保活
            CloseChild();
        };
    }

    /// <summary>
    /// 在物理像素锚点（如 <c>System.Windows.Forms.Cursor.Position</c>）显示菜单。
    /// <paramref name="onSelect"/> 为 null 时点击执行 <see cref="MenuItemView.Invoke"/>。
    /// </summary>
    public static void Show(FluentPopupHost owner, Point anchorPhysicalPx,
                            IEnumerable<MenuItemView> items, Action<MenuItemView>? onSelect = null)
    {
        CloseCurrent(); // 全局单菜单：旧链（含子级联）先关
        var menu = new FluentMenu(items, onSelect) { _host = owner };
        owner.Content = menu;
        owner.KeepAliveProbe = menu.ChainAlive; // 根宿主看门狗：链内任一窗口激活或光标在任一矩形内即存活
        owner.Closed += (_, _) => menu.CloseChild();
        owner.Closed += (_, _) => { if (ReferenceEquals(_openRootHost, owner)) _openRootHost = null; };
        menu.AttachInput(owner);
        _openRootHost = owner;
        owner.ShowAt(anchorPhysicalPx);
    }

    /// <summary>关闭当前显示的菜单链（应用退出/重开时调用，防弹层残留）。</summary>
    public static void CloseCurrent() => _openRootHost?.Close();

    // ---------- 背板（v3 T2/T3：宿主显示前抓屏自绘模糊，交回画刷或 null） ----------

    /// <summary>
    /// 拿到抓屏模糊 → 卡底铺该画刷，并在其上压一层 <c>CardFillBlur</c> 半透明深色遮罩（文字对比度靠它，
    /// 不是靠"透出系统模糊"）；拿不到（抓屏失败 / <c>BEAUTIFUL_NOBLUR=1</c> / 瞄准越界）→ 卡底铺实心渐变
    /// <c>CardFillSolid</c>、遮罩层透明。两条分支都把整张卡铺满：宿主是分层窗口，"没画到"= 看穿到桌面，
    /// 那正是 v2 顶栏与弹层的故障形态，绝不能在任何降级里重演。
    /// <para>
    /// 画刷由宿主瞄准到**窗口矩形**后交回（见 <see cref="IBackdropAware.OnBackdropReady"/> 的取舍说明）：
    /// 卡片四周的投影留白让它相对卡片平移了不到一个像素格，在半径 32 的模糊下不可分辨。
    /// </para>
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

    // ---------- 输入接线（激活式弹层：Window 级 PreviewKeyDown 隧道取键，Deactivated 链感知关闭） ----------

    private void AttachInput(FluentPopupHost host)
    {
        host.PreviewKeyDown += OnPreviewKeyDown; // 焦点在任何内部元素都能先于控件拿到按键
        host.Deactivated += OnHostDeactivated;   // 失活 → 延后判定链是否整体让位给链外窗口
    }

    // ---------- 存活判定（根宿主看门狗 + 键盘/失活关闭共用） ----------

    /// <summary>
    /// 链存活＝整条父子弹层链任一窗口仍持有前台激活（键盘导航中，评审 Important#3）
    /// 或光标位于任一矩形（外扩桥接容差）内 → 保持打开。看门狗据此判定，且链激活时绝不因光标离开而关。
    /// </summary>
    private bool ChainAlive()
    {
        for (var m = RootMenu; m is not null; m = m._childMenu)
        {
            var h = m._host;
            if (h is null) continue;
            if (h.IsActive) return true;
            if (h.CursorInInflated(FluentPopupHost.RectInflate)) return true;
        }
        return false;
    }

    /// <summary>
    /// 光标是否落在本菜单的子孙（不含自身）弹层矩形内——父 decay 的防御性自兜底：指针已扫进子层
    /// （含子层卡片留白，不止行）即取消收起。刻意不看 IsActive：子菜单 hover 打开后自身保持激活，
    /// 若把激活也算"存活"则横扫父层兄弟项永远收不起子菜单（macOS 语义：移到兄弟项应收起旧子菜单）。
    /// </summary>
    private bool DescendantAlive()
    {
        for (var m = _childMenu; m is not null; m = m._childMenu)
        {
            var h = m._host;
            if (h is not null && h.CursorInInflated(FluentPopupHost.RectInflate)) return true;
        }
        return false;
    }

    private FluentMenu RootMenu
    {
        get
        {
            var m = this;
            while (m._parentMenu is not null) m = m._parentMenu;
            return m;
        }
    }

    private FluentPopupHost? RootHost
    {
        get
        {
            var h = _host;
            while (h?.ParentHost is not null) h = h.ParentHost;
            return h;
        }
    }

    // ---------- 失活关闭（评审 Important#3：激活式模型的主关闭路径） ----------

    private void OnHostDeactivated(object? sender, EventArgs e)
    {
        var root = RootMenu;
        if (root._closingChain) return; // 已在拆除链：忽略级联失活
        // 延后到激活态落定再判定：子弹层激活会先使父层 Deactivated，此时链内仍有活动窗口 → 不关。
        root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!root._closingChain && !root.ChainActiveAnywhere()) root.CloseChain();
        }));
    }

    private bool ChainActiveAnywhere()
    {
        for (var m = RootMenu; m is not null; m = m._childMenu)
            if (m._host?.IsActive == true) return true;
        return false;
    }

    // ---------- 键盘导航 ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up: MoveSelection(-1); e.Handled = true; return;
            case Key.Down: MoveSelection(1); e.Handled = true; return;
            case Key.Right: OpenChildFromCurrent(); e.Handled = true; return;
            case Key.Left: ReturnToParentOrSelf(); e.Handled = true; return;
            case Key.Enter:
            case Key.Space:
                ActivateCurrent(); e.Handled = true; return;
            case Key.Escape:
                RootMenu.CloseChain(); e.Handled = true; return;
            case Key.Home: SelectByIndex(FirstSelectableIndex()); e.Handled = true; return;
            case Key.End: SelectByIndex(LastSelectableIndex()); e.Handled = true; return;
        }

        // type-ahead：跳到当前项之后首个以该字符开头的可选项（macOS 式，循环回绕）。
        if (TryTypeAhead(e.Key)) e.Handled = true;
    }

    private void MoveSelection(int delta)
    {
        int count = Rows.Items.Count;
        if (count == 0) return;
        int i = Rows.SelectedIndex;
        // 无当前项时：向下从首行、向上从末行起步。
        int j = i < 0 ? (delta > 0 ? -1 : count) : i;
        for (int guard = 0; guard < count; guard++)
        {
            j += delta;
            if (j < 0 || j >= count) return; // 到端点不循环（macOS 菜单同规则）
            if (IsSelectableAt(j)) { SelectByIndex(j); return; }
        }
    }

    private int FirstSelectableIndex()
    {
        for (int i = 0; i < Rows.Items.Count; i++) if (IsSelectableAt(i)) return i;
        return -1;
    }

    private int LastSelectableIndex()
    {
        for (int i = Rows.Items.Count - 1; i >= 0; i--) if (IsSelectableAt(i)) return i;
        return -1;
    }

    private bool IsSelectableAt(int i) =>
        i >= 0 && i < Rows.Items.Count && Rows.Items[i] is MenuItemView it && !it.IsSeparator && it.IsEnabled;

    private void SelectByIndex(int i)
    {
        if (i < 0 || i >= Rows.Items.Count) return;
        Rows.SelectedIndex = i; // 容器 IsSelected 触发高亮 Enter/ExitActions（与悬停同源）
    }

    private MenuItemView? CurrentItem() =>
        Rows.SelectedIndex >= 0 && Rows.SelectedIndex < Rows.Items.Count
            ? Rows.Items[Rows.SelectedIndex] as MenuItemView
            : null;

    private bool TryTypeAhead(Key key)
    {
        char c = key switch
        {
            >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
            _ => '\0',
        };
        if (c == '\0') return false;
        int count = Rows.Items.Count;
        int start = Rows.SelectedIndex < 0 ? -1 : Rows.SelectedIndex;
        for (int step = 1; step <= count; step++)
        {
            int idx = (start + step + count) % count; // 从当前项之后循环一圈
            if (!IsSelectableAt(idx)) continue;
            if (Rows.Items[idx] is MenuItemView it &&
                it.Label.Length > 0 && char.ToLowerInvariant(it.Label[0]) == c)
            {
                SelectByIndex(idx);
                return true;
            }
        }
        return false;
    }

    private void OpenChildFromCurrent()
    {
        var item = CurrentItem();
        if (item is { HasChildren: true })
        {
            if (ReferenceEquals(_childFor, item) && _childMenu is not null) { _childMenu._host?.Activate(); return; }
            UIElement anchor = (UIElement?)CurrentRowElement() ?? this;
            OpenSubmenu((FrameworkElement)anchor, item);
        }
        // 叶子项按 → 无操作（macOS 同规则）。
    }

    private void ReturnToParentOrSelf()
    {
        var parent = _parentMenu;
        if (parent is null) return; // 根层无子可收：← 不关整链（Esc 才关）
        parent.CloseChild();        // 关闭本子菜单（含更深级联）
        parent._host?.Activate();   // 焦点/激活回到父层，链保持存活
    }

    private void ActivateCurrent()
    {
        var item = CurrentItem();
        if (item is null || item.IsSeparator || !item.IsEnabled) return;
        if (item.HasChildren) { OpenChildFromCurrent(); return; } // 父项 Enter=展开并进子菜单
        Select(item);
    }

    /// <summary>当前选中行的可视 Border（子菜单定位锚点，与悬停路径同源）；未就绪回退 null。</summary>
    private Border? CurrentRowElement()
    {
        if (Rows.SelectedIndex < 0) return null;
        if (Rows.ItemContainerGenerator.ContainerFromIndex(Rows.SelectedIndex) is not DependencyObject container)
            return null;
        return FindRowBorder(container);
    }

    private static Border? FindRowBorder(DependencyObject root)
    {
        if (root is Border b && b.Name == "Row") return b;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindRowBorder(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    // ---------- 行交互（鼠标） ----------

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border row || row.DataContext is not MenuItemView item
            || item.IsSeparator || !item.IsEnabled) return;
        Rows.SelectedItem = item; // 悬停与键盘高亮同源（IsSelected 驱动）
        _decayTimer.Stop();       // 指针仍在本菜单体内
        StopAncestorTimers();     // 评审 Important#2：指针进入本（子）层 → 逐层停祖先 decay/dwell，防父把本层关掉
        if (item.HasChildren)
        {
            if (ReferenceEquals(_childFor, item) && _childMenu is not null) return; // 已开
            _dwellRow = row;
            _dwellItem = item;
            _dwellTimer.Stop();
            _dwellTimer.Start();
        }
        else if (_childMenu is not null)
        {
            _decayTimer.Start(); // 扫过兄弟项：子菜单短暂延迟后收起（指针若已进子层，tick 时 DescendantAlive 自兜底取消）
        }
    }

    /// <summary>沿父链向上停止每一层的 decay/dwell 计时（评审 Important#2 核心修复）。</summary>
    private void StopAncestorTimers()
    {
        for (var m = _parentMenu; m is not null; m = m._parentMenu)
        {
            m._decayTimer.Stop();
            m._dwellTimer.Stop();
        }
    }

    private void Row_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(_dwellRow, sender)) _dwellTimer.Stop();
    }

    private void OnDwellTick(object? sender, EventArgs e)
    {
        _dwellTimer.Stop();
        if (_dwellRow is not null && _dwellItem is { HasChildren: true })
            OpenSubmenu(_dwellRow, _dwellItem);
    }

    private void Row_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border row || row.DataContext is not MenuItemView item
            || item.IsSeparator || !item.IsEnabled) return;
        if (item.HasChildren)
        {
            _dwellTimer.Stop();
            OpenSubmenu(row, item); // 父项点击=展开/保持子菜单（不触发 onSelect）
            return;
        }
        Select(item);
    }

    // ---------- 子菜单弹层 ----------

    private void OpenSubmenu(FrameworkElement anchorElement, MenuItemView item)
    {
        if (item.Children is not { Count: > 0 } children) return;
        if (ReferenceEquals(_childFor, item) && _childMenu is not null) { _childMenu._host?.Activate(); return; }
        CloseChild();
        _decayTimer.Stop();
        StopAncestorTimers();

        var childHost = new FluentPopupHost { ParentHost = _host };
        var childMenu = new FluentMenu(children, _onSelect) { _host = childHost, _parentMenu = this }; // 回调沿链透传
        childHost.Content = childMenu;
        childHost.Closed += (_, _) => childMenu.CloseChild();
        childMenu.AttachInput(childHost);
        _childMenu = childMenu;
        _childFor = item;

        // 行右上角/左下角物理像素（PointToScreen 返回设备像素，随窗口 DPI 精确）：
        // 首选右侧滑出；越界则以行左缘为翻转折线，子菜单右缘贴父项左缘（macOS 同规则）。
        var topRight = anchorElement.PointToScreen(new Point(anchorElement.ActualWidth - 6, -3));
        var bottomLeft = anchorElement.PointToScreen(new Point(2, anchorElement.ActualHeight + 3));
        childHost.ShowAtPhysical(topRight, bottomLeft); // ShowAtPhysical 内 ForceForeground → 键盘焦点进入子菜单
    }

    private void CloseChild()
    {
        _dwellTimer.Stop();
        var child = _childMenu;
        if (child is null) return;
        _childMenu = null;
        _childFor = null;
        _dwellRow = null;
        _dwellItem = null;
        child.CloseChild();      // 先置空再递归（幂等，防 Closed 事件重入）
        child._host?.Close();
        // 子层（可能正持激活）被关闭后，父层须重新夺回激活，否则链整体失活 → 误判关闭（评审 Important#3）。
        if (!RootMenu._closingChain) _host?.Activate();
    }

    // ---------- 选择 ----------

    private void Select(MenuItemView item)
    {
        var root = RootMenu;
        root._closingChain = true;
        _dwellTimer.Stop();
        _decayTimer.Stop();
        root.RootHost?.Close(); // 级联关闭全链（根 Closed → 子 Closed → …）
        // 弹层拆除完成后再执行动作：onSelect 优先（Task 3 接线用），否则项自带 Invoke
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_onSelect is not null) _onSelect(item);
            else item.Invoke?.Invoke();
        }));
    }

    /// <summary>关闭整条链（Esc / 失活让位链外窗口）：置拆除标志抑制级联 Deactivated 重入。</summary>
    private void CloseChain()
    {
        _closingChain = true;
        _host?.Close();
    }
}
