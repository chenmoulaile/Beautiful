using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Beautiful.App.Launcher;

/// <summary>
/// Launchpad（v4 Phase 5-T1 起步）：全屏、壁纸底、7×5 网格、搜索、翻页、点击/Enter 启动。
/// 复用 LauncherService 的索引与图标管线；文件夹、拖拽、自定义排序、Dock 图标入口仍待后续。
/// </summary>
public partial class LaunchpadWindow : Window
{
    private const int Columns = 7;
    private const int Rows = 5;
    private const int PageSize = Columns * Rows;

    private static LaunchpadWindow? _current;

    private readonly LauncherService _service;
    private IReadOnlyList<LauncherApp> _apps = Array.Empty<LauncherApp>();
    private List<LaunchpadTile> _pageTiles = new();
    private int _page;

    private sealed record LaunchpadTile(LauncherApp App, string Name, BitmapSource? Icon);

    private LaunchpadWindow(LauncherService service)
    {
        _service = service;
        InitializeComponent();
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

        _service.PrewarmIndex();
        Refresh();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
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
        Refresh();
    }

    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        _apps = query.Length == 0
            ? _service.AllApps
            : _service.Search(query, 200).Select(m => m.App).ToList();

        var pageCount = LaunchpadPagination.PageCount(_apps.Count, PageSize);
        _page = Math.Clamp(_page, 0, Math.Max(0, pageCount - 1));
        var (first, count) = LaunchpadPagination.SpanOf(_page, _apps.Count, PageSize);
        _pageTiles = new List<LaunchpadTile>(count);
        foreach (var app in _apps.Skip(first).Take(count))
        {
            _pageTiles.Add(new LaunchpadTile(app, app.Name, _service.IconOf(app)));
        }
        Tiles.ItemsSource = _pageTiles;
        if (_pageTiles.Count > 0) Tiles.SelectedIndex = 0;

        BuildDots(pageCount);
        Status.Text = _service.IsIndexReady
            ? (_apps.Count == 0 ? "没有匹配的应用" : $"{_apps.Count} 个应用")
            : "正在建立索引…";
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
        if (e.Key == Key.Right && _page < LaunchpadPagination.PageCount(_apps.Count, PageSize) - 1)
        { _page++; Refresh(); e.Handled = true; }
        if (e.Key == Key.Left && _page > 0)
        { _page--; Refresh(); e.Handled = true; }
    }

    private void Root_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pageCount = LaunchpadPagination.PageCount(_apps.Count, PageSize);
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
        if (_service.Launch(tile.App, out var error))
        {
            Close();
            return;
        }
        Status.Text = $"启动失败：{error}";
    }
}
