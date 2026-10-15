namespace Beautiful.Core.Controls;

/// <summary>
/// 控制中心音量砖块的纯换算层（v3 T7）。
/// <para>
/// 【为什么只走 scalar，不碰 dB】Core Audio 的 <c>IAudioEndpointVolume</c> 同时给"标量 0..1"和
/// "dB 电平"两套刻度，而 Windows 自己的音量滑杆（托盘/设置/混音器滑杆）读写的是<b>标量</b>。
/// 我们若改用 dB，就得再引入一次 dB→标量的映射假设（各端点 <c>GetVolumeRange</c> 的下限不同，
/// 且对数/线性之争在文档里说法不一），换回来的只有"和系统滑杆差一格"的取整分歧。
/// 因此这里只承认一件事：<b>百分比 = 标量 × 100</b>，与系统滑杆 1:1 对齐；dB 只在混音器窗口里
/// 由系统自己显示，我们不复制那条路，也不伪造它。
/// </para>
/// <para>
/// 【为什么这层要放 Core】COM interop 本身在 CI/单测里跑不了（要真端点），但它两侧的规则——
/// 越界钳制、非有限值处理、静音与拖动的相互作用——全是纯函数，钉在单测里比钉在注释里可靠。
/// </para>
/// </summary>
public static class VolumeMath
{
    /// <summary>标量下限（0 = 无声）。</summary>
    public const double MinScalar = 0.0;

    /// <summary>标量上限（1 = 端点最大值；Windows 的 1.0 不等于"增益 &gt;100%"，端点已归一）。</summary>
    public const double MaxScalar = 1.0;

    /// <summary>百分比上限。</summary>
    public const int MaxPercent = 100;

    /// <summary>
    /// 标量 → 整数百分比：四舍五入（远离零），钳到 0..100。
    /// <b>NaN / 无穷 / 负数一律读成 0</b>——端点异常时界面宁可显示"没有声音"，也绝不显示一个
    /// 编造出来的音量（那会让用户以为拖动它能改变什么）。
    /// </summary>
    public static int ToPercent(double scalar)
    {
        if (!double.IsFinite(scalar) || scalar <= MinScalar) return 0;
        if (scalar >= MaxScalar) return MaxPercent;
        return (int)Math.Round(scalar * MaxPercent, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 整数百分比 → 标量（滑杆写回系统）：钳到 0..100 后除以 100。
    /// 100 走 <see cref="MaxScalar"/> 直返，避免 <c>100/100.0</c> 之外的任何浮点路径把满量程写成
    /// 0.9999999（端点上表现为"音量条到头了但读数还差一格"）。
    /// </summary>
    public static double ToScalar(int percent)
    {
        if (percent <= 0) return MinScalar;
        if (percent >= MaxPercent) return MaxScalar;
        return percent / (double)MaxPercent;
    }

    /// <summary>
    /// 拖动滑杆的结果（静音与音量是<b>两个正交状态</b>，见 <see cref="SliderPercent"/> 的取舍说明）：
    /// 拖到大于 0 的位置即顺带解除静音（用户拖动就是在要求"我能听见"），拖到 0 则保留静音状态不动
    /// ——把一个已经静音的端点"设成 0 并取消静音"会立刻产生一次可闻的咔哒/系统提示音，得不偿失。
    /// </summary>
    public static (double Scalar, bool Muted) ApplyDrag(int percent, bool muted)
    {
        var scalar = ToScalar(percent);
        return (scalar, percent > 0 ? false : muted);
    }

    /// <summary>
    /// 滑杆读数：恒等于真实标量的百分比，<b>静音不改滑杆位置</b>。
    /// 理由（与系统混音器 sndvol 同规则，而不是与 Win11 快速设置同规则）：静音若把滑杆压到 0，
    /// 用户随手抖一格就会被解释成"设成 1% 并解除静音"，原来 45% 的设置当场丢失；
    /// 滑杆保持真值时静音只是一个可逆的旗标，取消静音即刻回到原音量。
    /// </summary>
    public static int SliderPercent(double scalar) => ToPercent(scalar);

    /// <summary>砖块是否"真的能听见"（滑杆有值且未静音）；供状态文字与单测钉住边界。</summary>
    public static bool IsAudible(double scalar, bool muted) => !muted && ToPercent(scalar) > 0;

    /// <summary>
    /// 静音按钮的下一个状态。<b>唯一的边界修正</b>：在"滑杆为 0"的端点上取消静音毫无意义
    /// （听不见却显示未静音，读数与观感自相矛盾），所以取消静音时给一个可闻的下限
    /// （<see cref="UnmuteFloorPercent"/>%），让"取消静音 = 能听见"这条用户预期成立。
    /// </summary>
    public static (bool Muted, double Scalar) ApplyMuteToggle(double scalar, bool muted)
    {
        if (muted) return (false, Math.Max(scalar, ToScalar(UnmuteFloorPercent)));
        return (true, scalar);
    }

    /// <summary>取消静音后的最低可闻百分比（1% —— 足以证明"有声音了"，又不会突然很大声）。</summary>
    public const int UnmuteFloorPercent = 1;
}
