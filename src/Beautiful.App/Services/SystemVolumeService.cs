using System.Runtime.InteropServices;
using Beautiful.App.Interop;
using Beautiful.Core.Controls;

namespace Beautiful.App.Services;

/// <summary>
/// 系统音量（默认播放端点的 <c>IAudioEndpointVolume</c>）门面，供控制中心的音量砖块使用（v3 T7）。
/// <para>
/// 【不可用就如实说，绝不假装】没有音频服务 / 没有默认端点 / 端点被禁用 / COM 组件未注册 ——
/// 这些情况下 <see cref="IsAvailable"/> 为 false、<see cref="UnavailableReason"/> 给出中文原因，
/// 界面把整条音量行置灰。读数的任何一次失败都返回 false 而<b>不返回一个数</b>：
/// 用户看到"0%"并拖它，比看到"这一项现在不能用"更糟（前者会让人以为声音坏了）。
/// </para>
/// <para>
/// 【生命周期】跟随宿主弹层：控制中心每次打开新建一个、关闭即 <see cref="Dispose"/>。
/// 不做成静态常驻有两个理由：① 端点可能拔耳机就换一块，常驻对象要挂通知回调才能跟得上，
/// 而瞬态表面在打开那一刻读一次就是"最新状态"（正是控制中心要的语义）；
/// ② 本仓库被"半构造对象挂在静态事件上"咬过多次，RCW 同理——不留下没人负责释放的 COM 引用。
/// </para>
/// </summary>
public sealed class SystemVolumeService : IDisposable
{
    /// <summary>
    /// 音频自检降级开关：<c>BEAUTIFUL_NOAUDIO=1</c> 时本类恒为"不可用"，控制中心的音量行显示置灰 + 原因。
    /// 与 <see cref="ScreenBlurBackdrop.DisabledByEnvironment"/> 同一套"环境变量做 QA 开关"的做法。
    /// </summary>
    public static bool DisabledByEnvironment { get; } =
        Environment.GetEnvironmentVariable("BEAUTIFUL_NOAUDIO") == "1";

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioEndpointVolume? _volume;
    private bool _disposed;

    /// <summary>音量砖块是否可用（构造成功且拿到端点音量接口）。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>不可用的中文原因（可用时为 null）。直接进砖块的 ToolTip，不做二次加工。</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>当前默认播放端点的友好名（面板抬头用）。取不到 = null，不编。</summary>
    public string? DeviceName { get; private set; }

    /// <summary>
    /// 读端点的 <c>PKEY_Device_FriendlyName</c>。属性仓库打开或取值失败一律回 null——
    /// 抬头少一行设备名不影响控制音量，编一个名字才会。
    /// </summary>
    private static string? ReadFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(CoreAudioProps.StgmRead, out var store);
            store.GetValue(in CoreAudioProps.DeviceFriendlyName, out var value);
            var name = value.ReadString();
            PropVariant.PropVariantClear(ref value);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public SystemVolumeService()
    {
        // 全程 try/catch：这是 UI 线程上"打开面板"路径里的一步，任何一次 COM 失败都只能变成
        // "这一项不可用"，不能变成异常冒到 DispatcherUnhandledException（那会弹模态框并把面板一起带走）。
        if (DisabledByEnvironment)
        {
            // QA 开关（与 BEAUTIFUL_NOBLUR 同一族）：真机上音频端点是好的，没法用正常路径看到"置灰 + 原因"
            // 长什么样。留一个环境变量入口，降级形态就能被截图取证，而不是只在注释里成立。
            IsAvailable = false;
            UnavailableReason = "音频接口已按自检开关关闭";
            Logger.Info("BEAUTIFUL_NOAUDIO=1：音量砖块强制走不可用形态（自检用）");
            return;
        }
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(CoreAudioIds.MMDeviceEnumeratorClsid))
                       ?? throw new NotSupportedException("未注册 MMDeviceEnumerator 组件");
            if (Activator.CreateInstance(type) is not IMMDeviceEnumerator enumerator)
                throw new NotSupportedException("MMDeviceEnumerator 接口查询失败");
            _enumerator = enumerator;

            // eRender + eConsole = "系统音量滑杆所控制的那块端点"。无默认端点时这一步抛 COMException。
            enumerator.GetDefaultAudioEndpoint(CoreAudioIds.DataFlowRender, CoreAudioIds.RoleConsole, out var device);
            _device = device;
            DeviceName = ReadFriendlyName(device);   // 读不到就是 null：面板宁可不显示这行，也不编一个设备名

            var iid = new Guid(CoreAudioIds.AudioEndpointVolumeIid);
            device.Activate(ref iid, CoreAudioIds.CtxServer, IntPtr.Zero, out var raw);
            if (raw is not IAudioEndpointVolume volume)
                throw new NotSupportedException("端点不支持 IAudioEndpointVolume");
            _volume = volume;

            IsAvailable = true;
            Logger.Info($"系统音量就绪：标量={ReadScalarOrNull():0.###}");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"音频接口不可用（{ex.Message}）";
            Logger.Warn($"系统音量不可用：{ex.GetType().Name} {ex.Message}");
            ReleaseComObjects(); // 半截成功的部分也要归还，不能留下无主的 RCW
        }
    }

    /// <summary>
    /// 读当前音量：标量（0..1）与静音旗标。任何一步失败返回 false，<paramref name="scalar"/>/<paramref name="muted"/>
    /// 落回 0/false（调用方见到 false 必须置灰，不得拿这两个值当读数显示）。
    /// </summary>
    public bool TryGetVolume(out double scalar, out bool muted)
    {
        scalar = 0; muted = false;
        if (!IsAvailable || _volume is null || _disposed) return false;
        try
        {
            _volume.GetMasterVolumeLevelScalar(out var level);
            _volume.GetMute(out var isMuted);
            scalar = level;   // 越界/非有限值由 VolumeMath 的换算兜住（那里是显示的单一入口）
            muted = isMuted;
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取系统音量失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>写主音量（内部过 <see cref="VolumeMath.ToScalar"/> 的钳制口径，界面只给整数百分比）。</summary>
    public bool TrySetPercent(int percent)
    {
        if (!IsAvailable || _volume is null || _disposed) return false;
        try
        {
            _volume.SetMasterVolumeLevelScalar((float)VolumeMath.ToScalar(percent), IntPtr.Zero);
            Logger.Info($"系统音量 → {percent}%（标量 {VolumeMath.ToScalar(percent):0.###}）");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"设置系统音量失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>写静音旗标。与音量值正交：静音不改动标量，取消静音即刻回到原值。</summary>
    public bool TrySetMuted(bool muted)
    {
        if (!IsAvailable || _volume is null || _disposed) return false;
        try
        {
            _volume.SetMute(muted, IntPtr.Zero);
            Logger.Info($"系统静音 → {(muted ? "开" : "关")}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"设置系统静音失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 拖滑杆：一次调用做完"设音量 + 必要时解除静音"（规则在 <see cref="VolumeMath.ApplyDrag"/>，有单测）。
    /// 返回假表示端点拒写（拔设备瞬间），调用方保留 UI 现值即可，不重试也不弹错。
    /// </summary>
    public bool ApplySliderPercent(int percent, bool muted, out bool newMuted)
    {
        var (scalar, muteAfter) = VolumeMath.ApplyDrag(percent, muted);
        newMuted = muteAfter;
        // 先解静音再设音量：反过来的话，静音状态下写音量虽然也记账，但用户那一格拖动听不到反馈。
        if (muted && !muteAfter && !TrySetMuted(false)) return false;
        return TrySetPercent(VolumeMath.ToPercent(scalar));
    }

    /// <summary>点静音按钮：返回写入后的静音状态；失败返回 null（调用方回读端点保持真值）。</summary>
    public bool? ToggleMute()
    {
        if (!TryGetVolume(out var scalar, out var muted)) return null;
        var (nextMuted, nextScalar) = VolumeMath.ApplyMuteToggle(scalar, muted);
        if (!TrySetMuted(nextMuted)) return null;
        // 取消静音时若端点本来就是 0，补一个可闻下限，否则"已解除静音"却什么都听不见（见 VolumeMath）。
        if (!nextMuted && VolumeMath.ToPercent(nextScalar) > VolumeMath.ToPercent(scalar))
            TrySetPercent(VolumeMath.ToPercent(nextScalar));
        TryGetVolume(out scalar, out muted);   // 回读端点真值作为界面现值（系统可能自己改写过）
        return muted;
    }

    private double? ReadScalarOrNull()
    {
        if (_volume is null) return null;
        try { _volume.GetMasterVolumeLevelScalar(out var level); return level; }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseComObjects();
    }

    /// <summary>
    /// 归还 RCW。<see cref="Marshal.ReleaseComObject"/> 强制减一次引用（比 <c>FinalRelease</c> 更直接），
    /// 字段一并置空：Dispose 后任何一次误用都落在 <see cref="_disposed"/> 早退上，而不是打到已释放的指针。
    /// </summary>
    private void ReleaseComObjects()
    {
        Release(ref _volume);
        Release(ref _device);
        Release(ref _enumerator);
        IsAvailable = false;
        UnavailableReason ??= "音频接口已释放";
    }

    private static void Release<T>(ref T? field) where T : class
    {
        if (field is null) return;
        try { Marshal.ReleaseComObject(field); }
        catch (Exception ex) { Logger.Warn($"释放音频 COM 对象失败（进程退出会回收，仅记录）: {ex.Message}"); }
        field = null;
    }
}
