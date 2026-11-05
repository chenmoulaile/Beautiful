namespace Beautiful.Core.Network;

/// <summary>
/// 无线网卡在场性裁定：把 <c>WlanEnumInterfaces</c> 逐卡 <c>state</c> 折成一个结论。
/// <para>
/// 【为什么不再依赖 <c>WlanQueryInterface(opcode 0)</c>】本机实测 Intel(R) Wi-Fi 6 AX201 对该 opcode
/// 返回 <c>ERROR_NOT_SUPPORTED</c>，"网卡开没开"永远问不出来，控制中心的 Wi-Fi 砖块就退化成
/// 「读不到网卡开关状态」这点不动的空态；雪上加霜的是该 P/Invoke 还少声明了一个出参、把数据大小
/// 当成了指针。而 <c>WlanEnumInterfaces</c> 在同一台机器上一次就给出 <c>state=connected</c>——
/// 同一个数据源既便宜又可靠，判定就该建在它上面。
/// </para>
/// <para>
/// 【档位口径】<c>wlan_interface_state</c>：0 not_ready / 1 connected / 2 ad_hoc_joined /
/// 3 disconnecting / 4 disconnected / 5 associating / 6 discovering / 7 authenticating。
/// <b>0 视为"关或不可用"</b>，<b>1 视为"已连接"</b>，其余（含 2/3/4/5/6/7 这些过渡态）一律"在场未连"。
/// <b>未知档位（不在 0-7 内的数字）也按"在场"处理</b>：卡既然被枚举到了就说明硬件在、射频大概率开着，
/// 把它说成"关"会给用户一个点不动的假开关，比说"在场"更糟。
/// </para>
/// </summary>
public enum WifiAdapterVerdict
{
    /// <summary>一块无线网卡都没有（或枚举失败）：界面要说"这台机器没有无线网卡"，不能说"Wi-Fi 已关闭"。</summary>
    Absent,

    /// <summary>网卡在，但全部处于 not_ready：射频关着 / 被禁用 / 飞行模式。</summary>
    RadioOff,

    /// <summary>网卡在且可用，当前没连上任何网络。</summary>
    Ready,

    /// <summary>至少一块网卡处于 connected。</summary>
    Connected,
}

/// <summary>
/// 裁定结果：<c>ChosenIndex</c> 是"后续操作该打在哪块卡上"的下标，
/// <c>Reason</c> 是可以直接进界面与日志的中文原因。
/// </summary>
public readonly record struct WifiAdapterStatus(WifiAdapterVerdict Verdict, int ChosenIndex, string Reason);

/// <summary>网卡 state → 裁定的纯函数层（<c>Judge</c> 与 <c>Prefer</c> 都无 IO，可脱离网卡单测）。</summary>
public static class WifiAdapterState
{
    /// <summary>wlan_interface_state_not_ready。Core 侧再写一遍数值是有意的：Core 不能引用 App 的 interop。</summary>
    public const int StateNotReady = 0;

    /// <summary>wlan_interface_state_connected。</summary>
    public const int StateConnected = 1;

    /// <summary>除 not_ready 外都算"在场可用"（未知档位同样按在场，见类型注释）。</summary>
    public static bool IsUsable(int state) => state != StateNotReady;

    /// <summary>
    /// 把 (条目数, 逐卡 state) 折成一个裁定。
    /// <para>
    /// 【为什么返回下标】调用方拿着这个下标去 <c>interfaceList</c> 里取 GUID，之后列网络/连接/开关
    /// 都打在那块卡上；<b>RadioOff 也必须给一个合法下标</b>——射频关着时用户唯一能做的就是把它打开，
    /// 那正需要一块卡的 GUID 去 <c>WlanSetInterface</c>。
    /// </para>
    /// <para>
    /// 【多卡取哪一块】优先取第一块 <c>connected</c>（那块才是"正在干活"的卡），其次取第一块非
    /// not_ready 的卡，都没有才退回 0。
    /// </para>
    /// <para>
    /// 【半截数据】条目数与数组长度不一致时按小的算：调用方是从非托管链表逐条读出来的，
    /// 中途读取失败会留下"count 说有 3 块、实际只解出 1 块"的半截数据，越界读会直接崩。
    /// </para>
    /// </summary>
    public static WifiAdapterStatus Judge(int count, IReadOnlyList<int>? states)
    {
        // 条目数与数组长度不一致时按小的算，避免越界（见方法注释）。
        if (states is null || count <= 0 || states.Count == 0)
            return new WifiAdapterStatus(WifiAdapterVerdict.Absent, -1, "这台机器没有无线网卡");
        var n = Math.Min(count, states.Count);

        var firstUsable = -1;
        var connected = -1;
        for (var i = 0; i < n; i++)
        {
            var state = states[i];
            if (state == StateConnected) { connected = i; break; }
            if (firstUsable < 0 && IsUsable(state)) firstUsable = i;
        }

        if (connected >= 0)
            return new WifiAdapterStatus(WifiAdapterVerdict.Connected, connected, "无线网卡已连接");
        if (firstUsable >= 0)
            return new WifiAdapterStatus(WifiAdapterVerdict.Ready, firstUsable, "无线网卡可用，当前未连接");
        return new WifiAdapterStatus(WifiAdapterVerdict.RadioOff, 0, "无线网卡当前不可用（已关闭、被禁用或处于飞行模式）");
    }

    /// <summary>
    /// 多卡时的选择规则（<c>Judge</c> 的 ChosenIndex 单独抽出来，便于把"取第一块可用"钉成一条可测规则）。
    /// </summary>
    public static int Prefer(IReadOnlyList<int>? states)
    {
        if (states is null || states.Count == 0) return -1;
        for (var i = 0; i < states.Count; i++)
            if (states[i] == StateConnected) return i;
        for (var i = 0; i < states.Count; i++)
            if (IsUsable(states[i])) return i;
        return 0;   // 全是 not_ready：仍返回 0，让"把射频打开"这条唯一出路可用
    }

    /// <summary>裁定 → 顶栏/面板用的中文状态短语（不含网络名，名字由调用方拼）。</summary>
    public static string ShortText(WifiAdapterVerdict verdict) => verdict switch
    {
        WifiAdapterVerdict.Connected => "已连接",
        WifiAdapterVerdict.Ready => "已开启，未连接",
        WifiAdapterVerdict.RadioOff => "已关闭",
        _ => "无无线网卡",
    };
}
