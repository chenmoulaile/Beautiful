using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Beautiful.App.Controls;
using Beautiful.App.Services;
using Beautiful.Core.Layout;
using Beautiful.Core.Network;
using UserControl = System.Windows.Controls.UserControl;   // UseWindowsForms 隐式 using 与 WinForms 同名
using Button = System.Windows.Controls.Button;             // 同上
using Orientation = System.Windows.Controls.Orientation;   // 同上
using Brush = System.Windows.Media.Brush;                  // 同上，System.Drawing.Brush
using Brushes = System.Windows.Media.Brushes;              // 同上
using Color = System.Windows.Media.Color;                  // 同上
using FontFamily = System.Windows.Media.FontFamily;        // 同上
using Point = System.Windows.Point;                        // 同上

namespace Beautiful.App.TopBar;

/// <summary>
/// 顶栏网络图标的自家 WiFi 面板：列网络、看信号、点一下连，全程不借 Windows 的飞轮或设置页。
/// <para>
/// 【为什么所有 wlanapi 调用都在后台线程】<c>WlanGetAvailableNetworkList</c> 是同步阻塞的，
/// 周边网络多时能跑到几百毫秒；面板是"显示一次"的瞬态表面，把它放 UI 线程上就是打开卡顿。
/// 回投一律走 <see cref="Dispatcher"/>，且每个回调都先问 <c>IsLoaded</c>——面板可能已经关了。
/// </para>
/// <para>
/// 【口令】只在"需要口令且系统里还没有它的配置文件"时就地展开一行输入，不弹第二层窗口；
/// 口令只交给 <see cref="WifiService.Connect"/>，不进日志、不进任何文案。
/// 企业级/证书类网络在列表里<b>置灰并写明原因</b>，同时给出"系统网络设置…"这条唯一出路——
/// 不给一个填了也连不上的自建表单（项目裁定：不放死控件）。
/// </para>
/// </summary>
public partial class WifiPanel : UserControl, IBackdropAware, IPopupShadow
{
    private readonly WifiService _wifi = new();
    private FluentPopupHost? _host;

    /// <summary>正在等的连接目标（非 null 时整张列表禁用，避免连点两次）。</summary>
    private WifiNetwork? _pending;

    /// <summary>口令行当前挂着哪个网络（null = 收起）。</summary>
    private WifiNetwork? _passTarget;

    /// <summary>投影留白 = 自己根元素的 Margin（唯一来源，不与 XAML 各说各话）。</summary>
    public PopupShadow ShadowInset => new(Shell.Margin.Left, Shell.Margin.Top, Shell.Margin.Right, Shell.Margin.Bottom);

    public WifiPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>宿主注入：连接成功/失败后要能收起自己。</summary>
    public void AttachHost(FluentPopupHost host) => _host = host;

    /// <summary>释放 wlanapi 句柄（面板关闭时调用，幂等）。</summary>
    public void Shutdown() => _wifi.Dispose();

    public void OnBackdropReady(ScreenBackdrop? backdrop)
    {
        var blur = backdrop?.Brush;
        if (blur is not null)
        {
            Card.Background = blur;
            Scrim.Background = (Brush)Resources["CardFillBlur"];
            return;
        }
        Card.Background = (Brush)Resources["CardFillSolid"];
        Scrim.Background = Brushes.Transparent;
    }

    // ---------- 数据 ----------

    /// <summary>后台取列表，await 回来重建行。取不到就画空态，绝不留着上一轮的旧数据冒充新的。</summary>
    private async void Refresh()
    {
        HeaderStatus.Text = _wifi.IsAvailable ? (_wifi.ConnectedSsid() ?? "未连接") : "不可用";
        if (!_wifi.IsAvailable)
        {
            NetworkHost.Children.Clear();
            ShowStatus(_wifi.UnavailableReason ?? "无线网卡不可用");
            return;
        }
        ShowStatus("读取网络列表…");
        List<WifiNetwork> networks;
        try
        {
            networks = await System.Threading.Tasks.Task.Run(() => _wifi.QueryNetworks());
        }
        catch (Exception ex)
        {
            Logger.Warn($"WiFi 列表异常: {ex.Message}");
            networks = [];
        }
        if (!IsLoaded) return;   // 面板可能已经关了
        BuildRows(networks);
    }

    private void BuildRows(List<WifiNetwork> networks)
    {
        NetworkHost.Children.Clear();
        if (networks.Count == 0)
        {
            ShowStatus("没搜到任何无线网络");
            return;
        }
        StatusLine.Visibility = Visibility.Collapsed;

        foreach (var n in networks) NetworkHost.Children.Add(MakeRow(n));
    }

    /// <summary>一行：信号格 + 网络名 + 加密方式 +（已连接勾 / 置灰原因），点击即连。</summary>
    private UIElement MakeRow(WifiNetwork n)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bars = MakeSignalBars(n.SignalQuality);
        Grid.SetColumn(bars, 0);
        grid.Children.Add(bars);

        var text = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = n.Ssid,
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = n.Connected ? "已连接" : n.SecurityLabel,
            FontSize = 10.5,
            Opacity = 0.66,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (n.Connected)
        {
            Grid.SetColumn(MakeGlyph("\uE73E"), 2);   // Segoe Fluent CheckMark
            grid.Children.Add(MakeGlyph("\uE73E"));
        }

        var row = new Button
        {
            Content = grid,
            Style = (Style)Resources["RowButtonStyle"],
            Margin = new Thickness(0, 1, 0, 0),
            IsEnabled = n.Joinable && _pending is null,
            Tag = n,
            ToolTip = n.Joinable || n.Connected ? null : n.NotJoinableReason,
        };
        row.Click += Row_Click;
        return row;
    }

    /// <summary>四根柱子画信号（<see cref="WifiSignal.Bars"/> 决定点亮几根）；不依赖任何图标字体。</summary>
    private UIElement MakeSignalBars(int quality)
    {
        var lit = WifiSignal.Bars(quality);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < 4; i++)
        {
            panel.Children.Add(new Border
            {
                Width = 3,
                Height = 5 + i * 3,
                Margin = new Thickness(0, 0, 1.5, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(1),
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(i < lit ? 0xF2 : 0x3C), 0xFF, 0xFF, 0xFF)),
            });
        }
        return panel;
    }

    private static TextBlock MakeGlyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 13,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 0, 0),
        Opacity = 0.8,
    };

    // ---------- 交互 ----------

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WifiNetwork n }) return;
        HidePassBox();

        var auth = AuthOf(n);
        // 系统里已有配置文件的（含"已连接过"的）不再要口令：让 wlanapi 用它自己存的凭据
        if (WifiSecurity.NeedsPassphrase(auth))
        {
            ShowPassBox(n, auth);
            return;
        }
        _ = StartConnectAsync(n, auth, null);
    }

    /// <summary>
    /// 加密方式标签 → 认证枚举的反向映射。列表项里没带原始枚举（面板不需要它，
    /// 只有连的时候要用），所以从标签折回来：标签是 <see cref="WifiSecurity.Describe"/> 生成的，
    /// 一对一可逆，因此这里不引入第二套判定口径。
    /// </summary>
    private static int AuthOf(WifiNetwork n) => n.SecurityLabel switch
    {
        "开放" => WifiSecurity.Auth80211Open,
        "共享密钥" => WifiSecurity.Auth80211Shared,
        "WPA-TKIP" => WifiSecurity.AuthWpa,
        "WPA2-AES" or "WPA2-TKIP" => WifiSecurity.AuthWpa2,
        "WPA3-AES" => WifiSecurity.AuthWpa3,
        _ => WifiSecurity.AuthWpa2,
    };

    private void ShowPassBox(WifiNetwork n, int auth)
    {
        _passTarget = n;
        PassHint.Text = $"连接到“{n.Ssid}”（{n.SecurityLabel}）需要密码";
        PassError.Visibility = Visibility.Collapsed;
        PassInput.Password = "";
        PassBox.Visibility = Visibility.Visible;
        PassInput.Focus();
    }

    private void HidePassBox()
    {
        _passTarget = null;
        PassBox.Visibility = Visibility.Collapsed;
    }

    private void PassConnect_Click(object sender, RoutedEventArgs e)
    {
        if (_passTarget is not WifiNetwork n) return;
        var pass = PassInput.Password;
        if (pass.Length == 0)
        {
            PassError.Text = "密码不能为空";
            PassError.Visibility = Visibility.Visible;
            return;
        }
        _ = StartConnectAsync(n, AuthOf(n), pass);
    }

    private void PassCancel_Click(object sender, RoutedEventArgs e) => HidePassBox();

    /// <summary>连接：整张列表禁用 → 后台跑 wlanapi → 失败把原因写回状态行，成功则收起面板。</summary>
    private async System.Threading.Tasks.Task StartConnectAsync(WifiNetwork n, int auth, string? passphrase)
    {
        _pending = n;
        HidePassBox();
        ShowStatus($"正在连接“{n.Ssid}”…");
        foreach (var child in NetworkHost.Children)
            if (child is Button b) b.IsEnabled = false;

        var (ok, error) = await System.Threading.Tasks.Task.Run(
            () => _wifi.Connect(n.Ssid, auth, passphrase));

        if (!IsLoaded) return;
        _pending = null;
        if (ok)
        {
            Logger.Info($"WiFi：连接请求已提交 {n.Ssid}");
            _host?.Close();
            return;
        }
        ShowStatus(error ?? "连接失败");
        Refresh();
    }

    private void ShowStatus(string text)
    {
        StatusLine.Text = text;
        StatusLine.Visibility = Visibility.Visible;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus("已请求扫描，稍候再刷新…");
        _ = System.Threading.Tasks.Task.Run(() => _wifi.TriggerScan());
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-wifi")
            { UseShellExecute = true });
            _host?.Close();
        }
        catch (Exception ex)
        {
            // 常驻进程不该因为一次打不开设置页就炸：原因写回状态行
            ShowStatus($"打不开系统网络设置：{ex.Message}");
        }
    }
}
