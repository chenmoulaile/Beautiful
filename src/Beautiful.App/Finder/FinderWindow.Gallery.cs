using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Services;
using Beautiful.Core.Finder;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Beautiful.App.Finder;

/// <summary>
/// 访达窗口 · 画廊视图（Phase 6-c）：上方大预览 + 下方横向胶片条。
/// <para>
/// 胶片条的容量、原点、命中、滚动偏移与方向键全部走 <see cref="FinderGallery"/> 的纯函数；
/// 本文件只负责把状态翻译成缩略图容器、把选中项翻译成大预览。缩略图是自建的
/// <see cref="Border"/>（不是虚拟化列表），与图标视图同一形态——目录已按 1500 项截断，
/// 再多的条目在图标视图里也一样要建容器，不在这里发明第二套上限。
/// </para>
/// <para>
/// 预览分派沿用 <see cref="QuickLookRouter"/>：图片等比缩放（不放大）、文本给头 40 行、
/// 其余（含 PDF/媒体/文件夹）给大图标 + 元数据卡——**宁可给信息卡也不假装能渲染**。
/// 大图解码在后台线程完成，选中项一变就作废上一轮（代次号 + CTS），不阻塞 UI。
/// </para>
/// <para>
/// 画廊是单选视图（macOS 画廊也以"当前一张"为核心）：<c>Ctrl+A</c> 只给提示、不做全选，
/// 空格 Quick Look 只带当前一张。这条边界写进 README，不假装支持多选。
/// </para>
/// </summary>
public partial class FinderWindow
{
    /// <summary>文本预览读的行数上限（比列预览多，但仍只读头部）。</summary>
    private const int GalleryTextLines = 40;

    /// <summary>大图解码的像素宽上限：再大的图对屏幕观感无增益，却会让解码慢一个数量级。</summary>
    private const int GalleryMaxDecodePixels = 1600;

    /// <summary>胶片条缩略图的解码宽度（84 DIP 的格子 × 高 DPI 也够用）。</summary>
    private const int GalleryThumbPixels = 128;

    /// <summary>胶片条里主动解码缩略图的条目上限：其余条目继续等 shell 图标（不无限解图吃内存）。</summary>
    private const int GalleryThumbLimit = 200;

    private readonly Dictionary<FinderRow, Border> _galleryThumbs = [];
    private FinderRow? _gallerySelection;
    private BitmapImage? _galleryPreviewBitmap;
    private CancellationTokenSource? _galleryPreviewCts;
    private CancellationTokenSource? _galleryThumbCts;
    private int _galleryPreviewGeneration;

    // ---------- 胶片条 ----------

    /// <summary>
    /// 按当前可见行重建胶片条。选择按路径保留（筛选/刷新后仍停在同一个文件上）；
    /// 之前没有选中就选第一项（macOS 进入画廊即有预览）。
    /// </summary>
    private void RefreshGallery()
    {
        if (GalleryStrip is null || GalleryHost is null)
        {
            return;
        }

        var previousPath = _gallerySelection?.FullPath;
        GalleryStrip.Children.Clear();
        _galleryThumbs.Clear();

        var idle = new SolidColorBrush(Color.FromArgb(0x12, 0x80, 0x80, 0x80));
        idle.Freeze();

        for (var i = 0; i < _visibleRows.Count; i++)
        {
            var row = _visibleRows[i];
            var thumb = BuildGalleryThumb(row, idle);
            thumb.Margin = new Thickness(0, 0, i == _visibleRows.Count - 1 ? 0 : FinderGallery.Gap, 0);
            _galleryThumbs[row] = thumb;
            GalleryStrip.Children.Add(thumb);
        }

        FinderRow? selection = null;
        if (previousPath is not null)
        {
            selection = _visibleRows.FirstOrDefault(row => row.FullPath == previousPath);
        }

        selection ??= _visibleRows.FirstOrDefault();
        SetGallerySelection(selection, scrollIntoView: false);

        // 图片条目的缩略图自己解码（shell 图标对 jpg 只会给出"图片程序"的图标，
        // 而画廊的灵魂就是看到缩略图）；其余条目继续用 shell 图标 + 字形回退。
        StartGalleryThumbs();

        Logger.Info($"访达画廊：{_visibleRows.Count} 项，选中={selection?.Name ?? "无"}");

        // 一条空胶片条时给一句提示，否则用户看到的是"一片空白"。
        GalleryEmptyHint.Visibility = _visibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryEmptyHint.Text = _visibleRows.Count == 0 ? "此文件夹为空" : string.Empty;
    }

    /// <summary>建一个缩略图：图片/字形 + 选中描边；点击即选中并把键盘焦点交给画廊。</summary>
    private Border BuildGalleryThumb(FinderRow row, Brush idleBackground)
    {
        var grid = new Grid();
        var isImage = !row.IsDirectory && QuickLookRouter.KindOf(row.FullPath) == QuickLookKind.Image;
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(4),
            SnapsToDevicePixels = true
        };

        if (!isImage)
        {
            // 图片条目交给后台缩略图任务；给它绑 Icon 会与缩略图抢同一个 Source
            image.SetBinding(Image.SourceProperty, new Binding(nameof(FinderRow.Icon)));
        }

        var glyph = new TextBlock
        {
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(FinderRow.Glyph)));
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "MacTextSecondary");

        grid.Children.Add(image);
        grid.Children.Add(glyph);

        var thumb = new Border
        {
            Width = FinderGallery.ThumbWidth,
            Height = FinderGallery.ThumbHeight,
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Background = idleBackground,
            Cursor = Cursors.Hand,
            ToolTip = row.Name,
            Child = grid,
            // 绑定源：缩略图里的 Image/字形都靠它读 Icon/Glyph（不设的话绑定拿到 null DataContext，
            // 图标永远不出现——只在真机截图里才看得出来，单测抓不到这类 WPF 绑定疏漏）
            DataContext = row
        };

        thumb.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SetGallerySelection(row, scrollIntoView: true);
            GalleryHost.Focus();
        };
        thumb.MouseLeftButtonUp += (_, e) =>
        {
            // 双击打开：单击只选中，与 macOS 画廊一致（单击预览区不打开任何东西）
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                OpenRow(row);
            }
        };

        return thumb;
    }

    /// <summary>更新选中描边、滚动位置、大预览（唯一的选择入口）。</summary>
    private void SetGallerySelection(FinderRow? row, bool scrollIntoView)
    {
        _gallerySelection = row;

        var accent = GalleryAccentBrush();
        var idle = new SolidColorBrush(Color.FromArgb(0x12, 0x80, 0x80, 0x80));
        idle.Freeze();

        foreach (var pair in _galleryThumbs)
        {
            var selected = ReferenceEquals(pair.Key, row);
            pair.Value.BorderBrush = selected ? accent : Brushes.Transparent;
            pair.Value.Background = selected ? GallerySelectedBackground() : idle;
        }

        if (row is not null && scrollIntoView)
        {
            var index = _visibleRows.IndexOf(row);
            var viewport = GalleryScroll.ViewportWidth > 1 ? GalleryScroll.ViewportWidth : GalleryScroll.ActualWidth;
            GalleryScroll.ScrollToHorizontalOffset(
                FinderGallery.ScrollOffsetFor(viewport, _visibleRows.Count, index));
        }

        BeginGalleryPreview(row);
    }

    /// <summary>按方向键移动选中项（两端夹住，不环绕）；返回 false 表示没有可移动的目标。</summary>
    private bool HandleGalleryArrow(Key key)
    {
        if (_visibleRows.Count == 0)
        {
            return false;
        }

        var delta = key is Key.Left or Key.Up ? -1 : 1;
        var current = _gallerySelection is null ? -1 : _visibleRows.IndexOf(_gallerySelection);
        var next = FinderGallery.MoveSelection(current, delta, _visibleRows.Count);
        if (next < 0)
        {
            return false;
        }

        SetGallerySelection(_visibleRows[next], scrollIntoView: true);
        return true;
    }

    /// <summary>滚轮在胶片条上左右滚动（macOS 触控板/鼠标的横向语义；Shift 不改行为）。</summary>
    private void GalleryScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (GalleryScroll is null)
        {
            return;
        }

        GalleryScroll.ScrollToHorizontalOffset(GalleryScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>预览区单击：把焦点交给画廊（让 ↑↓←→ 立即可用）；双击 = 打开当前项。</summary>
    private void GalleryPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        GalleryHost.Focus();
        if (e.ClickCount == 2 && _gallerySelection is { } row)
        {
            e.Handled = true;
            OpenRow(row);
        }
    }

    /// <summary>诊断/外部入口用：按行选中（<c>--select</c>、文件操作完成后的定位）。</summary>
    private void SelectGalleryRow(FinderRow row)
    {
        if (!_galleryThumbs.ContainsKey(row))
        {
            return;   // 不在当前可见集合里：不硬造选中（例如被搜索过滤掉了）
        }

        SetGallerySelection(row, scrollIntoView: true);
        GalleryHost.Focus();
    }

    /// <summary>
    /// 清空胶片条与预览（载入态/错误态/离开目录时调用）。提示语由调用方给，
    /// 因为"正在载入…"和"此文件夹为空"是两件不同的事实，不能混用一句话。
    /// </summary>
    private void ResetGallery(string hint)
    {
        _gallerySelection = null;
        _galleryPreviewBitmap = null;
        CancelGalleryPreview();
        CancelGalleryThumbs();
        GalleryStrip?.Children.Clear();
        _galleryThumbs.Clear();

        if (GalleryPreviewContent is not null)
        {
            GalleryPreviewContent.Content = null;
        }

        if (GalleryEmptyHint is not null)
        {
            GalleryEmptyHint.Visibility = Visibility.Visible;
            GalleryEmptyHint.Text = hint;
        }
    }

    private void CancelGalleryPreview()
    {
        _galleryPreviewGeneration++;
        var cts = _galleryPreviewCts;
        _galleryPreviewCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ---------- 胶片条缩略图 ----------

    /// <summary>
    /// 为图片条目后台解码小缩略图（上限 <see cref="GalleryThumbLimit"/> 条，前到后顺序）。
    /// 与 shell 图标是两条独立的来源：这里只负责"图片看起来像图片"，取不到就继续显示字形/图标。
    /// </summary>
    private void StartGalleryThumbs()
    {
        var cts = new CancellationTokenSource();
        var previous = _galleryThumbCts;
        _galleryThumbCts = cts;
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        var targets = _visibleRows
            .Take(GalleryThumbLimit)
            .Where(row => !row.IsDirectory && QuickLookRouter.KindOf(row.FullPath) == QuickLookKind.Image)
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        _ = LoadGalleryThumbsAsync(targets, cts);
    }

    private async Task LoadGalleryThumbsAsync(IReadOnlyList<FinderRow> targets, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            foreach (var row in targets)
            {
                token.ThrowIfCancellationRequested();
                if (!_galleryThumbs.TryGetValue(row, out var thumb) || thumb.Parent is null)
                {
                    continue;   // 胶片条已重建/该条目被过滤掉：跳过
                }

                var bitmap = await Task.Run(() => DecodeGalleryImage(row.FullPath, GalleryThumbPixels), token);
                if (bitmap is null || token.IsCancellationRequested)
                {
                    continue;
                }

                // 解码期间胶片条可能已重建：旧容器不再挂在条上，结果直接丢弃
                if (!_galleryThumbs.TryGetValue(row, out var current) || !ReferenceEquals(current, thumb)
                    || thumb.Child is not Grid grid || grid.Children.Count < 2)
                {
                    continue;
                }

                if (grid.Children[0] is Image image)
                {
                    image.Source = bitmap;
                }

                // 缩略图到位后撤掉字形，避免图标字形从图片边缘透出来
                if (grid.Children[1] is TextBlock glyph)
                {
                    glyph.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 刷新/换目录/关闭窗口：停止解码
        }
        catch (Exception ex)
        {
            Logger.Warn($"访达胶片条缩略图失败：{ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_galleryThumbCts, cts))
            {
                _galleryThumbCts = null;
            }

            cts.Dispose();
        }
    }

    private void CancelGalleryThumbs()
    {
        var cts = _galleryThumbCts;
        _galleryThumbCts = null;
        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    // ---------- 大预览 ----------

    /// <summary>
    /// 画当前选中项的大预览：先同步给回退卡（图标/名称/元数据），图片与文本在后台补上。
    /// 预览任务带代次号：选中项一变，上一轮的结果即使回来也丢弃。
    /// </summary>
    private void BeginGalleryPreview(FinderRow? row)
    {
        CancelGalleryPreview();
        _galleryPreviewBitmap = null;

        if (GalleryPreviewContent is null || GalleryEmptyHint is null)
        {
            return;
        }

        GalleryPreviewContent.Content = null;

        if (row is null)
        {
            GalleryEmptyHint.Visibility = Visibility.Visible;
            GalleryEmptyHint.Text = "选择项目以预览";
            return;
        }

        GalleryEmptyHint.Visibility = Visibility.Collapsed;
        GalleryPreviewContent.Content = BuildGalleryFallback(row);

        var kind = QuickLookRouter.KindOf(row.FullPath);
        if (kind is not (QuickLookKind.Image or QuickLookKind.Text) || row.IsDirectory)
        {
            return;   // 其余类型就停在回退卡：大图标 + 元数据，不假装能渲染
        }

        var generation = _galleryPreviewGeneration;
        var cts = new CancellationTokenSource();
        _galleryPreviewCts = cts;
        var token = cts.Token;

        // 解码上限按 UI 线程量到的可用宽度估（后台只做解码/读文件，不碰可视树）。
        var (availableWidth, _) = GalleryPreviewArea();
        var dpi = 1.0;
        try
        {
            dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            // 窗口未完成初始化：按 100% 处理
        }

        _ = LoadGalleryPreviewAsync(row, kind, generation, cts, availableWidth, dpi, token);
    }

    private async Task LoadGalleryPreviewAsync(FinderRow row, QuickLookKind kind, int generation,
        CancellationTokenSource cts, double decodeWidth, double dpi, CancellationToken token)
    {
        try
        {
            if (kind == QuickLookKind.Image)
            {
                var maxPixels = (int)Math.Clamp(decodeWidth * dpi, 64, GalleryMaxDecodePixels);
                var bitmap = await Task.Run(() => DecodeGalleryImage(row.FullPath, maxPixels), token);
                if (bitmap is null || token.IsCancellationRequested || generation != _galleryPreviewGeneration)
                {
                    return;
                }

                // 布局此时已完成：用真实可用区域重算一次尺寸。首次预览常常发生在窗口布局之前，
                // 那时只能估一个宽度——直接沿用估值会让宽图/长行溢出预览框（被 Border 裁掉）。
                var (fitWidth, fitHeight) = GalleryPreviewArea();
                var (width, height) = FinderGallery.FitPreview(
                    fitWidth, fitHeight, bitmap.PixelWidth, bitmap.PixelHeight);
                _galleryPreviewBitmap = bitmap;
                GalleryPreviewContent.Content = new Image
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Fill,
                    SnapsToDevicePixels = true,
                    ToolTip = row.Name
                };
                return;
            }

            // 等宽行宽按可用宽度换算成字符数：多截几个字符比"文字被边框硬裁掉"诚实得多
            var (textWidth, textHeight) = GalleryPreviewArea();
            var maxChars = Math.Clamp((int)(textWidth / 7.0) - 2, 40, 160);
            var text = await Task.Run(() => ReadHeadLines(row.FullPath, GalleryTextLines, maxChars), token);
            if (token.IsCancellationRequested || generation != _galleryPreviewGeneration)
            {
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                return;   // 空文件/读不到：保留回退卡（信息卡本身就是诚实的答案）
            }

            var textBlock = new TextBlock
            {
                Text = text,
                FontFamily = (FontFamily)FindResource("MacFontMono"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                // 显式宽度（不是 MaxWidth）：NoWrap 的多行文本在只给 MaxWidth 时仍可能按内容宽度
                // 排版后被容器硬裁，看不到省略号。给死宽度才能保证"每行在框内结束"。
                Width = textWidth,
                MaxHeight = textHeight,
                Foreground = (Brush)FindResource("MacTextSecondary"),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            GalleryPreviewContent.Content = textBlock;
            UpdateLayout();
            Logger.Info($"访达画廊文本预览：{row.Name} {GalleryTextLines} 行 × {maxChars} 字符（框宽 {textBlock.ActualWidth:0.#}）");
        }
        catch (OperationCanceledException)
        {
            // 选中项已变：丢弃
        }
        catch (Exception ex)
        {
            Logger.Warn($"访达画廊预览失败：{row.FullPath} {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_galleryPreviewCts, cts))
            {
                _galleryPreviewCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>解码图片：按上限降采样，句柄立即释放（不长期占文件锁）。失败返回 null。</summary>
    private static BitmapImage? DecodeGalleryImage(string path, int decodePixelWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Max(16, decodePixelWidth);
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException
                                      or System.Runtime.InteropServices.COMException
                                      or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>回退卡：大图标（位图还没到时用字形）+ 名称 + 种类/大小/时间。</summary>
    private FrameworkElement BuildGalleryFallback(FinderRow row)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var box = new Grid { Width = 112, Height = 112, HorizontalAlignment = HorizontalAlignment.Center };
        if (row.Icon is { } icon)
        {
            box.Children.Add(new Image
            {
                Source = icon,
                Width = 112,
                Height = 112,
                Stretch = Stretch.Uniform
            });
        }

        box.Children.Add(new TextBlock
        {
            Text = row.Glyph,
            FontSize = 44,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("MacTextSecondary")
        });
        panel.Children.Add(box);

        panel.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 420,
            Margin = new Thickness(0, 12, 0, 4),
            Foreground = (Brush)FindResource("MacTextPrimary")
        });
        panel.Children.Add(new TextBlock
        {
            Text = row.IsDirectory ? $"文件夹 · {row.ModifiedText}" : $"{row.KindText} · {row.SizeText} · {row.ModifiedText}",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("MacTextSecondary")
        });
        panel.Children.Add(new TextBlock
        {
            Text = row.IsDirectory ? "双击打开文件夹" : "按空格键快速查看",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)FindResource("MacTextTertiary")
        });

        return panel;
    }

    /// <summary>窗口尺寸变化时按新可用区域重新贴合已解码的大图（不重新解码、不重读文件）。</summary>
    private void ApplyGalleryImageFit()
    {
        if (_galleryPreviewBitmap is null || GalleryPreviewContent?.Content is not Image image)
        {
            return;
        }

        var (availableWidth, availableHeight) = GalleryPreviewArea();
        var (width, height) = FinderGallery.FitPreview(
            availableWidth, availableHeight, _galleryPreviewBitmap.PixelWidth, _galleryPreviewBitmap.PixelHeight);
        image.Width = width;
        image.Height = height;
    }

    /// <summary>预览可用区域（DIP）：扣掉预览框四周留白；未布局时按窗口尺寸估。</summary>
    private (double Width, double Height) GalleryPreviewArea()
    {
        var width = GalleryPreviewFrame?.ActualWidth ?? 0;
        var height = GalleryPreviewFrame?.ActualHeight ?? 0;
        if (width < 8 || height < 8)
        {
            width = Math.Max(200, ActualWidth - 90);
            height = Math.Max(160, ActualHeight - 240);
        }

        return (Math.Max(80, width - 32), Math.Max(60, height - 32));
    }

    /// <summary>选中描边用的强调色（跟随外观字典的 MacAccent，缺键时退回系统蓝）。</summary>
    private Brush GalleryAccentBrush()
    {
        return TryFindResource("MacAccent") as Brush ?? new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xFF));
    }

    /// <summary>选中缩略图的底色：比常态略深一档，配合描边形成 macOS 的"浅蓝选中格"。</summary>
    private Brush GallerySelectedBackground()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x2E, 0x00, 0x7A, 0xFF));
        brush.Freeze();
        return brush;
    }
}
