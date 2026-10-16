namespace Beautiful.Core.Network;

/// <summary>
/// 一个可连接的无线网络条目（ wlanapi 的 <c>WLAN_AVAILABLE_NETWORK</c> 里我们真正用得上的那几列）。
/// </summary>
/// <param name="Ssid">网络名；隐藏网络为空串时由调用方给占位名。</param>
/// <param name="SignalQuality">0-100 的信号质量（wlanapi 直接给百分比，不是 dBm）。</param>
/// <param name="Connected">当前是否正连着它。</param>
/// <param name="SecurityLabel">给人看的加密方式（<see cref="WifiSecurity.Describe"/> 的结果）。</param>
/// <param name="Joinable">能不能由本程序直接连（企业级/证书类一律 false，不假装能弹密码框解决）。</param>
/// <param name="NotJoinableReason">置灰原因，直接进 ToolTip——置灰必须说清为什么，不给死控件。</param>
public readonly record struct WifiNetwork(
    string Ssid,
    int SignalQuality,
    bool Connected,
    string SecurityLabel,
    bool Joinable,
    string? NotJoinableReason);

/// <summary>
/// WiFi 信号与列表排序规则（纯函数，可单测）。
/// </summary>
public static class WifiSignal
{
    /// <summary>信号格数 0-4（面板画 4 根柱子，<see cref="WifiSecurity"/> 的"开放"另画锁形图标）。</summary>
    /// <remarks>
    /// 阈值不是照抄某个系统：wlanapi 的 <c>wlanSignalQuality</c> 是 0-100 的线性百分比，
    /// 等分成 4 档最直观；边界取"整段落在同一档"的十位数，便于口算核对。
    /// </remarks>
    public static int Bars(int signalQuality) => signalQuality switch
    {
        >= 100 => 4,
        >= 75 => 3,
        >= 50 => 2,
        >= 25 => 1,
        _ => 0,
    };

    /// <summary>
    /// 面板顺序：已连接置顶 → 信号强在前 → 名字字典序（忽略大小写）收尾。
    /// 稳定排序，因此同名多信道条目的相对次序由调用方保证。
    /// </summary>
    public static List<WifiNetwork> Order(IEnumerable<WifiNetwork> networks) => networks
        .OrderByDescending(n => n.Connected)
        .ThenByDescending(n => n.SignalQuality)
        .ThenBy(n => n.Ssid, StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// 802.11 认证/加密枚举 → 人话标签，以及"本程序能不能直接把它连上"的判定。
/// </summary>
/// <remarks>
/// 取值以 <c>wlantypes.h</c> 的 <c>DOT11_AUTH_ALGORITHM</c> / <c>DOT11_ENCRYPTION_ALGORITHM</c> 为准。
/// 判定刻意保守：企业级（802.1X / 证书）与未知组合一律不可直连，交给系统设置——
/// 弹一个密码框去要企业凭据是"看起来能用、实际连不上"的死控件，本项目不放这种东西。
/// </remarks>
public static class WifiSecurity
{
    // DOT11_AUTH_ALGORITHM
    public const int Auth80211Open = 1;
    public const int Auth80211Shared = 2;
    public const int AuthWpa = 3;
    public const int AuthWpa2 = 4;
    public const int AuthWpa3 = 6;
    public const int AuthFcuWpaWpa2 = 5;    // FT 802.1X
    public const int AuthFcuWpa2Psk = 7;
    public const int AuthWpa3Enterprise = 8;
    public const int AuthWpa3EnterpriseSuiteB = 9;
    public const int AuthWpa3EnterpriseSubscrCred = 10;

    // DOT11_ENCRYPTION_ALGORITHM（位标志）
    public const int EncWep40 = 1;
    public const int EncWep104 = 2;
    public const int EncTkip = 4;
    public const int EncCcmp128 = 8;
    public const int EncWep = 16;
    public const int EncCcmp256 = 32;
    public const int EncBip384 = 128;       // WPA3-GCMP/管理帧保护位（此处按 BIP-384 取）

    /// <summary>给人看的加密方式名。</summary>
    public static string Describe(int authAlgorithm, int encryptionAlgorithm)
    {
        var enc = encryptionAlgorithm switch
        {
            EncCcmp128 => "AES",
            EncCcmp256 => "AES-GCMP256",
            EncTkip => "TKIP",
            EncWep40 or EncWep104 or EncWep => "WEP",
            EncBip384 => "BIP",
            _ => "",
        };
        var baseName = authAlgorithm switch
        {
            Auth80211Open => "开放",
            Auth80211Shared => "共享密钥",
            AuthWpa => "WPA",
            AuthWpa2 => "WPA2",
            AuthWpa3 => "WPA3",
            AuthFcuWpaWpa2 => "WPA 企业级",
            AuthFcuWpa2Psk => "WPA2-FT",
            AuthWpa3Enterprise or AuthWpa3EnterpriseSuiteB or AuthWpa3EnterpriseSubscrCred => "WPA3 企业级",
            _ => "未知",
        };
        if (baseName is "开放" or "未知") return baseName;
        return enc.Length == 0 ? baseName : $"{baseName}-{enc}";
    }

    /// <summary>
    /// 是否可由本程序直接连接：开放网络、或 WPA/WPA2/WPA3 个人版（口令式，能生成 profile XML）。
    /// </summary>
    public static bool CanJoinDirectly(int authAlgorithm, out string? reasonIfNot)
    {
        switch (authAlgorithm)
        {
            case Auth80211Open:
            case Auth80211Shared:
            case AuthWpa:
            case AuthWpa2:
            case AuthWpa3:
            case AuthFcuWpa2Psk:
                reasonIfNot = null;
                return true;
            case AuthWpa3Enterprise:
            case AuthWpa3EnterpriseSuiteB:
            case AuthWpa3EnterpriseSubscrCred:
            case AuthFcuWpaWpa2:
                reasonIfNot = "企业级网络需要域账号或证书，请到系统设置里连接";
                return false;
            default:
                reasonIfNot = "加密方式未知，为避免连错不放口令框";
                return false;
        }
    }

    /// <summary>连接它要不要口令（开放网络不要，别弹一个空密码框）。</summary>
    public static bool NeedsPassphrase(int authAlgorithm) => authAlgorithm != Auth80211Open;
}
