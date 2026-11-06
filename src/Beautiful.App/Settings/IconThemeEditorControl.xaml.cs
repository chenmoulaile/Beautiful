using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Dock;
using Beautiful.App.Services;
using Beautiful.Core.Dock;
using Beautiful.Core.Themes;
using UserControl = System.Windows.Controls.UserControl;   // UseWindowsForms 隐式全局 using 与 WinForms 同名 UserControl 消歧（项目惯例）

namespace Beautiful.App.Settings;

/// <summary>
/// 图标主题编辑器（独立自包含 UserControl）：列出当前 Dock 各格（应用名 + 当前图标缩略图），
/// 每格支持 ① 从内置 TahoeIcons 图集网格选（带预览缩略图）② 从本地 PNG 文件选 ③ 恢复默认；
/// 底条提供主题包导出/导入（本地 zip，见 <see cref="IconThemeService"/>）。
/// <para>
/// 【自包含】样式全部就地定义（深色卡片，与 Dock/控制中心同一审美），不引用宿主资源字典，
/// 宿主只需把本控件放进任意分区即可（注册做法见 settingsContract / docs/icon-theme-packs.md）。
/// </para>
/// <para>
/// 【数据口径】格子来自 <see cref="DockViewModel.Items"/> 的可编辑种类（访达/启动台/固定项/运行窗口/
/// 已最小化/回收站；分隔符、媒体、小组件没有图标身份，不列）。应用键 = Core <see cref="IconThemeKey"/>
/// 的候选键首项——Dock「一格 = 一个应用」合并用的键，就是这里的覆盖键；解析不出键的格（极少数
/// 伪路径条目）照常列出但禁用换图（保留右键「更换图标」按路径覆盖的既有通道）。
/// </para>
/// </summary>
public partial class IconThemeEditorControl : UserControl
{
    /// <summary>Dock 数据源（<see cref="Attach"/> 注入；null 时列表为空，不抛异常）。</summary>
    private DockViewModel? _dockVm;
    private readonly ObservableCollection<IconCellRow> _cells = new();
    private readonly ObservableCollection<GalleryItem> _gallery = new();
    private bool _galleryLoaded;

    /// <summary>
    /// 无参构造：设置宿主可把它当普通 XAML 元素放进分区（宿主 code-behind 再
    /// <c>IconThemeEditor.Attach(_dockVm)</c> 一行接线，见 settingsContract）。
    /// </summary>
    public IconThemeEditorControl()
    {
        InitializeComponent();
        CellList.ItemsSource = _cells;
        GalleryItems.ItemsSource = _gallery;
        Loaded += (_, _) =>
        {
            RefreshCells();
            IconThemeService.Changed += OnIconThemeChanged;
        };
        Unloaded += (_, _) => IconThemeService.Changed -= OnIconThemeChanged;
    }

    /// <summary>独立使用（代码直接构造）的便捷重载：构造即注入数据源。</summary>
    public IconThemeEditorControl(DockViewModel dockVm) : this() => Attach(dockVm);

    /// <summary>
    /// 注入 Dock 数据源（宿主在构造期或 Loaded 后调用均可）：已加载就立即刷新，
    /// 未加载则等 Loaded 事件统一刷。
    /// </summary>
    public void Attach(DockViewModel dockVm)
    {
        _dockVm = dockVm ?? throw new ArgumentNullException(nameof(dockVm));
        if (IsLoaded) RefreshCells();
    }

    /// <summary>行模型：一格 Dock 条目的编辑视图（重建式刷新，不做逐属性通知）。</summary>
    public sealed class IconCellRow
    {
        public string AppKey { get; init; } = string.Empty;
        public string Display { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string? OverrideDisplay { get; init; }
        public ImageSource? Icon { get; init; }
        public bool HasOverride { get; init; }
        public bool CanOverride { get; init; }
    }

    private sealed record GalleryItem(string Key, BitmapSource? Thumb);

    /// <summary>覆盖表变更（本控件自己改的 / 主题包导入 / 别处改的）→ 整表重建，选中格按键保持。</summary>
    private void OnIconThemeChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnIconThemeChanged);
            return;
        }
        RefreshCells();
    }

    // ---------- 格子列表 ----------

    private void RefreshCells()
    {
        var vm = _dockVm;
        if (vm is null)
        {
            _cells.Clear();
            UpdateStatus();
            UpdateDetail();
            return;
        }
        var selectedKey = (CellList.SelectedItem as IconCellRow)?.AppKey;
        var overrides = IconThemeService.Overrides;

        _cells.Clear();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in vm.Items)
        {
            if (item.Kind is not (DockItemKind.Finder or DockItemKind.Launchpad or DockItemKind.Pinned
                or DockItemKind.Window or DockItemKind.MinimizedWindow or DockItemKind.RecycleBin))
            {
                continue;   // 分隔符 / 媒体 / 小组件没有图标身份
            }

            var key = IconThemeKey.CandidatesFor(item.Kind, item.LaunchPath, item.IconSource)
                               .FirstOrDefault() ?? string.Empty;
            if (key.Length > 0 && !seenKeys.Add(key)) continue;   // 最小化区与固定区的同一应用只列一格

            // 当前图标与 Dock 渲染同一来源（覆盖命中显示覆盖，否则默认图），编辑器所见即所得。
            var icon = MacIconCatalog.TryGet(item) ?? item.Icon;
            var hasOverride = key.Length > 0 && overrides.ContainsKey(key);
            _cells.Add(new IconCellRow
            {
                AppKey = key,
                Display = string.IsNullOrWhiteSpace(item.Label) ? "(未命名)" : item.Label,
                Detail = key.Length > 0 ? $"键 {key} · {KindText(item.Kind)}" : KindText(item.Kind),
                OverrideDisplay = hasOverride && overrides.TryGetValue(key, out var reference)
                    ? IconThemeService.DescribeRef(reference)
                    : null,
                Icon = icon,
                HasOverride = hasOverride,
                CanOverride = key.Length > 0,
            });
        }

        RestoreSelection(selectedKey);
        UpdateStatus();
        UpdateDetail();
    }

    private void RestoreSelection(string? appKey)
    {
        if (_cells.Count == 0) return;
        var index = appKey is null ? -1 : _cells.ToList().FindIndex(c => c.AppKey == appKey);
        CellList.SelectedIndex = index >= 0 ? index : 0;
    }

    private void CellList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetail();

    private static string KindText(DockItemKind kind) => kind switch
    {
        DockItemKind.Finder => "常驻格",
        DockItemKind.Launchpad => "常驻格",
        DockItemKind.Pinned => "固定项",
        DockItemKind.Window => "运行窗口",
        DockItemKind.MinimizedWindow => "已最小化",
        DockItemKind.RecycleBin => "废纸篓",
        _ => kind.ToString(),
    };

    // ---------- 详情：换图 / 恢复默认 ----------

    private IconCellRow? SelectedCell => CellList.SelectedItem as IconCellRow;

    private void UpdateDetail()
    {
        var cell = SelectedCell;
        if (cell is null)
        {
            DetailImage.Source = null;
            DetailTitle.Text = "没有可编辑的格子";
            DetailKey.Text = string.Empty;
            DetailOverride.Text = string.Empty;
            PickAssetButton.IsEnabled = PickFileButton.IsEnabled = ResetCellButton.IsEnabled = false;
            return;
        }

        DetailImage.Source = cell.Icon;
        DetailTitle.Text = cell.Display;
        DetailKey.Text = cell.CanOverride ? $"应用键：{cell.AppKey}" : "无应用键（伪路径条目），不能按应用覆盖";
        DetailOverride.Text = cell.HasOverride
            ? $"当前覆盖：{cell.OverrideDisplay}"
            : "未覆盖（使用默认图标）";
        PickAssetButton.IsEnabled = PickFileButton.IsEnabled = cell.CanOverride;
        ResetCellButton.IsEnabled = cell.HasOverride;
    }

    private void PickAssetButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCell is not { CanOverride: true }) return;
        GalleryExpander.IsExpanded = !GalleryExpander.IsExpanded;
    }

    private void GalleryExpander_Expanded(object sender, RoutedEventArgs e)
    {
        if (_galleryLoaded) return;
        _galleryLoaded = true;
        foreach (var key in TahoeAssetKeys())
        {
            _gallery.Add(new GalleryItem(key, LoadAssetThumb(key)));
        }
        if (_gallery.Count == 0)
        {
            SetStatus("图集加载失败（资源清单里没有 TahoeIcons），只能用「从文件选择…」");
        }
    }

    private void GalleryItem_Click(object sender, RoutedEventArgs e)
    {
        var cell = SelectedCell;
        if (cell is not { CanOverride: true }) return;
        if ((sender as FrameworkElement)?.DataContext is not GalleryItem item) return;

        GalleryExpander.IsExpanded = false;
        if (IconThemeService.Set(cell.AppKey, IconThemeFile.AssetPrefix + item.Key, out var reason))
        {
            SetStatus($"已把 {cell.Display} 换成图集 {item.Key}，Dock 已刷新");
        }
        else
        {
            SetStatus($"换图失败：{reason}");
            Logger.Warn($"图标主题编辑器: {cell.AppKey} ← 图集 {item.Key} 失败：{reason}");
        }
    }

    private void PickFileButton_Click(object sender, RoutedEventArgs e)
    {
        var cell = SelectedCell;
        if (cell is not { CanOverride: true }) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"选择 {cell.Display} 的图标图片",
            Filter = "图片 (*.png;*.ico;*.jpg;*.jpeg;*.bmp)|*.png;*.ico;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;   // 取消不是错误（与右键「更换图标」同一姿态）

        if (IconThemeService.Set(cell.AppKey, dialog.FileName, out var reason))
        {
            SetStatus($"已把 {cell.Display} 换成 {System.IO.Path.GetFileName(dialog.FileName)}，Dock 已刷新");
        }
        else
        {
            SetStatus($"换图失败：{reason}");
            Logger.Warn($"图标主题编辑器: {cell.AppKey} ← {dialog.FileName} 失败：{reason}");
        }
    }

    private void ResetCellButton_Click(object sender, RoutedEventArgs e)
    {
        var cell = SelectedCell;
        if (cell is null || !cell.HasOverride) return;
        IconThemeService.Reset(cell.AppKey);
        SetStatus($"已恢复 {cell.Display} 的默认图标");
    }

    // ---------- 主题包 ----------

    private void ExportPack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出图标主题包",
            Filter = "图标主题包 (*.zip)|*.zip",
            FileName = SanitizeFileName(PackNameBox.Text)
        };
        if (dialog.ShowDialog() != true) return;

        if (IconThemeService.ExportPack(dialog.FileName, PackNameBox.Text, PackAuthorBox.Text,
                description: string.Empty, out var error))
        {
            SetStatus($"已导出主题包：{dialog.FileName}");
        }
        else
        {
            SetStatus($"导出失败：{error}");
        }
    }

    private void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入图标主题包",
            Filter = "图标主题包 (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        if (IconThemeService.ImportPack(dialog.FileName, out var report))
        {
            SetStatus(report);
        }
        else
        {
            SetStatus($"导入失败：{report}");
        }
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (IconThemeService.Count == 0)
        {
            SetStatus("当前没有任何覆盖。");
            return;
        }
        IconThemeService.ClearAll();
        SetStatus("已清空全部覆盖，所有格子恢复默认图标。");
    }

    // ---------- 状态行 / 图集清单 ----------

    private void UpdateStatus()
        => SetStatus($"共 {IconThemeService.Count} 条覆盖。选中左侧格子换图；改动即时生效并保存到 config/icon-theme.json。");

    private void SetStatus(string text) => StatusText.Text = text;

    private static string SanitizeFileName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) trimmed = trimmed.Replace(c, '_');
        return trimmed.Length == 0 ? "我的图标主题" : trimmed;
    }

    /// <summary>
    /// 内置 TahoeIcons 图集键清单：从程序集的 .g.resources 资源清单里枚举（运行时真实存在哪些
    /// PNG 就列哪些，与编译进程序的资产一致，不需要另维护一张表）。
    /// </summary>
    private static IReadOnlyList<string> TahoeAssetKeys()
    {
        const string prefix = "assets/tahoeicons/";
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) return Array.Empty<string>();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return Array.Empty<string>();
            using var reader = new ResourceReader(stream);
            var keys = new List<string>();
            foreach (DictionaryEntry entry in reader)
            {
                if (entry.Key is not string name) continue;
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                keys.Add(name[prefix.Length..^4]);
            }
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }
        catch (Exception ex)
        {
            Logger.Warn($"图集清单枚举失败: {ex.GetType().Name} {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>图集键 → 48px 缩略图（DecodePixelWidth 端上就缩小，1024 原版不占内存）。</summary>
    private static BitmapSource? LoadAssetThumb(string key)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,/Assets/TahoeIcons/{key}.png", UriKind.Absolute);
            image.DecodePixelWidth = 48;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException
                                        or FileFormatException)
        {
            return null;   // 单张失败只缺一格缩略图，不拖垮整个网格
        }
    }
}
