using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Launcher;
using Beautiful.Core.Layout;
using Beautiful.Core.Launchpad;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Image = System.Windows.Controls.Image;

namespace Beautiful.App.Launcher;

/// <summary>
/// Launchpad（v4 Phase 5-T1 起步）：全屏、壁纸底、7×5 网格、搜索、翻页、点击/Enter 启动。
/// 复用 LauncherService 的索引与图标管线；文件夹、拖拽、自定义排序、Dock 图标入口仍待后续。
/// </summary>
public partial class LaunchpadWindow : Window, System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>当前页列数（由 <see cref="LaunchpadGridRules.Fit"/> 算出；XAML 绑定它）。</summary>
    public int TileColumns { get; private set; } = 7;

    /// <summary>当前页行数。</summary>
    public int TileRows { get; private set; } = 5;

    /// <summary>当前图标边长（DIP）；窄屏时会小于 76。</summary>
    public double TileIconSize { get; private set; } = 76;

    /// <summary>每页容量 = 列 × 行；分页与翻页都读它，不再有写死的 35。</summary>
    private int PageSize => Math.Max(1, TileColumns * TileRows);

    /// <summary>关闭动画进行中（防止 Closing 递归拦截）。</summary>
    private bool _closing;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void RaiseTileGeometryChanged()
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TileColumns)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TileRows)));
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(TileIconSize)));
    }

    private static LaunchpadWindow? _current;

    private readonly LauncherService _service;
    private IReadOnlyList<LauncherApp> _apps = Array.Empty<LauncherApp>();
    private ObservableCollection<LaunchpadTile> _pageTiles = new();
    private int _page;

    /// <summary>当前页图标分帧泵的取消源：翻页/搜索/关闭时取消，防止旧页继续提取。</summary>
    private CancellationTokenSource? _iconPump;

    /// <summary>图标提取请求尺寸（物理 px）：TileIconSize × DPI × 悬停 1.10，取整到 16 的倍数。</summary>
    private int _iconPx = 176;

    /// <summary>搜索输入防抖：每敲一个字就全量 Refresh+重开泵，会让索引搜索与图标泵反复从头来。</summary>
    private System.Windows.Threading.DispatcherTimer? _searchDebounce;

    private sealed record LaunchpadTile(
        LauncherApp? App, string Name, BitmapSource? Icon, Services.LaunchpadFolderRecord? Folder = null);

    private LaunchpadWindow(LauncherService service)
    {
        _service = service;
        InitializeComponent();
        InitLaunchpadFolders();
        _service.IndexChanged += OnIndexChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public static void ShowOrToggle(LauncherService? service)
    {
        if (_current is { IsLoaded: true })
        {
            _current.Close();
            return;
        }
        if (service is null)
        {
            Logger.Warn("启动台：启动器索引服务未装配，无法打开");
            return;
        }
        _current = new LaunchpadWindow(service);
        _current.Show();
        _current.Activate();
        Logger.Info("启动台已打开");
    }

    public static void CloseCurrent()
    {
        _current?.Close();
        _current = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!NativeMethods.GetCursorPos(out var point)) point = new NativeMethods.POINT { X = 0, Y = 0 };
        var area = MonitorGeometry.AreaForPoint(point.X, point.Y);
        if (area is { } a)
        {
            Left = StripMath.ToDip(a.Monitor.Left, a.DpiScale);
            Top = StripMath.ToDip(a.Monitor.Top, a.DpiScale);
            Width = Math.Max(1, StripMath.ToDip(a.Monitor.Width, a.DpiScale));
            Height = Math.Max(1, StripMath.ToDip(a.Monitor.Height, a.DpiScale));
        }

        var wallpaper = MachineInfoService.WallpaperPath();
        if (wallpaper is not null)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(wallpaper, UriKind.Absolute);
                image.DecodePixelWidth = 1920;
                image.EndInit();
                image.Freeze();
                WallpaperImage.Source = image;
                WallpaperImage.Effect = new BlurEffect { Radius = 36, KernelType = KernelType.Gaussian };
            }
            catch (Exception ex) { Logger.Warn($"启动台壁纸加载失败（用纯色底）: {ex.Message}"); }
        }

        ApplyGridFit();
        SizeChanged += (_, _) => ApplyGridFit();

        _service.PrewarmIndex();
        Refresh();
        PlayOpenAnimation();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    /// <summary>
    /// 按当前窗口尺寸重算列/行/图标边长。搜索条与页点占的是行外空间，所以从可用高度里先扣掉。
    /// 尺寸变化（含跨屏 DPI 变化后的重排）都要重算 —— 否则 4K 屏上会一直用笔记本的列数。
    /// </summary>
    private void ApplyGridFit()
    {
        // 70/46/70/32 是外层 Grid 的留白（见 XAML），顶部再扣搜索条 + 文件夹 chip 条、底部扣页点。
        var width = Math.Max(0, ActualWidth - 140);
        var height = Math.Max(0, ActualHeight - 78 - 96);

        var fit = LaunchpadGridRules.Fit(width, height, iconDip: 76, gapDip: 26);
        if (fit.Columns == TileColumns && fit.Rows == TileRows
            && Math.Abs(fit.IconSize - TileIconSize) < 0.5)
        {
            return;
        }

        TileColumns = fit.Columns;
        TileRows = fit.Rows;
        TileIconSize = fit.IconSize;
        RaiseTileGeometryChanged();
    }

    /// <summary>开场：0.96 → 1 缩放 + 淡入，180ms（MacDock 的 kLaunchpadOpenMs）。</summary>
    private void PlayOpenAnimation()
    {
        var scale = new ScaleTransform(0.96, 0.96);
        Root.RenderTransformOrigin = new Point(0.5, 0.5);
        Root.RenderTransform = scale;
        Root.Opacity = 0;

        var ease = new System.Windows.Media.Animation.QuadraticEase
        {
            EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
        };
        var zoom = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease
        };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
        Root.BeginAnimation(OpacityProperty, fade);
        // 动画结束后清掉变换：留着会让命中测试与文字渲染都走 GPU 合成路径，滚动时发虚。
        zoom.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Root.RenderTransform = Transform.Identity;
        };
    }

    /// <summary>退场：140ms 缩放 + 淡出后再真正关闭（MacDock 的 kLaunchpadCloseMs）。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            _closing = true;

            var scale = Root.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
            Root.RenderTransformOrigin = new Point(0.5, 0.5);
            Root.RenderTransform = scale;

            var zoom = new System.Windows.Media.Animation.DoubleAnimation(0.97, TimeSpan.FromMilliseconds(140));
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => Close();
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
            Root.BeginAnimation(OpacityProperty, fade);
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>悬停：图标放大到 1.10（MacDock 的 kLaunchpadHoverScale），90ms 起、120ms 回。</summary>
    private void Tile_MouseEnter(object sender, MouseEventArgs e) => AnimateTileIcon(sender, 1.10, 90);

    private void Tile_MouseLeave(object sender, MouseEventArgs e) => AnimateTileIcon(sender, 1.0, 120);

    private static void AnimateTileIcon(object sender, double target, int milliseconds)
    {
        if (sender is not Button { Content: StackPanel stack })
        {
            return;
        }

        var image = stack.Children.OfType<Image>().FirstOrDefault();
        if (image is null)
        {
            return;
        }

        // DataTemplate 里的 Transform 在加载后会被 WPF 冻结（模板可共享），对冻结对象
        // BeginAnimation 会抛 "因为该对象已密封或已冻结" —— 真机上表现为一悬停就弹异常框。
        // 因此每个格子第一次需要动画时换成自己的可变实例。
        if (image.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            scale = new ScaleTransform(1, 1);
            image.RenderTransform = scale;
        }

        var animation = new System.Windows.Media.Animation.DoubleAnimation(target,
            TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _iconPump?.Cancel();
        _iconPump = null;
        _searchDebounce?.Stop();
        _service.IndexChanged -= OnIndexChanged;
        if (ReferenceEquals(_current, this)) _current = null;
        Logger.Info("启动台已关闭");
    }

    private void OnIndexChanged()
    {
        if (IsLoaded) Refresh();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _page = 0;
        // 防抖 150ms（alpha.22 OOM 整改）：连续输入时每字符都全量刷新+重提图标，是纯浪费。
        _searchDebounce ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounceTick;
        _searchDebounce.Tick += OnSearchDebounceTick;
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        Refresh();
    }

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        _apps = query.Length == 0
            ? _service.AllApps
            : _service.Search(query, 200).Select(m => m.App).ToList();

        // 文件夹只在"无搜索"时参与排版：搜索是"找应用"，把文件夹塞进结果只会让人多点一次。
        var allTiles = BuildTiles(query.Length == 0);
        BuildFolderStrip(query.Length == 0);
        var pageCount = LaunchpadPagination.PageCount(allTiles.Count, PageSize);
        _page = Math.Clamp(_page, 0, Math.Max(0, pageCount - 1));
        // 图标请求尺寸随网格适配结果走（ApplyGridFit 先于 Refresh 调用）
        _iconPx = Math.Clamp((int)Math.Ceiling(TileIconSize * VisualTreeHelper.GetDpi(this).PixelsPerDip * 1.10 / 16.0) * 16,
            64, Interop.IconExtractor.PreferredSize);

        var (first, count) = LaunchpadPagination.SpanOf(_page, allTiles.Count, PageSize);
        _pageTiles = new ObservableCollection<LaunchpadTile>(allTiles.Skip(first).Take(count));
        Tiles.ItemsSource = _pageTiles;
        if (_pageTiles.Count > 0) Tiles.SelectedIndex = 0;

        // alpha.22 OOM 根因修复：旧版对全量应用同步提 256×256 图标（UI 线程），位图分配风暴
        // 2 秒内打爆 WPF 渲染通道。现在 tile 先建（图标空），当前页的图标由泵分帧补。
        PumpPageIcons();

        BuildDots(pageCount);
        Status.Text = _service.IsIndexReady
            ? (_apps.Count == 0 ? "没有匹配的应用" : $"{_apps.Count} 个应用")
            : "正在建立索引…";
    }

    /// <summary>
    /// 当前页图标分帧泵：后台逐个提取（尺寸按显示需求，不再 256 一刀切），每 6 个让出一拍——
    /// 渲染/输入优先，图标在 UI 上渐次浮现（iOS/macOS 启动台本身就这样，观感不是妥协而是同构）。
    /// 翻页/搜索/换文件夹都会重开泵（旧泵取消），所以每时每刻只有一页 ≤35 个在提取。
    /// </summary>
    private void PumpPageIcons()
    {
        _iconPump?.Cancel();
        _iconPump = new CancellationTokenSource();
        var token = _iconPump.Token;
        var tiles = _pageTiles;
        var size = _iconPx;
        _ = PumpIconsAsync(tiles, size, token);
    }

    private async Task PumpIconsAsync(IList<LaunchpadTile> tiles, int size, CancellationToken token)
    {
        const int batch = 6;
        var doneInBatch = 0;
        for (var i = 0; i < tiles.Count; i++)
        {
            if (token.IsCancellationRequested) return;
            var tile = tiles[i];
            if (tile.App is null || tile.Icon is not null) continue;   // 文件夹 tile 自带拼贴图标
            try
            {
                var app = tile.App;
                var icon = await Task.Run(() => _service.IconOf(app, size), token);
                if (token.IsCancellationRequested || icon is null) continue;
                // 续接回 UI 线程（本方法从 UI 线程启动）：索引替换触发 Replace 通知，单项重绑
                if (i < tiles.Count && ReferenceEquals(tiles[i], tile))
                    tiles[i] = tile with { Icon = icon };
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warn($"启动台图标提取失败（留空格）：{ex.GetType().Name} {ex.Message}");
            }
            if (++doneInBatch >= batch)
            {
                doneInBatch = 0;
                try { await Task.Delay(16, token); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private void BuildDots(int pageCount)
    {
        Dots.Children.Clear();
        var shown = Math.Min(pageCount, 12);
        for (var i = 0; i < shown; i++)
        {
            var selected = i == _page;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = selected ? 8 : 6,
                Height = selected ? 8 : 6,
                Margin = new Thickness(4, 0, 4, 0),
                Fill = new SolidColorBrush(selected ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF))
            };
            Dots.Children.Add(dot);
        }
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.Enter) { LaunchSelected(); e.Handled = true; return; }
        if (e.Key == Key.Down) { Tiles.Focus(); if (Tiles.Items.Count > 0) Tiles.SelectedIndex = 0; e.Handled = true; }
    }

    private void Tiles_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.Enter) { LaunchSelected(); e.Handled = true; return; }
        if (e.Key == Key.Right && _page < LaunchpadPagination.PageCount(Tiles.Items.Count + (_page * PageSize), PageSize) - 1)
        { _page++; Refresh(); e.Handled = true; }
        if (e.Key == Key.Left && _page > 0)
        { _page--; Refresh(); e.Handled = true; }
    }

    private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pageCount = LaunchpadPagination.PageCount(Tiles.Items.Count + (_page * PageSize), PageSize);
        if (pageCount <= 1) return;
        if (e.Delta < 0 && _page < pageCount - 1) _page++;
        else if (e.Delta > 0 && _page > 0) _page--;
        else return;
        Refresh();
        e.Handled = true;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LaunchpadTile tile }) Launch(tile);
    }

    private void LaunchSelected()
    {
        if (Tiles.SelectedItem is LaunchpadTile tile) Launch(tile);
        else if (_pageTiles.Count > 0) Launch(_pageTiles[0]);
    }

    private void Launch(LaunchpadTile tile)
    {
        if (tile.Folder is not null)
        {
            OpenFolder(tile.Folder);
            return;
        }

        if (tile.App is null)
        {
            return;
        }

        if (_service.Launch(tile.App, out var error))
        {
            Close();
            return;
        }
        Status.Text = $"启动失败：{error}";
    }
}
