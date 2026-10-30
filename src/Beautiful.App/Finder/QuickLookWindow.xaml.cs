using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Finder;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace Beautiful.App.Finder;

/// <summary>
/// Quick Look 预览卡（Phase 6-b 起步）：文本 / 图片 / 媒体是真实预览，PDF 与未知类型走信息卡。
/// <para>
/// 窗口纪律与访达/设置一致：非分层窗口（AllowsTransparency 保持 false）+ WindowChrome + DWM 圆角，
/// 保留 ClearType 与 DWM 阴影；CaptionHeight=0，标题条自绘拖动。空格 / Esc 关闭，
/// <see cref="ShowFor"/> 是进程级单例：同一路径再按空格即关闭（macOS 的开关语义），
/// 换成另一个路径则原地替换内容，不叠出第二块预览卡。
/// </para>
/// <para>
/// 只读不写：文本最多读 64KB（BOM 感知 UTF-8/UTF-16），图片用 <see cref="BitmapCacheOption.OnLoad"/>
/// 解码后立即关闭文件流，媒体用 <see cref="MediaElement"/> 播放且关闭时 Stop + Close。
/// 任何读取/解码/播放失败都回退信息卡，并在页脚写明中文原因——预览器绝不因为坏文件崩溃，
/// 也绝不伪造内容（PDF 明确标注“PDF 预览待接入”）。
/// </para>
/// </summary>
public partial class QuickLookWindow : Window
{
    /// <summary>文本预览上限：64KB（超大日志/源码只读开头，页脚注明截断）。</summary>
    private const int MaxTextBytes = 64 * 1024;

    /// <summary>无 BOM 时的严格 UTF-8 解码器：遇到非法字节序列再退回 Encoding.Default。</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private const string PdfNote = "PDF 预览待接入（Phase 6-b）";
    private const string NoPreviewNote = "暂无内建预览";

    private static QuickLookWindow? _current;

    private string _path = string.Empty;
    private string _mediaPath = string.Empty;
    private bool _mediaPending;

    /// <summary>
    /// 单例入口（访达空格 / <c>--quicklook &lt;path&gt;</c>）。空路径只记日志不弹窗；
    /// 同一路径再调用 = 关闭；不同路径 = 原地换内容并激活。
    /// </summary>
    public static void ShowFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Logger.Warn("快速查看：路径为空，已忽略");
            return;
        }

        if (_current is { IsLoaded: true } existing)
        {
            if (SamePath(existing._path, path))
            {
                existing.Close();   // 空格开关语义：开着同一个文件时再按一次就关
                return;
            }

            existing.SetPath(path);
            existing.Activate();
            return;
        }

        var window = new QuickLookWindow(path);
        _current = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
            {
                _current = null;
            }

            Logger.Info($"快速查看已关闭：{window._path}");
        };
        window.Show();
        window.Activate();
    }

    public QuickLookWindow(string path)
    {
        InitializeComponent();
        Loaded += (_, _) => StartPendingMedia();
        SetPath(path);
    }

    // ---------- 内容切换 ----------

    private void SetPath(string path)
    {
        _path = path;
        var name = FinderPaths.DisplayName(path);
        FileNameText.Text = name;
        FileNameText.ToolTip = path;
        Title = name.Length == 0 ? "快速查看" : $"快速查看 - {name}";

        var kind = QuickLookRouter.KindOf(path);
        ResetContent();
        Logger.Info($"快速查看已打开：{path} kind={kind}");

        try
        {
            // 文件夹没有内容预览：图标/大小/日期仍可给，走信息卡（Finder 的选中项可能是目录）。
            if (Directory.Exists(path))
            {
                ShowInfoCardCore(path, NoPreviewNote, footer: null);
                return;
            }

            switch (kind)
            {
                case QuickLookKind.Text:
                    ShowText(path);
                    break;
                case QuickLookKind.Image:
                    ShowImage(path);
                    break;
                case QuickLookKind.Media:
                    ShowMedia(path);
                    break;
                case QuickLookKind.Pdf:
                    ShowInfoCardCore(path, PdfNote, footer: null);
                    break;
                default:
                    ShowInfoCardCore(path, NoPreviewNote, footer: null);
                    break;
            }
        }
        catch (Exception ex)
        {
            // 兜底：路由/控件之外任何意外都降级为信息卡，键盘路径不外抛。
            Logger.Warn($"快速查看内容切换失败：{path} {ex.GetType().Name} {ex.Message}");
            ResetContent();
            ShowInfoCardCore(path, NoPreviewNote, $"预览失败：{Reason(ex)}");
        }
    }

    /// <summary>清空四种内容与页脚，回到“什么都不显示”的中间态（切路径/回退信息卡前调用）。</summary>
    private void ResetContent()
    {
        TextView.Text = string.Empty;
        TextView.Visibility = Visibility.Collapsed;

        ImageView.Source = null;
        ImageHost.Visibility = Visibility.Collapsed;

        MediaHost.Visibility = Visibility.Collapsed;
        _mediaPending = false;
        _mediaPath = string.Empty;
        StopMedia();

        InfoHost.Visibility = Visibility.Collapsed;

        FooterText.Text = string.Empty;
        FooterBar.Visibility = Visibility.Collapsed;
    }

    // ---------- 文本 ----------

    private void ShowText(string path)
    {
        try
        {
            var preview = ReadTextPreview(path, MaxTextBytes);
            TextView.Text = preview.Text;
            TextView.Visibility = Visibility.Visible;
            if (preview.Truncated)
            {
                ShowFooter($"已截断：仅显示前 64 KB（{preview.EncodingName}）。");
            }
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            Logger.Warn($"快速查看读取文本失败：{path} {ex.GetType().Name} {ex.Message}");
            ResetContent();
            ShowInfoCardCore(path, NoPreviewNote, $"无法读取文本：{Reason(ex)}");
        }
    }

    /// <summary>
    /// 读取文本预览：最多 64KB；BOM 感知 UTF-8/UTF-16/UTF-32，无 BOM 先按严格 UTF-8，
    /// 非法字节序列退回 <see cref="Encoding.Default"/>。文件以共享读打开，读完即关（不锁文件）。
    /// </summary>
    private static TextPreview ReadTextPreview(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var buffer = new byte[maxBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var truncated = stream.Length > read;

        string text;
        string encodingName;
        if (read >= 4 && buffer[0] == 0xFF && buffer[1] == 0xFE && buffer[2] == 0x00 && buffer[3] == 0x00)
        {
            text = Encoding.UTF32.GetString(buffer, 4, read - 4);
            encodingName = "UTF-32 LE";
        }
        else if (read >= 4 && buffer[0] == 0x00 && buffer[1] == 0x00 && buffer[2] == 0xFE && buffer[3] == 0xFF)
        {
            text = new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(buffer, 4, read - 4);
            encodingName = "UTF-32 BE";
        }
        else if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            text = Encoding.Unicode.GetString(buffer, 2, read - 2);
            encodingName = "UTF-16 LE";
        }
        else if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            text = Encoding.BigEndianUnicode.GetString(buffer, 2, read - 2);
            encodingName = "UTF-16 BE";
        }
        else if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            text = Encoding.UTF8.GetString(buffer, 3, read - 3);
            encodingName = "UTF-8";
        }
        else if (truncated)
        {
            // 截断点可能切在多字节序列中间：宽松解码，只会在末尾留下一个替换字符。
            text = Encoding.UTF8.GetString(buffer, 0, read);
            encodingName = "UTF-8";
        }
        else
        {
            try
            {
                text = StrictUtf8.GetString(buffer, 0, read);
                encodingName = "UTF-8";
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Default.GetString(buffer, 0, read);
                encodingName = Encoding.Default.EncodingName;
            }
        }

        return new TextPreview(text, truncated, encodingName);
    }

    private static bool IsReadFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or System.Security.SecurityException or DecoderFallbackException or InvalidOperationException;

    // ---------- 图片 ----------

    private void ShowImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 解码后不再持有文件流
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            bitmap.Freeze();
            ImageView.Source = bitmap;
            ImageHost.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileFormatException
                                      or NotSupportedException or ArgumentException or InvalidOperationException
                                      or System.Runtime.InteropServices.COMException)
        {
            Logger.Warn($"快速查看解码图片失败：{path} {ex.GetType().Name} {ex.Message}");
            ResetContent();
            ShowInfoCardCore(path, NoPreviewNote, $"无法解码图片：{Reason(ex)}");
        }
    }

    // ---------- 媒体 ----------

    private void ShowMedia(string path)
    {
        try
        {
            var uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            _mediaPath = path;
            MediaPlayer.Source = uri;
            MediaPlayer.Volume = 0.8;
            MediaHost.Visibility = Visibility.Visible;
            _mediaPending = true;
            if (IsLoaded)
            {
                StartPendingMedia();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException
                                      or InvalidOperationException or System.Security.SecurityException)
        {
            Logger.Warn($"快速查看媒体源无效：{path} {ex.GetType().Name} {ex.Message}");
            ResetContent();
            ShowInfoCardCore(path, NoPreviewNote, $"无法播放媒体：{Reason(ex)}");
        }
    }

    /// <summary>窗口 Show 之前设置 Source 时 MediaElement 尚未 Loaded；这里补一次 Play（LoadedBehavior 也会播）。</summary>
    private void StartPendingMedia()
    {
        if (!_mediaPending || MediaHost.Visibility != Visibility.Visible)
        {
            return;
        }

        _mediaPending = false;
        try
        {
            MediaPlayer.Play();
        }
        catch (Exception ex)
        {
            Logger.Warn($"快速查看开始播放失败：{_mediaPath} {ex.GetType().Name} {ex.Message}");
            ShowMediaFallback(_mediaPath, ex.Message);
        }
    }

    private void MediaPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (MediaHost.Visibility != Visibility.Visible)
        {
            return;
        }

        var failedPath = _mediaPath;
        var reason = string.IsNullOrWhiteSpace(e.ErrorException?.Message)
            ? "媒体组件报告了未知错误"
            : e.ErrorException!.Message;
        Logger.Warn($"快速查看媒体失败：{failedPath} {reason}");

        // 在 MediaElement 自己的事件里直接 Close 会重入媒体管线：回到 Dispatcher 下一拍再回退。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MediaHost.Visibility != Visibility.Visible
                || !string.Equals(_mediaPath, failedPath, StringComparison.Ordinal))
            {
                return;   // 用户已切到别的文件/关窗：旧失败不再影响当前内容
            }

            ShowMediaFallback(failedPath, reason);
        }));
    }

    private void ShowMediaFallback(string path, string reason)
    {
        ResetContent();
        ShowInfoCardCore(path, NoPreviewNote, $"无法播放媒体：{reason}");
    }

    /// <summary>媒体停止并释放句柄（切路径 / 关闭窗口；失败只记日志，绝不让关窗链路抛异常）。</summary>
    private void StopMedia()
    {
        try
        {
            MediaPlayer.Stop();
            MediaPlayer.Source = null;
            MediaPlayer.Close();
        }
        catch (Exception ex)
        {
            Logger.Warn($"快速查看释放媒体失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    // ---------- 信息卡 ----------

    private void ShowInfoCardCore(string path, string note, string? footer)
    {
        var isDirectory = Directory.Exists(path);

        ImageSource? icon = null;
        try
        {
            icon = IconExtractor.GetIcon(path, 64);
        }
        catch (Exception ex)
        {
            Logger.Warn($"快速查看提取图标失败：{path} {ex.GetType().Name} {ex.Message}");
        }

        InfoIcon.Source = icon;
        InfoIcon.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        InfoGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;

        InfoNameText.Text = FinderPaths.DisplayName(path);
        InfoSizeText.Text = SizeText(path, isDirectory);
        InfoKindText.Text = KindText(path, isDirectory);
        InfoModifiedText.Text = ModifiedText(path, isDirectory);
        InfoNoteText.Text = note;
        InfoHost.Visibility = Visibility.Visible;

        if (footer is not null)
        {
            ShowFooter(footer);
        }
    }

    private static string SizeText(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "—";
        }

        try
        {
            return FormatSize(new FileInfo(path).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return "—";
        }
    }

    private static string KindText(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return "文件夹";
        }

        var kind = QuickLookRouter.KindOf(path);
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

        var extension = Path.GetExtension(path).TrimStart('.');
        return extension.Length == 0 ? "文件" : $"{extension.ToUpperInvariant()} 文件";
    }

    private static string ModifiedText(string path, bool isDirectory)
    {
        try
        {
            var time = isDirectory ? Directory.GetLastWriteTime(path) : File.GetLastWriteTime(path);
            return time.Year <= 1601
                ? "—"
                : time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return "—";
        }
    }

    private static string FormatSize(long bytes)
    {
        const double kb = 1024;
        if (bytes < kb)
        {
            return $"{bytes} B";
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

    private void ShowFooter(string message)
    {
        FooterText.Text = message;
        FooterBar.Visibility = Visibility.Visible;
    }

    private static string Reason(Exception ex)
        => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;

    // ---------- 窗口交互 ----------

    /// <summary>空格 / Esc 关闭（macOS Quick Look 的开关语义；文本仍可用滚动条/滚轮浏览）。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.Key is Key.Escape or Key.Space)
        {
            e.Handled = true;
            Close();
        }
    }

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

    protected override void OnClosed(EventArgs e)
    {
        // 先收内容再释放媒体：媒体关闭可能异步抛出 MediaFailed，隐藏后处理器会直接忽略。
        MediaHost.Visibility = Visibility.Collapsed;
        _mediaPending = false;
        _mediaPath = string.Empty;
        StopMedia();
        base.OnClosed(e);
    }

    /// <summary>WindowStyle=None 后显式请求 DWM 圆角（非分层窗口也能被 DWM 裁剪，ClearType 不受影响）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var corner = NativeMethods.DwmwcpRound;
        NativeMethods.DwmSetWindowAttribute(
            new WindowInteropHelper(this).Handle,
            NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        try
        {
            var a = Path.GetFullPath(left).TrimEnd('\\', '/');
            var b = Path.GetFullPath(right).TrimEnd('\\', '/');
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>文本读取结果：正文 + 是否截断 + 实际编码名（页脚展示，便于判断乱码来源）。</summary>
    private sealed record TextPreview(string Text, bool Truncated, string EncodingName);
}
