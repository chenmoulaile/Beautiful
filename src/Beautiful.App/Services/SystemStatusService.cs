using System;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.Core.TopBar;
using Windows.Devices.Radios;

namespace Beautiful.App.Services;

/// <summary>一次状态采样（顶栏/控制中心共用的四个字段）。</summary>
public sealed record SystemStatus(string Battery, string BatteryGlyphKey, string Network, string Bluetooth,
    string Input, string Account)
{
    public static readonly SystemStatus Unknown =
        new("--", "battery-unknown", "离线", "不可用", "EN", "未登录");
}

/// <summary>
/// 系统状态采样（P4-T9 电量/网络/蓝牙、P4-T10 输入法）：5 秒一次，全部读系统只读接口。
/// <para>
/// 【为什么统一在一个服务里】四块状态都在控制中心同一行显示，各自起一个定时器就会各读各的、
/// 刷新节奏不一致（出现"电量是新的、网络是旧的"）；一个采样点一次投递最简单也最省。
/// </para>
/// <para>
/// 【失败一律降级为占位】蓝牙 WinRT 在部分机器上会抛（无适配器/被策略禁用），输入法在无焦点窗口时
/// 拿不到；这些都只落到 <c>不可用</c> / <c>EN</c>，不抛、不弹、不写错误日志刷屏。
/// </para>
/// </summary>
public sealed class SystemStatusService : IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _bluetoothBusy;
    private bool? _bluetoothOn;
    private SystemStatus _current = SystemStatus.Unknown;

    public SystemStatus Current => _current;

    public event EventHandler? Updated;

    public SystemStatusService(Dispatcher dispatcher)
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        var status = new SystemStatus(
            Battery: ReadBatteryText(),
            BatteryGlyphKey: ReadBatteryGlyphKey(),
            Network: ReadNetwork(),
            Bluetooth: StatusTiles.BluetoothText(_bluetoothOn),
            Input: StatusTiles.InputBadge(ReadInputLanguage()),
            Account: StatusTiles.AccountLine(Environment.UserName, ReadUsbCount()));

        if (status != _current)
        {
            _current = status;
            Updated?.Invoke(this, EventArgs.Empty);
        }

        RefreshBluetoothAsync();
    }

    private static (int Percent, bool Charging, bool Unknown) ReadPower()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var sps) || sps.BatteryLifePercent == 255)
        {
            return (-1, false, true);
        }

        // AC 在线 = 充电中（BatteryFlag 的 bit3 表示"充电中"，两者取或更稳）
        var charging = sps.ACLineStatus == 1 || (sps.BatteryFlag & 8) != 0;
        return (sps.BatteryLifePercent, charging, false);
    }

    private static string ReadBatteryText()
    {
        var (percent, charging, unknown) = ReadPower();
        return unknown ? "--" : StatusTiles.BatteryDetail(percent, charging);
    }

    private static string ReadBatteryGlyphKey()
    {
        var (percent, charging, unknown) = ReadPower();
        return StatusTiles.BatteryGlyphKey(percent, charging, unknown);
    }

    /// <summary>网络一行：优先有线 → Wi-Fi（取 SSID）→ 离线。全部走系统网络栈，无阻塞调用。</summary>
    private static string ReadNetwork()
    {
        try
        {
            if (!NetworkInterface.GetIsNetworkAvailable()) return StatusTiles.NetworkText(false, false, null);

            var active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var ethernet = active.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
            var wifi = active.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            return StatusTiles.NetworkText(ethernet, wifi, null);
        }
        catch (NetworkInformationException)
        {
            return StatusTiles.NetworkText(false, false, null);
        }
    }

    /// <summary>
    /// 可移动磁盘数量（"USB 设备"的诚实口径：只数能挂载的卷，不数键鼠/摄像头——
    /// 后者要 SetupAPI 逐设备枚举，代价远大于收益，且数字对用户没有意义）。
    /// 读不到返回 -1，由 Core 决定不显示数字。
    /// </summary>
    private static int ReadUsbCount()
    {
        try
        {
            return System.IO.DriveInfo.GetDrives()
                .Count(d => d.DriveType == System.IO.DriveType.Removable && d.IsReady);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <summary>当前输入法语言标签（无焦点窗口时退回进程默认语言）。</summary>
    private static string? ReadInputLanguage()
    {
        try
        {
            var language = InputLanguageManager.Current.CurrentInputLanguage;
            return language?.Name ?? CultureInfo.CurrentUICulture.Name;
        }
        catch (InvalidOperationException)
        {
            return CultureInfo.CurrentUICulture.Name;
        }
    }

    /// <summary>
    /// 蓝牙状态：WinRT Radio 枚举。逐个 Radio 取第一个 Bluetooth 的 State。
    /// 失败（无适配器/策略禁用/API 不可用）就保持上一次的值，最终由 null → "不可用"。
    /// </summary>
    private void RefreshBluetoothAsync()
    {
        if (_bluetoothBusy) return;
        _bluetoothBusy = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access is not (RadioAccessStatus.Allowed or RadioAccessStatus.Unspecified))
                {
                    return;
                }

                var radios = await Radio.GetRadiosAsync();
                var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
                if (bluetooth is not null)
                {
                    _bluetoothOn = bluetooth.State == RadioState.On;
                }
            }
            catch (Exception)
            {
                // 无蓝牙硬件 / 无权限 / 投影缺失：保持 null（= 不可用），不刷日志
            }
            finally
            {
                _bluetoothBusy = false;
            }
        });
    }

    public void Dispose() => _timer.Stop();
}
