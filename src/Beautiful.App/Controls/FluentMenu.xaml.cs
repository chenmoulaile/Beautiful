// 自绘菜单，视觉对标 macOS；无第三方代码。
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
// UseWindowsForms 隐式全局 using 引入 WinForms 同名类型，此处定向消歧：
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Beautiful.App.Controls;

/// <summary>
/// 高级自绘菜单（渲染进 <see cref="FluentPopupHost"/> 弹层）：圆角毛玻璃卡、悬停高亮滑入、
/// 勾选列/图标列/▸ 子菜单箭头、分隔线；子菜单为第二个弹层，自父项右侧驻留 ~140ms 滑出，
/// 左侧越界自动翻转到父项左缘（macOS 行为）。整条父子弹层链共享"点击外部才关"存活判定
/// （根宿主看门狗遍历链矩形），叶子点击 → 先级联关链、再于 Input 优先级派发回调
/// （退出/开窗动作不与弹层拆除打架，也无焦点劫持）。
/// 全局同一时刻仅一个根菜单：新 Show 顶掉旧菜单（托盘连点安全）。
/// </summary>
public partial class FluentMenu : UserControl
{
    private const int SubmenuDwellMs = 140; // 悬停父项多久滑出子菜单
    private const int SubmenuDecayMs = 260; // 移开到普通项后子菜单延迟关闭（防横扫闪烁）

    private static FluentPopupHost? _openRootHost;

    private readonly Action<MenuItemView>? _onSelect;
    private FluentPopupHost? _host;          // 本页所在弹层
    private FluentMenu? _childMenu;          // 当前打开的子菜单（链式，最多一层活跃）
    private MenuItemView? _childFor;         // 子菜单对应的父项
    private Border? _dwellRow;               // 驻留计时中的行
    private MenuItemView? _dwellItem;

    private readonly DispatcherTimer _dwellTimer = new() { Interval = TimeSpan.FromMilliseconds(SubmenuDwellMs) };
    private readonly DispatcherTimer _decayTimer = new() { Interval = TimeSpan.FromMilliseconds(SubmenuDecayMs) };

    private FluentMenu(IEnumerable<MenuItemView> items, Action<MenuItemView>? onSelect)
    {
        InitializeComponent();
        _onSelect = onSelect;
        Rows.ItemsSource = items;
        _dwellTimer.Tick += OnDwellTick;
        _decayTimer.Tick += (_, _) => { _decayTimer.Stop(); CloseChild(); };
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
        owner.KeepAliveProbe = menu.ChainAlive; // 根宿主看门狗：整链任一矩形内存活
        owner.Closed += (_, _) => menu.CloseChild();
        owner.Closed += (_, _) => { if (ReferenceEquals(_openRootHost, owner)) _openRootHost = null; };
        _openRootHost = owner;
        owner.ShowAt(anchorPhysicalPx);
    }

    /// <summary>关闭当前显示的菜单链（应用退出/重开时调用，防弹层残留）。</summary>
    public static void CloseCurrent() => _openRootHost?.Close();

    // ---------- 存活判定（根宿主看门狗调用） ----------

    /// <summary>光标位于整条父子弹层链任一矩形（外扩桥接容差）内 → 保持打开。</summary>
    private bool ChainAlive()
    {
        for (var m = this; m is not null; m = m._childMenu)
            if (m._host is not null && m._host.CursorInInflated(FluentPopupHost.RectInflate))
                return true;
        return false;
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

    // ---------- 行交互 ----------

    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border row || row.DataContext is not MenuItemView item || item.IsSeparator) return;
        _decayTimer.Stop(); // 指针仍在菜单体内
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
            _decayTimer.Start(); // 扫过兄弟项：子菜单短暂延迟后收起
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
        if (sender is not Border row || row.DataContext is not MenuItemView item || item.IsSeparator) return;
        if (item.HasChildren)
        {
            _dwellTimer.Stop();
            OpenSubmenu(row, item); // 父项点击=展开/保持子菜单（不触发 onSelect）
            return;
        }
        Select(item);
    }

    // ---------- 子菜单弹层 ----------

    private void OpenSubmenu(Border row, MenuItemView item)
    {
        if (item.Children is not { Count: > 0 } children) return;
        if (ReferenceEquals(_childFor, item) && _childMenu is not null) return;
        CloseChild();
        _decayTimer.Stop();

        var childHost = new FluentPopupHost { ParentHost = _host };
        var childMenu = new FluentMenu(children, _onSelect) { _host = childHost }; // 回调沿链透传：叶子命中根 onSelect
        childHost.Content = childMenu;
        childHost.Closed += (_, _) => childMenu.CloseChild();
        _childMenu = childMenu;
        _childFor = item;

        // 行右上角/左下角物理像素（PointToScreen 返回设备像素，随窗口 DPI 精确）：
        // 首选右侧滑出；越界则以行左缘为翻转折线，子菜单右缘贴父项左缘（macOS 同规则）。
        var topRight = row.PointToScreen(new Point(row.ActualWidth - 6, -3));
        var bottomLeft = row.PointToScreen(new Point(2, row.ActualHeight + 3));
        childHost.ShowAtPhysical(topRight, bottomLeft);
    }

    private void CloseChild()
    {
        _dwellTimer.Stop();
        var child = _childMenu;
        if (child is null) return;
        _childMenu = null;
        _childFor = null;
        child.CloseChild();      // 先置空再递归（幂等，防 Closed 事件重入）
        child._host?.Close();
    }

    // ---------- 选择 ----------

    private void Select(MenuItemView item)
    {
        _dwellTimer.Stop();
        _decayTimer.Stop();
        RootHost?.Close(); // 级联关闭全链（根 Closed → 子 Closed → …）
        // 弹层拆除完成后再执行动作：onSelect 优先（Task 3 接线用），否则项自带 Invoke
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_onSelect is not null) _onSelect(item);
            else item.Invoke?.Invoke();
        }));
    }
}
