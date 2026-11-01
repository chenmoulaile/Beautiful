using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Services;
using Beautiful.Core.Launcher;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace Beautiful.App.Launcher;

/// <summary>
/// 启动台 · 文件夹（Phase 5 收尾）：把 Core 的 <see cref="Beautiful.Core.Launchpad.LaunchpadFolders"/>
/// 接到窗口上——拖 A 到 B 上建文件夹、拖到文件夹上加入、右键成员移出、点开覆盖层。
/// <para>
/// 【为什么合成图标而不改模板】文件夹图标是"2×2 的成员缩略图拼图"。如果为它改 XAML 模板，
/// 每个格子都要多一套分支；这里直接把它渲染成一张 76×76 的位图，复用现有模板——视觉一致、
/// 代码只有一处 <see cref="FolderIcon"/>。
/// </para>
/// <para>
/// 【拖放用系统 DragDrop】不用自己追踪鼠标：DoDragDrop 期间由 OLE 接管，跨格子/跨页都稳，
/// 且和"拖到桌面/资源管理器"这类系统交互不冲突（我们只处理落在自己的格子上）。
/// </para>
/// </summary>
public partial class LaunchpadWindow
{
    private LaunchpadLayoutStore? _layout;
    private Services.LaunchpadFolderRecord? _openFolder;
    private Point _dragOrigin;
    private LaunchpadTile? _dragTile;

    /// <summary>拖拽启动阈值（DIP）：小于它视作点击，避免"点一下就建了文件夹"。</summary>
    private const double DragThresholdDip = 6;

    private void InitLaunchpadFolders()
    {
        _layout = new LaunchpadLayoutStore();

        Tiles.AllowDrop = true;
        Tiles.PreviewMouseLeftButtonDown += Tiles_DragStart;
        Tiles.PreviewMouseMove += Tiles_DragMove;
        Tiles.DragOver += Tiles_DragOver;
        Tiles.Drop += Tiles_Drop;
        FolderOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 生成一屏条目：文件夹在前（macOS 行为），其余应用按原顺序；已进文件夹的应用不再单列。
    /// <paramref name="includeFolders"/> 为 false 时（搜索中）返回纯应用列表。
    /// </summary>
    private List<LaunchpadTile> BuildTiles(bool includeFolders)
    {
        var tiles = new List<LaunchpadTile>();
        if (_layout is null)
        {
            // alpha.22 OOM 整改：不再在此同步提图标（UI 线程 × 全量应用 × 256px 是内存风暴根因）。
            // tile 先建（Icon=null），当前页图标由 PumpPageIcons 分帧补；文件夹 tile 仍同步拼贴（数量少）。
            return _apps.Select(app => new LaunchpadTile(app, app.Name, null)).ToList();
        }

        var inFolder = new HashSet<string>(StringComparer.Ordinal);
        if (includeFolders)
        {
            foreach (var folder in _layout.Folders)
            {
                var members = _layout.MembersOf(folder, _service.AllApps);

                // 成员一个都解析不出来（应用已卸载、或键口径变化）时不画这个文件夹：
                // 画一个空白圆角块比"文件夹消失"更让人困惑，也会让 chip 条里留一个点不开的项。
                if (members.Count == 0)
                {
                    continue;
                }

                foreach (var member in members)
                {
                    inFolder.Add(LaunchpadLayoutStore.KeyOf(member));
                }

                tiles.Add(new LaunchpadTile(null, folder.Name, FolderIcon(members), folder));
            }
        }

        foreach (var app in _apps)
        {
            if (includeFolders && inFolder.Contains(LaunchpadLayoutStore.KeyOf(app)))
            {
                continue;
            }

            tiles.Add(new LaunchpadTile(app, app.Name, null));
        }

        return tiles;
    }

    /// <summary>
    /// 文件夹图标：把成员的图标按 2×2 拼成一张 76×76 位图（1 个成员时就铺满，3 个时留一角）。
    /// 取不到图标的成员跳过；全空时画一个圆角灰块，保证文件夹永远有个可见的实体。
    /// </summary>
    private BitmapSource FolderIcon(IReadOnlyList<LauncherApp> members)
    {
        const int size = 76;
        const int cell = 34;
        const int gap = 2;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            dc.DrawRoundedRectangle(background, null, new Rect(0, 0, size, size), 16, 16);

            var icons = members.Select(m => _service.IconOf(m, _iconPx)).Where(i => i is not null).Take(4).ToList();
            if (icons.Count == 0)
            {
                return Render(visual, size);
            }

            var slots = icons.Count switch
            {
                1 => new[] { new Point((size - cell) / 2.0, (size - cell) / 2.0) },
                2 => new[]
                {
                    new Point((size - (cell * 2) - gap) / 2.0, (size - cell) / 2.0),
                    new Point((size - (cell * 2) - gap) / 2.0 + cell + gap, (size - cell) / 2.0)
                },
                3 => new[]
                {
                    new Point((size - (cell * 2) - gap) / 2.0, (size - (cell * 2) - gap) / 2.0),
                    new Point((size - (cell * 2) - gap) / 2.0 + cell + gap, (size - (cell * 2) - gap) / 2.0),
                    new Point((size - cell) / 2.0, (size - (cell * 2) - gap) / 2.0 + cell + gap)
                },
                _ => new[]
                {
                    new Point((size - (cell * 2) - gap) / 2.0, (size - (cell * 2) - gap) / 2.0),
                    new Point((size - (cell * 2) - gap) / 2.0 + cell + gap, (size - (cell * 2) - gap) / 2.0),
                    new Point((size - (cell * 2) - gap) / 2.0, (size - (cell * 2) - gap) / 2.0 + cell + gap),
                    new Point((size - (cell * 2) - gap) / 2.0 + cell + gap,
                              (size - (cell * 2) - gap) / 2.0 + cell + gap)
                }
            };

            for (var i = 0; i < icons.Count; i++)
            {
                var rect = new Rect(slots[i].X, slots[i].Y, cell, cell);
                dc.DrawImage(icons[i]!, rect);
            }
        }

        return Render(visual, size);
    }

    private BitmapSource Render(DrawingVisual visual, int size)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------- 拖放 ----------

    private void Tiles_DragStart(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(Tiles);
        _dragTile = TileAt(e.OriginalSource as DependencyObject);
    }

    private void Tiles_DragMove(object sender, MouseEventArgs e)
    {
        if (_dragTile?.App is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(Tiles) - _dragOrigin;
        if (Math.Abs(delta.X) < DragThresholdDip && Math.Abs(delta.Y) < DragThresholdDip)
        {
            return;
        }

        var app = _dragTile.App;
        _dragTile = null;
        DragDrop.DoDragDrop(Tiles, app, DragDropEffects.Move);
    }

    private void Tiles_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(LauncherApp)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Tiles_Drop(object sender, DragEventArgs e)
    {
        if (_layout is null || e.Data.GetData(typeof(LauncherApp)) is not LauncherApp source)
        {
            return;
        }

        var target = TileAt(e.OriginalSource as DependencyObject);
        if (target is null)
        {
            return;
        }

        var sourceKey = LaunchpadLayoutStore.KeyOf(source);
        string message;
        if (target.Folder is not null)
        {
            message = _layout.AddToFolder(target.Folder.Id, sourceKey);
        }
        else if (target.App is not null)
        {
            message = _layout.DropOnApp(LaunchpadLayoutStore.KeyOf(target.App), sourceKey, target.Name);
        }
        else
        {
            return;
        }

        if (!string.IsNullOrEmpty(message))
        {
            Status.Text = message;
        }

        Refresh();
        e.Handled = true;
    }

    /// <summary>从可视树往上找最近的、Tag 是条目的按钮。</summary>
    private static LaunchpadTile? TileAt(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is Button { Tag: LaunchpadTile tile })
            {
                return tile;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    // ---------- 文件夹覆盖层 ----------

    /// <summary>诊断入口：按名字打开文件夹覆盖层（截图验收用，不进产品逻辑）。</summary>
    public static void OpenFolderForDiagnostics(string name)
    {
        if (_current is not { IsLoaded: true } window || window._layout is null)
        {
            return;
        }

        var folder = window._layout.Folders.FirstOrDefault(f => f.Name == name);
        if (folder is not null)
        {
            window.OpenFolder(folder);
        }
    }

    /// <summary>诊断入口：用索引里的前两个应用临时拼一个文件夹并打开（不落盘，只用于截图验收）。</summary>
    public static bool ShowDemoFolderForDiagnostics()
    {
        if (_current is not { IsLoaded: true } window)
        {
            return false;
        }

        var apps = window._service.AllApps.Take(2).ToList();
        if (apps.Count < 2)
        {
            return false;   // 索引还没就绪：调用方稍后重试
        }

        var demo = new Services.LaunchpadFolderRecord("demo", "示例文件夹",
            apps.Select(LaunchpadLayoutStore.KeyOf).ToList());
        window.FolderTitle.Text = demo.Name;
        window.FolderItems.ItemsSource = apps
            .Select(app => new LaunchpadTile(app, app.Name, window._service.IconOf(app, window._iconPx)))
            .ToList();
        window.FolderOverlay.Visibility = Visibility.Visible;
        return true;
    }

    /// <summary>
    /// 文件夹 chip 条（MacDock 的 folder strip）：搜索条下方一排等宽胶囊，点开覆盖层。
    /// <para>
    /// 只在"没有搜索且有文件夹"时占位——空功能不该推开网格；搜索时也不显示，
    /// 因为搜索结果本身就是一份扁平列表，混进文件夹会让"找应用"变成"先点两下"。
    /// </para>
    /// </summary>
    private void BuildFolderStrip(bool includeFolders)
    {
        if (FolderStrip is null || Tiles is null || _layout is null)
        {
            return;
        }

        FolderStrip.Children.Clear();
        if (!includeFolders || _layout.Folders.Count == 0)
        {
            FolderStrip.Visibility = Visibility.Collapsed;
            Tiles.Margin = new Thickness(0);
            return;
        }

        FolderStrip.Visibility = Visibility.Visible;
        Tiles.Margin = new Thickness(0, 44, 0, 0);   // 给 chip 条让出一行高度

        foreach (var folder in _layout.Folders)
        {
            // 与网格同一条判据：成员全解析不到就不给 chip（点开只会是一个空面板）
            if (_layout.MembersOf(folder, _service.AllApps).Count == 0)
            {
                continue;
            }

            var button = new Button
            {
                Content = folder.Name,
                Height = 32,
                MinWidth = 120,
                MaxWidth = 200,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(5, 0, 5, 0),
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0),
                ToolTip = $"{folder.MemberIds.Count} 个应用",
                Template = ChipTemplate()
            };

            var captured = folder;
            button.Click += (_, _) => OpenFolder(captured);
            FolderStrip.Children.Add(button);
        }
    }

    /// <summary>胶囊按钮模板：圆角 16、内容居中、悬停加深（默认 Button 模板会画方角灰底）。</summary>
    private static ControlTemplate ChipTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty,
            new SolidColorBrush(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF))));
        template.Triggers.Add(hover);
        return template;
    }

    private void OpenFolder(Services.LaunchpadFolderRecord folder)
    {
        if (_layout is null)
        {
            return;
        }

        _openFolder = folder;
        FolderTitle.Text = folder.Name;
        // 文件夹成员通常 ≤20：同步提取可接受，但尺寸跟随页图标（缓存命中同尺寸，不额外翻倍内存）
        FolderItems.ItemsSource = _layout.MembersOf(folder, _service.AllApps)
            .Select(app => new LaunchpadTile(app, app.Name, _service.IconOf(app, _iconPx)))
            .ToList();
        FolderOverlay.Visibility = Visibility.Visible;
    }

    private void CloseFolder()
    {
        _openFolder = null;
        FolderOverlay.Visibility = Visibility.Collapsed;
    }

    private void FolderOverlay_Click(object sender, MouseButtonEventArgs e)
    {
        // 只有点在遮罩本身（不是卡片里）才关，避免点成员时误关
        if (ReferenceEquals(e.OriginalSource, FolderOverlay))
        {
            CloseFolder();
            e.Handled = true;
        }
    }

    private void FolderClose_Click(object sender, RoutedEventArgs e) => CloseFolder();

    private void FolderMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LaunchpadTile { App: { } app } } && _service.Launch(app, out var error))
        {
            Close();
            return;
        }
        else if (sender is Button { Tag: LaunchpadTile { App: null } })
        {
            return;
        }
    }

    private void FolderMember_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (_layout is null || _openFolder is null
            || sender is not Button { Tag: LaunchpadTile { App: { } app } })
        {
            return;
        }

        var message = _layout.RemoveFromFolder(_openFolder.Id, LaunchpadLayoutStore.KeyOf(app));

        // 文件夹被解散（只剩一个成员）时覆盖层没有意义了：直接关掉
        var stillThere = _layout.Folders.FirstOrDefault(f => f.Id == _openFolder.Id);
        Status.Text = message;
        if (stillThere is null)
        {
            CloseFolder();
        }
        else
        {
            OpenFolder(stillThere);
        }

        Refresh();
        e.Handled = true;
    }
}
