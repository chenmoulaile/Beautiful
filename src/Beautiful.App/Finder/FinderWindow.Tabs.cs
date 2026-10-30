using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Beautiful.Core.Finder;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口 · 标签页（Phase 6-c）。
/// <para>
/// 标签状态由 Core 的 <see cref="FinderTabSet"/> 持有（每个标签一条独立历史栈），
/// 本文件只做两件事：把状态渲染成 Big Sur 样式的标签条、把点击/快捷键翻译成状态变更。
/// 标签条只在 <c>Count &gt; 1</c> 时出现——单标签时它纯属噪音，macOS 也是这么做的。
/// </para>
/// <para>
/// 切换标签 = 换一整套"当前目录 + 历史 + 视图选项"：视图选项仍按文件夹记忆，
/// 所以从标签 A（分栏）切到标签 B（列表）时，两边各显示自己记住的视图，互不污染。
/// </para>
/// </summary>
public partial class FinderWindow
{
    private FinderTabSet _tabs = null!;

    /// <summary>当前标签的导航历史（窗口所有后退/前进都走这里）。</summary>
    private NavigationHistory _history => _tabs.ActiveHistory;

    /// <summary>
    /// 诊断入口（<c>--tabs a,b,c</c>）：把给定目录各开一个标签并激活最后一个。
    /// 截图/验收用，不影响正常打开的窗口（没有窗口时静默忽略）。
    /// </summary>
    public static void OpenTabs(IReadOnlyList<string> paths)
    {
        if (_current is not { IsLoaded: true } window || paths.Count == 0)
        {
            return;
        }

        var added = 0;
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && window._tabs.Add(path) >= 0)
            {
                added++;
            }
        }

        if (added == 0)
        {
            return;
        }

        window.RefreshTabBar();
        window.ActivateTab(window._tabs.Count - 1);
    }

    // ---------- 标签条渲染 ----------

    /// <summary>重建标签条（数量为 1 时整条隐藏）。</summary>
    private void RefreshTabBar()
    {
        if (TabBar is null || TabStack is null)
        {
            return;
        }

        TabStack.Children.Clear();
        TabBar.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_tabs.Count <= 1)
        {
            return;
        }

        for (var i = 0; i < _tabs.Count; i++)
        {
            TabStack.Children.Add(BuildTabChip(i));
        }
    }

    /// <summary>单个标签：标题 + 关闭叉；激活态用浅色底与强调色文字。</summary>
    private FrameworkElement BuildTabChip(int index)
    {
        var active = index == _tabs.ActiveIndex;
        var chip = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 3, 4, 3),
            Margin = new Thickness(0, 0, 4, 0),
            MaxWidth = 190,
            Background = active
                ? new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80))
                : Brushes.Transparent,
            Cursor = Cursors.Hand
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = _tabs.TitleAt(index),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource(active ? "MacTextPrimary" : "MacTextSecondary"),
            ToolTip = _tabs.Paths[index]
        });

        var close = new Button
        {
            Content = "×",
            Width = 16,
            Height = 16,
            Margin = new Thickness(6, 0, 0, 0),
            FontSize = 12,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("MacTextTertiary"),
            Focusable = false,
            ToolTip = "关闭标签 (Ctrl+W)"
        };
        close.Click += (_, e) =>
        {
            e.Handled = true;
            CloseTab(index);
        };
        content.Children.Add(close);

        chip.Child = content;
        chip.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            ActivateTab(index);
        };

        return chip;
    }

    // ---------- 标签操作 ----------

    /// <summary>⌘T：在当前标签右侧新建一个同目录标签（macOS 的行为）。</summary>
    private void NewTab()
    {
        var index = _tabs.Add(_currentPath);
        if (index < 0)
        {
            ShowStatusHint($"最多 {FinderTabSet.MaxTabs} 个标签");
            return;
        }

        RefreshTabBar();
        UpdateNavButtons();
        ShowStatusHint("已新建标签");
    }

    /// <summary>⌘W：先关标签，只剩一个时关窗口（由调用方决定）。</summary>
    /// <returns>是否关掉了标签（false 表示没有标签可关，应关闭窗口）。</returns>
    private bool CloseActiveTab()
    {
        if (!_tabs.CanClose)
        {
            return false;
        }

        CloseTab(_tabs.ActiveIndex);
        return true;
    }

    private void CloseTab(int index)
    {
        if (!_tabs.Close(index))
        {
            return;
        }

        RefreshTabBar();
        SwitchToActiveTab();
    }

    private void ActivateTab(int index)
    {
        if (!_tabs.Activate(index))
        {
            return;
        }

        RefreshTabBar();
        SwitchToActiveTab();
    }

    /// <summary>把窗口切到当前激活标签的路径（不写历史：切换标签不是一次导航）。</summary>
    private void SwitchToActiveTab()
    {
        var path = _tabs.ActivePath;
        _currentPath = path;
        ApplyPath(path);
        SyncSidebarSelection();
        UpdateNavButtons();
        ApplyViewOptionsFor(path);
        SyncViewChrome();
        StartLoad(path);
        ResetSearch();
    }
}
