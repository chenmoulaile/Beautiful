using System.Runtime.InteropServices;

namespace Beautiful.App.Interop;

// 手写的最小 Core Audio（端点音量）interop —— 不引第三方包。
//
// 【为什么手写】控制中心的音量砖块必须真的动系统音量，唯一的正路是 Core Audio 的
// IAudioEndpointVolume。可选路径只有三条：
//   ① NuGet（NAudio/CSCore 一类）——本仓库 src 侧零包引用，为一个滑杆拖进整个音频库不值得；
//   ② shell 启动 sndvol.exe——那是"打开一个窗口"，不是控制中心要的"拖动即改音量"；
//   ③ 手写最小 interop（本文件）——只用 4 个槽，代价是一段必须逐槽对齐 vtable 的声明。
// GUID 与槽序全部取自本机 Windows SDK 头文件（10.0.26100.0：mmdeviceapi.h / endpointvolume.h），
// 不是抄来的口诀：C# 的 COM interop 按**声明顺序**排槽，抄错一个顺序就会在运行期把
// "设音量"发到"注册回调"上，而且多半不报错、只是什么都没发生。
//
// 【未使用的槽为什么也要声明】InterfaceIsIUnknown 的接口里，声明顺序就是槽号。
// 想调用第 7 槽，就必须把前面 6 个槽都占住。这些声明从不被调用，参数按 IntPtr 占位即可；
// 一旦有人真的想调用某个占位方法，先把它换成头文件里的真实签名（注释里已标明槽名）。

/// <summary>
/// <c>mmdeviceapi.h</c> 的对象标识常量。CLSID/IID 均以 SDK 头文件为准。
/// </summary>
internal static class CoreAudioIds
{
    /// <summary>CLSID_MMDeviceEnumerator = {BCDE0395-E52F-467C-8E3D-C4579291692E}。</summary>
    public const string MMDeviceEnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";

    /// <summary>IID_IMMDeviceEnumerator = {A95664D2-9614-4F35-A746-DE8DB63617E6}。</summary>
    public const string ImmDeviceEnumeratorIid = "A95664D2-9614-4F35-A746-DE8DB63617E6";

    /// <summary>IID_IAudioEndpointVolume = {5CDF2C82-841E-4546-9722-0CF74078229A}。</summary>
    public const string AudioEndpointVolumeIid = "5CDF2C82-841E-4546-9722-0CF74078229A";

    /// <summary>eRender：播放端点（我们只关心"听得见的那一侧"）。</summary>
    public const int DataFlowRender = 0;

    /// <summary>eConsole：控制台角色（系统音量滑杆用的就是这一个端点）。</summary>
    public const int RoleConsole = 0;

    /// <summary>CLCTX：进程内 + 本地服务，与 <c>CoCreateInstance</c> 的常用组合一致。</summary>
    public const int CtxServer = 1 | 2 | 4;
}

/// <summary><c>IMMDeviceEnumerator</c>（mmdeviceapi.h 第 756 行起，5 个方法，顺序即槽号）。</summary>
[ComImport, Guid(CoreAudioIds.ImmDeviceEnumeratorIid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    /// <summary>槽 1 EnumAudioEndpoints（未使用）。</summary>
    void EnumAudioEndpoints(int dataFlow, int stateMask, IntPtr deviceCollection);

    /// <summary>槽 2 GetDefaultAudioEndpoint：取指定流/角色的默认端点。失败抛 COMException（无音频设备时即如此）。</summary>
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

    /// <summary>槽 3 GetDevice（未使用）。</summary>
    void GetDevice(IntPtr deviceId, IntPtr device);

    /// <summary>槽 4 RegisterEndpointNotificationCallback（未使用：面板是瞬态表面，不挂端点变更回调）。</summary>
    void RegisterEndpointNotificationCallback(IntPtr client);

    /// <summary>槽 5 UnregisterEndpointNotificationCallback（未使用）。</summary>
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

/// <summary><c>IMMDevice</c>（mmdeviceapi.h 第 426 行起，4 个方法）。</summary>
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    /// <summary>槽 1 Activate：把端点对象激活成目标接口（这里只要 IAudioEndpointVolume）。</summary>
    void Activate(ref Guid interfaceId, int classContext, IntPtr activationParams,
                  [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    /// <summary>槽 2 OpenPropertyStore（未使用）。</summary>
    void OpenPropertyStore(int accessFlags, IntPtr propertyStore);

    /// <summary>槽 3 GetId（未使用）。</summary>
    void GetId(IntPtr deviceId);

    /// <summary>槽 4 GetState（未使用：默认端点取到了就说明它是活的）。</summary>
    void GetState(out int state);
}

/// <summary>
/// <c>IAudioEndpointVolume</c>（endpointvolume.h 第 207 行起，18 个方法）。
/// 只有 4 个槽被真正调用：<see cref="GetMasterVolumeLevelScalar"/>、
/// <see cref="SetMasterVolumeLevelScalar"/>、<see cref="GetMute"/>、<see cref="SetMute"/>，
/// 其余按 SDK 头文件的顺序占位（见文件头的 vtable 说明）。
/// </summary>
[ComImport, Guid(CoreAudioIds.AudioEndpointVolumeIid), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    /// <summary>槽 1 RegisterControlChangeNotify（未使用：面板失活即关，不需要音量变更推送）。</summary>
    void RegisterControlChangeNotify(IntPtr callback);

    /// <summary>槽 2 UnregisterControlChangeNotify（未使用）。</summary>
    void UnregisterControlChangeNotify(IntPtr callback);

    /// <summary>槽 3 GetChannelCount（未使用）。</summary>
    void GetChannelCount(out uint channelCount);

    /// <summary>槽 4 SetMasterVolumeLevel（dB，未使用：见 <see cref="Core.Controls.VolumeMath"/> 的"只用标量"裁定）。</summary>
    void SetMasterVolumeLevel(float levelDb, IntPtr eventContext);

    /// <summary>槽 5 SetMasterVolumeLevelScalar：写主音量（0..1）。<paramref name="eventContext"/> 传 NULL
    /// 即"不是某个具体会话发起的变更"，系统混音器与 SMTC 都会照常跟随。</summary>
    void SetMasterVolumeLevelScalar(float level, IntPtr eventContext);

    /// <summary>槽 6 GetMasterVolumeLevel（dB，未使用）。</summary>
    void GetMasterVolumeLevel(out float levelDb);

    /// <summary>槽 7 GetMasterVolumeLevelScalar：读主音量（0..1）。</summary>
    void GetMasterVolumeLevelScalar(out float level);

    /// <summary>槽 8 SetChannelVolumeLevel（未使用）。</summary>
    void SetChannelVolumeLevel(uint channel, float levelDb, IntPtr eventContext);

    /// <summary>槽 9 SetChannelVolumeLevelScalar（未使用：主音量已覆盖左右声道）。</summary>
    void SetChannelVolumeLevelScalar(uint channel, float level, IntPtr eventContext);

    /// <summary>槽 10 GetChannelVolumeLevel（未使用）。</summary>
    void GetChannelVolumeLevel(uint channel, out float levelDb);

    /// <summary>槽 11 GetChannelVolumeLevelScalar（未使用）。</summary>
    void GetChannelVolumeLevelScalar(uint channel, out float level);

    /// <summary>槽 12 SetMute：写静音旗标（与音量值是**两个正交状态**，静音不动标量）。</summary>
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr eventContext);

    /// <summary>槽 13 GetMute：读静音旗标。</summary>
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

    /// <summary>槽 14 GetVolumeStepInfo（未使用：那是键盘音量键的步进语义）。</summary>
    void GetVolumeStepInfo(out uint step, out uint stepCount);

    /// <summary>槽 15 VolumeStepUp（未使用）。</summary>
    void VolumeStepUp(IntPtr eventContext);

    /// <summary>槽 16 VolumeStepDown（未使用）。</summary>
    void VolumeStepDown(IntPtr eventContext);

    /// <summary>槽 17 QueryHardwareSupport（未使用）。</summary>
    void QueryHardwareSupport(out uint hardwareSupportMask);

    /// <summary>槽 18 GetVolumeRange（未使用：只用标量就不需要 dB 上下限）。</summary>
    void GetVolumeRange(out float minDb, out float maxDb, out float stepDb);
}
