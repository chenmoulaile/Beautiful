using System.Runtime.InteropServices;
using Beautiful.App.Interop;
using Beautiful.Core.Network;

namespace Beautiful.App.Services;

/// <summary>
/// WiFi 面板的门面：一次会话内持有 wlanapi 句柄与"我们操作哪块网卡"的判定。
/// <para>
/// 所有调用都是<b>同步阻塞</b>的（wlanapi 没有异步版本），面板侧一律丢到后台线程再回投递到
/// Dispatcher——<see cref="QueryNetworks"/> 在信号多的环境里能跑到几百毫秒，放 UI 线程上就是面板打开卡顿。
/// </para>
/// <para>
/// 连接流程是"写 profile → 按 profile 名连"两步：<see cref="Core.Network.WifiProfileXml"/> 生成的 XML
/// 经 <c>WlanSetProfile(overwrite=true)</c> 落进系统，再 <c>WlanConnect(smConnectProfile)</c>。
/// profile 的 <c>connectionMode</c> 是 <c>manual</c>，所以本程序连过一次不会让系统以后每次开机自动重连它。
/// </para>
/// </summary>
public sealed class WifiService : IDisposable
{
    private IntPtr _handle = IntPtr.Zero;
    private Guid _interfaceGuid;
    private bool _ready;

    /// <summary>适配器是否可用（没有 WiFi 硬件 / 服务没起 / 飞行模式下都是 false，面板要给出诚实空态）。</summary>
    public bool IsAvailable => _ready;

    /// <summary>不可用的原因，直接进面板文案。</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>网卡名（面板抬头用；取不到就是空串，不编）。</summary>
    public string AdapterName { get; private set; } = "";

    public WifiService()
    {
        try
        {
            var open = WlanApi.WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
            if (open != WlanApi.Success)
            {
                // 0x00094C4D：WLAN AutoConfig 服务没跑；桌面机/精简系统上很常见
                UnavailableReason = WlanApi.DescribeError(open);
                return;
            }
            _handle = handle;

            var enumCode = WlanApi.WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (enumCode != WlanApi.Success)
            {
                UnavailableReason = WlanApi.DescribeError(enumCode);
                return;
            }
            try
            {
                var count = WlanApi.ListCount(list);
                WlanApi.WlanInterfaceInfo? first = null;
                var notReady = 0;
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WlanApi.WlanInterfaceInfo>(WlanApi.ListEntry<WlanApi.WlanInterfaceInfo>(list, i));
                    // 有网卡就够扫描用了；not_ready 只是初始化中的瞬态，不代表机器没网卡。
                    if (info.State == WlanApi.InterfaceStateNotReady) { notReady++; first ??= info; continue; }
                    first ??= info;
                    break;
                }
                if (first is { } chosen)
                {
                    _interfaceGuid = chosen.InterfaceGuid;
                    AdapterName = chosen.Description ?? "";
                    _ready = true;
                    if (notReady > 0 && count == notReady)
                        Logger.Info($"WiFi 网卡 {notReady} 块都处于 not_ready（初始化中），面板可能短暂为空");
                }
                else
                {
                    UnavailableReason = count == 0 ? "这台机器没有无线网卡" : "无线网卡当前不可用（可能已禁用或处于飞行模式）";
                }
            }
            finally
            {
                WlanApi.WlanFreeMemory(list);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            UnavailableReason = $"wlanapi 不可用：{ex.GetType().Name}";
        }
    }

    /// <summary>
    /// 网卡当前是否启用。<b>读不到返回 null</b>（不是 false）——界面要区分"关着"和"问不出来"，
    /// 把后者画成"关"就等于给了一个看起来正常、实际点不动的开关。
    /// </summary>
    public bool? IsAdapterEnabled()
    {
        if (!_ready) return null;
        var code = WlanApi.WlanQueryInterface(_handle, ref _interfaceGuid,
            WlanApi.OpcodeInterfaceState, IntPtr.Zero, out var data, out _);
        if (code != WlanApi.Success || data == IntPtr.Zero) return null;
        try { return Marshal.ReadInt32(data) != 0; }
        finally { WlanApi.WlanFreeMemory(data); }
    }

    /// <summary>开 / 关网卡（控制中心的 Wi-Fi 砖块）。失败时 error 给出系统原因，不静默。</summary>
    public bool SetAdapterEnabled(bool enabled, out string? error)
    {
        error = null;
        if (!_ready)
        {
            error = UnavailableReason ?? "没有可用的无线网卡";
            return false;
        }
        var ptr = Marshal.AllocHGlobal(sizeof(int));   // wlan_interface_state_settings 就是一个 BOOL(int)
        try
        {
            Marshal.WriteInt32(ptr, enabled ? 1 : 0);
            var code = WlanApi.WlanSetInterface(_handle, ref _interfaceGuid,
                WlanApi.OpcodeInterfaceState, sizeof(int), ptr, IntPtr.Zero);
            if (code == WlanApi.Success)
            {
                Logger.Info($"WiFi 网卡已{(enabled ? "启用" : "禁用")}");
                return true;
            }
            error = WlanApi.DescribeError(code);
            Logger.Warn($"WiFi 网卡开关失败: {error}");
            return false;
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>当前可用网络列表（已按 <see cref="WifiSignal.Order"/> 排好）。不可用或读失败 → 空表。</summary>
    public List<WifiNetwork> QueryNetworks()
    {
        var result = new List<WifiNetwork>();
        if (!_ready) return result;
        var code = WlanApi.WlanGetAvailableNetworkList(_handle, ref _interfaceGuid, 0, IntPtr.Zero, out var list);
        if (code != WlanApi.Success)
        {
            Logger.Warn($"WiFi 网络列表读取失败: {WlanApi.DescribeError(code)}");
            return result;
        }
        try
        {
            var count = WlanApi.ListCount(list);
            for (var i = 0; i < count; i++)
            {
                var n = Marshal.PtrToStructure<WlanApi.WlanAvailableNetwork>(
                    WlanApi.ListEntry<WlanApi.WlanAvailableNetwork>(list, i));
                var ssid = n.Dot11Ssid.ToSsid();
                // SSID 为空的条目是隐藏网络：不知道名字就没法生成 profile，也不该在列表里画一行点不动的东西。
                if (ssid.Length == 0) continue;

                var auth = (int)n.DefaultAuthAlgorithm;
                var connected = (n.Flags & WlanApi.NetworkConnected) != 0;
                var joinable = WifiSecurity.CanJoinDirectly(auth, out var reason);
                if (!n.NetworkConnectable)
                {
                    joinable = false;
                    reason = "该网络当前不可连接";
                }
                result.Add(new WifiNetwork(
                    ssid,
                    (int)n.SignalQuality,
                    connected,
                    WifiSecurity.Describe(auth, (int)n.DefaultCipherAlgorithm),
                    joinable,
                    connected ? null : reason));
            }
        }
        finally
        {
            WlanApi.WlanFreeMemory(list);
        }
        return WifiSignal.Order(result);
    }

    /// <summary>当前连着的那个网络名（没连上返回 null）。</summary>
    public string? ConnectedSsid() => QueryNetworks().FirstOrDefault(n => n.Connected).Ssid;

    /// <summary>让系统重新扫描一遍（结果异步产生，调用方稍后再 <see cref="QueryNetworks"/> 取）。</summary>
    public bool TriggerScan()
    {
        if (!_ready) return false;
        var code = WlanApi.WlanScan(_handle, ref _interfaceGuid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (code != WlanApi.Success) Logger.Warn($"WiFi 扫描请求失败: {WlanApi.DescribeError(code)}");
        return code == WlanApi.Success;
    }

    /// <summary>
    /// 连接：写 profile（覆盖同名）→ 按 profile 名连。
    /// <para>口令只在参数与 wlanapi 之间流动，<b>绝不进日志、绝不进返回值</b>；
    /// 失败信息也只回错误码文案，wlanapi 不会把口令回显出来。</para>
    /// </summary>
    public (bool Ok, string? Error) Connect(string ssid, int authAlgorithm, string? passphrase)
    {
        if (!_ready) return (false, UnavailableReason ?? "无线网卡不可用");
        string xml;
        try
        {
            xml = WifiProfileXml.Build(ssid, authAlgorithm, passphrase);
        }
        catch (ArgumentException ex)
        {
            return (false, ex.Message);
        }

        var setCode = WlanApi.WlanSetProfile(_handle, ref _interfaceGuid, 0, xml, IntPtr.Zero, true, IntPtr.Zero, out var reason);
        if (setCode != WlanApi.Success)
            return (false, $"写入网络配置失败：{WlanApi.DescribeError(setCode)}（reason 0x{reason:X8}）");

        var parameters = new WlanApi.WlanConnectionParameters
        {
            ConnectionMode = WlanApi.ConnectionModeProfile,
            Profile = ssid,
            Dot11Ssid = IntPtr.Zero,
            DesiredBssidList = IntPtr.Zero,
            Dot11BssType = WlanApi.BssTypeInfrastructure,
            Flags = 0,
        };
        var connectCode = WlanApi.WlanConnect(_handle, ref _interfaceGuid, ref parameters, IntPtr.Zero);
        if (connectCode != WlanApi.Success)
            return (false, $"连接请求被拒绝：{WlanApi.DescribeError(connectCode)}");

        Logger.Info($"WiFi：已提交连接请求 {ssid}");
        return (true, null);
    }

    /// <summary>断开当前连接（不删 profile，用户下次还能连）。</summary>
    public (bool Ok, string? Error) Disconnect()
    {
        if (!_ready) return (false, UnavailableReason ?? "无线网卡不可用");
        var code = WlanApi.WlanDisconnect(_handle, ref _interfaceGuid, IntPtr.Zero);
        return code == WlanApi.Success ? (true, null) : (false, WlanApi.DescribeError(code));
    }

    /// <summary>面板抬头的一行状态：连着谁 / 为什么不可用。</summary>
    public string HeaderStatus()
    {
        if (!_ready) return UnavailableReason ?? "无线网卡不可用";
        var connected = ConnectedSsid();
        return string.IsNullOrEmpty(connected) ? "未连接" : connected;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        try { WlanApi.WlanCloseHandle(_handle, IntPtr.Zero); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* 关掉即弃，不值得抛 */ }
        _handle = IntPtr.Zero;
        _ready = false;
    }
}
