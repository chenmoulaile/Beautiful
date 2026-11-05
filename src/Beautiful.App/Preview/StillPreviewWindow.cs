using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
// 本项目 UseWindowsForms=true，System.Drawing 会进全局 using；与 WPF 同名类型显式消歧（同 ThumbnailPreview 手法）。
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Beautiful.App.Preview;

/// <summary>
/// 悬停预览的**画面保底层**：一张自绘的深色圆角卡，显示 <see cref="WindowCapture"/> 抓到的源窗口帧。
/// <para>
/// 【为什么需要它】Win11 24H2 上 <c>DwmExtendFrameIntoClientArea(-1)</c> 的玻璃宿主会呈现一张
/// 纯白空卡：注册 / 查尺寸 / 写属性三步 HRESULT 全是 0，屏幕上却一个缩略图像素都不合成
/// （2026-11-05 真机交互取证：悬停 Chrome 弹出 680×450 的纯白卡，等 3.5 秒仍无内容）。
/// 项目既有裁定是「注册成功 ≠ 真画出了像素」，但只留空卡等于预览功能没实现。
/// </para>
/// <para>
/// 【做法】不拆掉 DWM 实时链路（在系统正常合成时它是最鲜活的画面），而是在宿主卡片矩形上
/// 盖一张自己的卡：首帧同步抓、之后 1.2s 低频后台刷新。抓帧走项目已有的
/// <c>WindowCapture.Capture</c>（PrintWindow → 屏幕抠图退路，返回的位图已 Freeze，可跨线程）；
/// 后台刷新避免 4K 窗口的 PrintWindow 卡住 UI 线程。
/// </para>
/// </summary>
internal sealed class StillPreviewWindow : Window
{
    private readonly Image _image;
    private readonly TextBlock _titleText;
    private readonly DispatcherTimer _refresh;
    private IntPtr _source;
    private bool _busy;

    public StillPreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;      // 预览卡纯展示，绝不吞掉落在它上面的点击
        SnapsToDevicePixels = true;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF6, 0x1F, 0x1F, 0x23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(10),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _titleText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEA)),
            Margin = new Thickness(2, 0, 2, 8),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(_titleText, 0);

        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Grid.SetRow(_image, 1);

        grid.Children.Add(_titleText);
        grid.Children.Add(_image);
        card.Child = grid;
        Content = card;

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        _refresh.Tick += (_, _) => RefreshAsync();
    }

    /// <summary>按宿主窗口的物理矩形（已除以 DPI 换成 DIP）定位并显示首帧。</summary>
    public void Attach(IntPtr source, string title,
        double leftDip, double topDip, double widthDip, double heightDip)
    {
        _source = source;
        _titleText.Text = string.IsNullOrWhiteSpace(title) ? "窗口预览" : title;
        Left = leftDip;
        Top = topDip;
        Width = Math.Max(120, widthDip);
        Height = Math.Max(90, heightDip);

        if (!IsVisible) Show();
        _image.Source = WindowCapture.Capture(source);   // 首帧同步，避免先弹一张空卡
        _refresh.Start();
    }

    /// <summary>
    /// 重新声明置顶，把画面层抬到玻璃宿主之上。宿主窗口在 <c>ThumbnailPreview.Show</c> 里
    /// 晚于本窗口执行 <c>ShowWindow(SW_SHOWNOACTIVATE)</c>，不重申一次就会被那张白卡盖住。
    /// 用 Topmost 脉冲而不是 Activate()：后者会抢走用户当前窗口的焦点。
    /// </summary>
    public void Raise()
    {
        if (!IsVisible) return;
        Topmost = false;
        Topmost = true;
    }

    /// <summary>收起并停掉刷新；可安全重复调用。</summary>
    public void Detach()
    {
        _refresh.Stop();
        _source = IntPtr.Zero;
        _image.Source = null;
        if (IsVisible) Hide();
    }

    private void RefreshAsync()
    {
        if (_source == IntPtr.Zero || _busy) return;
        _busy = true;
        var hwnd = _source;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            BitmapSource? frame = null;
            try
            {
                frame = WindowCapture.Capture(hwnd);
            }
            catch
            {
                // 抓帧失败就保留上一帧：宁可画面旧一点，也不让预览闪回空卡
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _busy = false;
                if (_source == hwnd && frame is not null) _image.Source = frame;
            }));
        });
    }
}
