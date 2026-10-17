using System.Runtime.InteropServices;

namespace Beautiful.App.Interop;

/// <summary>
/// <c>wlanapi.dll</c>（无线局域网 API）的最小互操作面。
/// <para>
/// 结构体逐字段照 <c>wlanapi.h</c> 的官方定义对齐（已按 MSDN 核对，不是凭记忆）：
/// <c>WLAN_INTERFACE_INFO</c> 只有 <b>一个</b> 256 WCHAR 名字字段 + 状态（没有 FriendlyName）；
/// <c>WLAN_AVAILABLE_NETWORK</c> 里的 <c>b*</c> 是 <b>BOOL</b>（4 字节，C# 的 <c>bool</c> 默认封送正好是它，
/// 用 <c>UnmanagedType.I1</c> 反而会错），"是否已连接/是否有配置文件"在末尾的 <c>dwFlags</c> 里而不是独立布尔。
/// 这类结构错位一个字段就会读到隔壁字符串，所以注释里把口径写死。
/// </para>
/// <para>
/// 所有 <c>*_LIST</c> 返回的内存必须由 <see cref="WlanFreeMemory"/> 归还；句柄由服务层进程级复用一次。
/// </para>
/// </summary>
internal static class WlanApi
{
    public const uint Success = 0;
    public const int MaxNameChars = 256;      // WLAN_MAX_NAME_LENGTH
    public const int SsidMaxBytes = 32;       // DOT11_SSID_MAX_LENGTH
    public const int MaxPhyTypes = 8;         // WLAN_MAX_PHY_TYPE_NUMBER

    /// <summary>wlan_connection_mode_profile：按已写入的 profile 名连接。</summary>
    public const int ConnectionModeProfile = 0;

    /// <summary>WLAN_AVAILABLE_NETWORK 的 dwFlags。</summary>
    public const uint NetworkConnected = 0x00000001;
    public const uint NetworkHasProfile = 0x00000002;

    /// <summary>
    /// WLAN_INTERFACE_STATE 全档（wlanapi.h 的 wlan_interface_state 枚举）：
    /// 0 not_ready / 1 connected / 2 ad_hoc_joined / 3 disconnecting / 4 disconnected /
    /// 5 associating / 6 discovering / 7 authenticating。
    /// 曾经只写两个值还都抄错了档位（2、5 实际是 ad_hoc_joined / associating），于是
    /// "已连接=1"的常态网卡反而过不了"适配器可用"判定，面板永远显示不可用——抄码不核对
    /// 文档是这个家族的第二起（前一起是 ShellHook 的 HSHELL_ACTIVATED）。
    /// </summary>
    public const int InterfaceStateNotReady = 0, InterfaceStateConnected = 1, InterfaceStateAdHocJoined = 2,
                      InterfaceStateDisconnecting = 3, InterfaceStateDisconnected = 4, InterfaceStateAssociating = 5,
                      InterfaceStateDiscovering = 6, InterfaceStateAuthenticating = 7;

    /// <summary>DOT11_BSS_TYPE：0=any（本程序只连基础设施网络，profile 里已写 ESS）。</summary>
    public const int BssTypeInfrastructure = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Dot11Ssid
    {
        public uint Length;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = SsidMaxBytes)]
        public byte[] Octets;

        /// <summary>SSID 按 UTF-8 解码（802.11 的规定就是原始字节；中文 SSID 在 Windows 上以 UTF-8 存）。</summary>
        public readonly string ToSsid()
        {
            if (Octets is null || Length == 0) return "";
            var n = (int)Math.Min(Length, (uint)Octets.Length);
            try { return System.Text.Encoding.UTF8.GetString(Octets, 0, n); }
            catch (ArgumentException) { return ""; }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxNameChars)]
        public string Description;

        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanAvailableNetwork
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxNameChars)]
        public string ProfileName;

        public Dot11Ssid Dot11Ssid;
        public int Dot11BssType;
        public uint NumberOfBssids;
        public bool NetworkConnectable;           // BOOL：4 字节，不能用 I1
        public int NotConnectableReason;
        public uint NumberOfPhyTypes;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = MaxPhyTypes)]
        public uint[] PhyTypes;

        public bool MorePhyTypes;
        public uint SignalQuality;                // 0-100 的百分比（不是 dBm）
        public bool SecurityEnabled;
        public uint DefaultAuthAlgorithm;
        public uint DefaultCipherAlgorithm;
        public uint Flags;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WlanConnectionParameters
    {
        public int ConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Profile;
        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public int Dot11BssType;
        public uint Flags;
    }

    [DllImport("wlanapi.dll")]
    public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint WlanGetAvailableNetworkList(
        IntPtr clientHandle, ref Guid interfaceGuid, uint flags, IntPtr reserved, out IntPtr networkList);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanScan(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr ssid, IntPtr ihvOemData, IntPtr reserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint WlanSetProfile(
        IntPtr clientHandle, ref Guid interfaceGuid, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string profileXml,
        IntPtr nameValuePairs, bool overwrite, IntPtr onexFlags, out uint correlationReason);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    public static extern uint WlanConnect(
        IntPtr clientHandle, ref Guid interfaceGuid, ref WlanConnectionParameters parameters, IntPtr onexFlags);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanDisconnect(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr onexFlags);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr memory);

    /// <summary>
    /// 读 <c>*_LIST</c> 头部（两个 DWORD：条目数 + 当前索引），条目区从头部之后开始。
    /// 不声明成 <c>ByValArray</c> 固定长度是因为变长数组在封送层里更麻烦，直接按 <see cref="Marshal.SizeOf{T}"/> 步进即可。
    /// </summary>
    public static int ListCount(IntPtr list) => list == IntPtr.Zero ? 0 : Marshal.ReadInt32(list);

    public static IntPtr ListEntry<T>(IntPtr list, int index)
        => IntPtr.Add(list, 8 + index * Marshal.SizeOf<T>());

    /// <summary>错误码转人话（只覆盖本程序会碰到的，其余给十六进制原值，不猜）。</summary>
    public static string DescribeError(uint code) => code switch
    {
        Success => "成功",
        5 => "拒绝访问（权限不足）",
        1168 => "没有可用的凭据或配置文件（0x490，找不到对等方时也常归到这一类）",
        0x00094C01 => "受保护网络要求交互式输入凭据",
        0x00094C4D => "WLAN AutoConfig 服务未运行",
        0x00094CD5 => "该网络当前不可连接",
        _ => $"wlanapi 错误 0x{code:X8}",
    };
}
