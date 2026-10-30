using System;
using System.Runtime.InteropServices;

namespace Beautiful.App.Interop;

/// <summary>
/// 逐应用音量所需的 Core Audio 会话互操作（Phase 7-T5）。
/// <para>
/// 与 <see cref="CoreAudioIds"/> 的端点音量是同一套 Core Audio，只是往下走一层：
/// <c>IMMDevice → IAudioSessionManager2 → IAudioSessionEnumerator → IAudioSessionControl2 + ISimpleAudioVolume</c>。
/// 每个应用的音量就挂在它自己那条会话上，改它不影响系统总音量——这正是"逐应用混音"的定义。
/// </para>
/// <para>
/// 方法顺序即 vtable 顺序（<c>audiopolicy.h</c>）。**继承来的方法必须在本接口里重新声明一遍**：
/// C# 的 ComImport 接口不做 vtable 继承合并，漏声明就会整体错位到别的槽位。
/// 返回 HRESULT 的方法一律 <c>[PreserveSig]</c>：我们要自己判断"这条会话是否已过期"（进程退出后
/// GetProcessId/GetMasterVolume 会返回 AUDCLNT_E_DEVICE_INVALIDATED 之类），不能让它抛异常打断枚举。
/// </para>
/// </summary>
internal static class AudioSessionIds
{
    /// <summary>IID_IAudioSessionManager2 = {77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F}。</summary>
    public const string SessionManager2Iid = "77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F";

    /// <summary>IID_IAudioSessionControl = {F4B1A599-7266-4319-A8CA-E70ACB11E8CD}。</summary>
    public const string SessionControlIid = "F4B1A599-7266-4319-A8CA-E70ACB11E8CD";

    /// <summary>IID_IAudioSessionControl2 = {BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D}。</summary>
    public const string SessionControl2Iid = "BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D";

    /// <summary>IID_ISimpleAudioVolume = {87CE5498-68D6-44E5-9215-6DA47EF883D8}。</summary>
    public const string SimpleAudioVolumeIid = "87CE5498-68D6-44E5-9215-6DA47EF883D8";

    /// <summary>会话状态：Active（正在出声）。</summary>
    public const int SessionStateActive = 1;
}

/// <summary><c>IAudioSessionManager2</c>（audiopolicy.h）：先声明继承自 IAudioSessionManager 的两个方法。</summary>
[ComImport]
[Guid(AudioSessionIds.SessionManager2Iid)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(ref Guid audioSessionGuid, uint streamFlags,
        out IAudioSessionControl sessionControl);

    [PreserveSig]
    int GetSimpleAudioVolume(ref Guid audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr sessionNotification);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr sessionNotification);

    [PreserveSig]
    int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string? sessionId, IntPtr duckNotification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr duckNotification);
}

/// <summary><c>IAudioSessionEnumerator</c>（audiopolicy.h，2 个方法）。</summary>
[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetSession(int index, out IAudioSessionControl session);
}

/// <summary><c>IAudioSessionControl</c>（audiopolicy.h，10 个方法）：只用来 QueryInterface 到 Control2。</summary>
[ComImport]
[Guid(AudioSessionIds.SessionControlIid)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName(out IntPtr name);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string? value, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath(out IntPtr path);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string? value, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr client);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr client);
}

/// <summary>
/// <c>IAudioSessionControl2</c>（audiopolicy.h）：在 Control 的 9 个方法之后追加 5 个。
/// 注意 <c>GetProcessId</c> 是**返回值不是参数**，必须用 <see cref="PreserveSigAttribute"/> 声明成
/// <c>int GetProcessId(out uint)</c>，否则会把进程号当成 HRESULT 抛出来。
/// </summary>
[ComImport]
[Guid(AudioSessionIds.SessionControl2Iid)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName(out IntPtr name);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string? value, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath(out IntPtr path);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string? value, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr client);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr client);

    [PreserveSig]
    int GetSessionIdentifier(out IntPtr identifier);

    [PreserveSig]
    int GetSessionInstanceIdentifier(out IntPtr identifier);

    [PreserveSig]
    int GetProcessId(out uint processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference(bool optOut);
}

/// <summary><c>ISimpleAudioVolume</c>（audiopolicy.h）：单条会话的音量/静音。</summary>
[ComImport]
[Guid(AudioSessionIds.SimpleAudioVolumeIid)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute(bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute(out bool mute);
}
