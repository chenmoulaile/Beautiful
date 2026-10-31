using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.App.Themes;
using Beautiful.Core.Design;

namespace Beautiful.App.Shell;

/// <summary>
/// Big Sur「关于本机」信息卡（v4 Phase 4-T2 起步）。非分层 + WindowChrome + DWM 圆角；
/// 信息取自注册表/内核 API，取不到显示“—”，不伪造。
/// </summary>
public partial class AboutWindow : Window
{
    private static AboutWindow? _current;

    public static void ShowSingleton()
    {
        if (_current is { IsLoaded: true })
        {
            _current.Activate();
            return;
        }
        _current = new AboutWindow();
        _current.Closed += (_, _) => _current = null;
        _current.Show();
        _current.Activate();
        Logger.Info("关于本机已打开");
    }

    public AboutWindow()
    {
        InitializeComponent();
        DataContext = this;
        VersionText.Text = $"v{ProductInfo.Version} · macOS Big Sur 还原壳";
        SummaryText.Text = $"{MachineInfoService.OsVersion}\n{ProductInfo.Version} · {FontResolution.Describe(FontProbe.Current)}";
        MachineText.Text = MachineInfoService.MachineName;
        ModelText.Text = MachineInfoService.Model;
        CpuText.Text = MachineInfoService.Cpu;
        MemoryText.Text = MachineInfoService.Memory;
        OsText.Text = MachineInfoService.OsVersion;
        FontText.Text = FontProbe.Current.Source;

        var wallpaper = MachineInfoService.WallpaperPath();
        if (wallpaper is not null)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(wallpaper, UriKind.Absolute);
                image.DecodePixelWidth = 1040;
                image.EndInit();
                image.Freeze();
                WallpaperImage.Source = image;
                WallpaperImage.Visibility = Visibility.Visible;
                WallpaperFallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Warn($"关于本机：壁纸缩略图解码失败（用渐变底）: {ex.Message}");
            }
        }
    }

    // 自家窗口一律真动画（v2.2 §4.4）：关于本机与设置窗口同一条收拢退场链，防重入同款。
    private bool _closeAnimated;

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeAnimated)
        {
            e.Cancel = true;
            _closeAnimated = true;
            Beautiful.App.Animation.OwnWindowAnimator.CloseAnimated(this);
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var corner = 2; // DWMWCP_ROUND
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void More_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:about") { UseShellExecute = true }); }
        catch (Exception ex) { Logger.Warn($"打开系统关于页失败：{ex.Message}"); }
    }
}
