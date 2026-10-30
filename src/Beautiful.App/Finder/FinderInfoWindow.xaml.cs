using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Finder;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Beautiful.App.Finder;

/// <summary>
/// 「显示简介」窗口（Phase 6-b，⌘I / Ctrl+I）：macOS 简介卡的四段式——通用 / 打开方式 / 位置 / 权限。
/// <para>
/// 窗口形态与访达/Quick Look 一致：非分层 + WindowChrome + DWM 圆角，保留 ClearType 与 DWM 阴影；
/// 标题条自绘拖动。全部字段**只读展示**：本版不提供"改权限/改打开方式"的写入路径
/// （macOS 的对应能力依赖 ACL/UTI 体系，Windows 上属高风险系统写入，按 §16.8 留给所有者决策）。
/// </para>
/// <para>
/// 元数据全部现场读取且每个字段独立容错：某一项取不到就显示 "—"，绝不让一个坏属性把整张卡打崩。
/// "占用磁盘"用 <c>GetCompressedFileSize</c>（与资源管理器同源，压缩/稀疏文件才说得准）；
/// 文件夹不做递归统计（大目录会卡界面），显示 "—" 并在字段说明里写明原因。
/// </para>
/// </summary>
public partial class FinderInfoWindow : Window
{
    private static FinderInfoWindow? _current;

    private string _path = string.Empty;

    /// <summary>单例入口：同一时间只有一张简介卡，换目标就原地重填（避免 ⌘I 连按刷出一堆窗口）。</summary>
    public static void ShowFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_current is { IsLoaded: true } existing)
        {
            existing.SetPath(path!);
            existing.Activate();
            return;
        }

        var window = new FinderInfoWindow(path!);
        _current = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
            {
                _current = null;
            }
        };
        window.Show();
        window.Activate();
    }

    /// <summary>诊断入口（<c>--shot</c>）：返回本窗口 HWND；未打开返回 0。</summary>
    public static IntPtr GetDiagnosticsHandle()
    {
        if (_current is not { IsLoaded: true } window)
        {
            return IntPtr.Zero;
        }

        try
        {
            window.Activate();
            return new WindowInteropHelper(window).Handle;
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    public FinderInfoWindow(string path)
    {
        InitializeComponent();
        SetPath(path);
    }

    private void SetPath(string path)
    {
        _path = path;
        var name = FinderPaths.DisplayName(path);
        Title = name.Length == 0 ? "简介" : $"简介 - {name}";
        Build(name);
    }

    // ---------- 内容 ----------

    private void Build(string name)
    {
        Body.Children.Clear();

        var isDirectory = Directory.Exists(_path);
        var exists = isDirectory || File.Exists(_path);

        Body.Children.Add(BuildHeader(name, isDirectory));
        Body.Children.Add(BuildSeparator());

        if (!exists)
        {
            Body.Children.Add(BuildRow("状态", "文件已不存在或无法访问", emphasize: true));
            return;
        }

        Body.Children.Add(BuildSectionTitle("通用"));
        Body.Children.Add(BuildRow("种类", KindText(isDirectory)));
        Body.Children.Add(BuildRow("大小", SizeText(isDirectory)));
        Body.Children.Add(BuildRow("占用磁盘", OnDiskText(isDirectory)));
        Body.Children.Add(BuildRow("创建时间", TimeText(created: true)));
        Body.Children.Add(BuildRow("修改时间", TimeText(created: false)));

        Body.Children.Add(BuildSeparator());
        Body.Children.Add(BuildSectionTitle("打开方式"));
        Body.Children.Add(BuildRow("默认程序", DefaultHandlerText(isDirectory)));
        Body.Children.Add(BuildRow("扩展名", ExtensionText()));

        Body.Children.Add(BuildSeparator());
        Body.Children.Add(BuildSectionTitle("位置"));
        Body.Children.Add(BuildPathBox());
        Body.Children.Add(BuildRow("所在文件夹", ParentText()));

        Body.Children.Add(BuildSeparator());
        Body.Children.Add(BuildSectionTitle("权限与属性"));
        Body.Children.Add(BuildRow("只读", YesNo(AttributeOf(FileAttributes.ReadOnly))));
        Body.Children.Add(BuildRow("隐藏", YesNo(AttributeOf(FileAttributes.Hidden))));
        Body.Children.Add(BuildRow("系统", YesNo(AttributeOf(FileAttributes.System))));
        Body.Children.Add(BuildRow("访问权限", "只读展示（本版不提供修改）"));
    }

    private FrameworkElement BuildHeader(string name, bool isDirectory)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        ImageSource? icon = null;
        try
        {
            icon = IconExtractor.GetIcon(_path, 96);
        }
        catch (Exception ex)
        {
            Logger.Warn($"简介提取图标失败：{_path} {ex.GetType().Name}");
        }

        var iconHost = new Grid { Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Top };
        if (icon is not null)
        {
            iconHost.Children.Add(new Image { Source = icon, Stretch = Stretch.Uniform });
        }
        else
        {
            iconHost.Children.Add(new TextBlock
            {
                Text = isDirectory ? "▤" : "▣",
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MacTextSecondary")
            });
        }

        Grid.SetColumn(iconHost, 0);
        grid.Children.Add(iconHost);

        var text = new StackPanel { Margin = new Thickness(12, 2, 0, 0) };
        text.Children.Add(new TextBlock
        {
            Text = name.Length == 0 ? _path : name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = KindText(isDirectory),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)FindResource("MacTextSecondary")
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private FrameworkElement BuildSectionTitle(string title)
        => new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 4),
            Foreground = (Brush)FindResource("MacTextSecondary")
        };

    /// <summary>一行"标签 —— 值"：标签固定 84 宽，值右对齐并允许换行（长路径不会撑破卡片）。</summary>
    private FrameworkElement BuildRow(string label, string value, bool emphasize = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("MacTextSecondary"),
            VerticalAlignment = VerticalAlignment.Top
        });

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
            Foreground = emphasize
                ? (Brush)FindResource("MacTextPrimary")
                : (Brush)FindResource("MacTextPrimary")
        };
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
        return grid;
    }

    /// <summary>位置：可选中复制的只读文本框（macOS 简介卡里的路径也能选中复制）。</summary>
    private FrameworkElement BuildPathBox()
    {
        var box = new TextBox
        {
            Text = _path,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("MacTextSecondary"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 2, 0, 2),
            CaretBrush = (Brush)FindResource("MacTextPrimary")
        };
        return box;
    }

    private FrameworkElement BuildSeparator()
        => new Border
        {
            Height = 1,
            Margin = new Thickness(0, 10, 0, 2),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80))
        };

    // ---------- 字段 ----------

    private string KindText(bool isDirectory)
    {
        if (isDirectory)
        {
            return "文件夹";
        }

        var kind = QuickLookRouter.KindOf(_path);
        if (kind != QuickLookKind.InfoCard)
        {
            return kind switch
            {
                QuickLookKind.Text => "文本文稿",
                QuickLookKind.Image => "图像",
                QuickLookKind.Media => "媒体",
                QuickLookKind.Pdf => "PDF 文稿",
                _ => "文件"
            };
        }

        var extension = Path.GetExtension(_path).TrimStart('.');
        return extension.Length == 0 ? "文件" : $"{extension.ToUpperInvariant()} 文件";
    }

    private string SizeText(bool isDirectory)
    {
        if (isDirectory)
        {
            return "—（文件夹不统计递归大小）";
        }

        try
        {
            return FormatSize(new FileInfo(_path).Length);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return "—";
        }
    }

    /// <summary>占用磁盘：GetCompressedFileSize（与资源管理器同源；压缩/稀疏文件才有意义）。</summary>
    private string OnDiskText(bool isDirectory)
    {
        if (isDirectory)
        {
            return "—（文件夹不统计递归大小）";
        }

        try
        {
            var low = InfoNative.GetCompressedFileSizeW(_path, out var high);
            if (low == 0xFFFFFFFF)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    return "—";
                }
            }

            var size = ((long)high << 32) + low;
            return size <= 0 ? "—" : FormatSize(size);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return "—";
        }
    }

    private string TimeText(bool created)
    {
        try
        {
            var time = created ? File.GetCreationTime(_path) : File.GetLastWriteTime(_path);
            return time.Year <= 1601
                ? "—"
                : time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return "—";
        }
    }

    private string DefaultHandlerText(bool isDirectory)
    {
        if (isDirectory)
        {
            return "访达（本程序）";
        }

        var extension = Path.GetExtension(_path);
        if (string.IsNullOrEmpty(extension))
        {
            return "—";
        }

        try
        {
            var buffer = new StringBuilder(512);
            var length = (uint)buffer.Capacity;
            var result = InfoNative.AssocQueryStringW(
                InfoNative.AssocFNone, InfoNative.AssocStrFriendlyAppName, extension, null, buffer, ref length);
            if (result == 0 && length > 1)
            {
                return buffer.ToString();
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // 老系统缺 shlwapi 导出：静默降级为"—"，不影响其它字段
        }

        return "—";
    }

    private string ExtensionText()
    {
        var extension = Path.GetExtension(_path);
        return string.IsNullOrEmpty(extension) ? "无" : extension.ToLowerInvariant();
    }

    private string ParentText()
    {
        var parent = FinderPaths.Parent(_path);
        return string.IsNullOrEmpty(parent) ? "—" : parent;
    }

    private bool? AttributeOf(FileAttributes attribute)
    {
        try
        {
            var attributes = File.GetAttributes(_path);
            return (attributes & attribute) != 0;
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            return null;
        }
    }

    private static string YesNo(bool? value) => value switch
    {
        true => "是",
        false => "否",
        _ => "—"
    };

    private static string FormatSize(long bytes)
    {
        const double kb = 1024;
        if (bytes < kb)
        {
            return $"{bytes} 字节";
        }

        if (bytes < kb * kb)
        {
            return (bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
        }

        if (bytes < kb * kb * kb)
        {
            return (bytes / (kb * kb)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        if (bytes < kb * kb * kb * kb)
        {
            return (bytes / (kb * kb * kb)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        }

        return (bytes / (kb * kb * kb * kb)).ToString("0.0", CultureInfo.InvariantCulture) + " TB";
    }

    private static bool IsIoFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException;

    // ---------- 窗口交互 ----------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已在 DragMove 前抬起：无操作
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key is Key.Escape or Key.W)
        {
            e.Handled = true;
            Close();
        }
    }

    /// <summary>WindowStyle=None 后显式请求 DWM 圆角（非分层窗口同样被 DWM 裁剪，ClearType 不受影响）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(
            new WindowInteropHelper(this).Handle,
            NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    /// <summary>简介卡需要的两个只读 Win32 查询（放这里而不是 NativeMethods：只有本窗口用）。</summary>
    private static class InfoNative
    {
        public const uint AssocFNone = 0;
        public const uint AssocStrFriendlyAppName = 4;

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int AssocQueryStringW(
            uint flags, uint str, string association, string? extra, StringBuilder output, ref uint outputLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint GetCompressedFileSizeW(string file, out uint high);
    }
}
